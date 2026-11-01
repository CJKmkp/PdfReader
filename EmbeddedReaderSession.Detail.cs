using System;
using System.Threading;
using System.Threading.Tasks;

namespace PdfReader
{
    internal sealed partial class EmbeddedReaderSession
    {
        private readonly SemaphoreSlim _detailGate = new(1, 1);
        private CancellationTokenSource _detailCts;
        private Task _detailTask = Task.CompletedTask;
        private int _detailVersion;
        private string _detailKey;

        private void CancelViewportDetail()
        {
            var view = _backgroundView;
            if (view != null && !view.Dispatcher.CheckAccess()) { view.Dispatcher.Invoke(CancelViewportDetail); return; }
            _detailVersion++;
            var previous = _detailCts; _detailCts = null; _detailKey = null;
            if (previous != null) { previous.Cancel(); previous.Dispose(); }
            view?.ClearDetailTiles();
        }

        private void QueueViewportDetailRender(bool immediate = false)
        {
            var view = _backgroundView;
            if (view == null) return;
            if (!view.Dispatcher.CheckAccess())
            { view.Dispatcher.BeginInvoke(new Action(() => QueueViewportDetailRender(immediate))); return; }
            if (!IsOpen || _disposed || _modeSwitching || !view.IsVisible || CurrentScale <= 1.001)
            { CancelViewportDetail(); return; }
            var document = _document;
            var pages = _displayMode == Views.PdfDisplayMode.ContinuousScroll
                ? view.GetStripVisiblePageRects() : view.GetPagerVisiblePageRects(CurrentPage);
            long budget = Math.Clamp(_config.CacheBudgetBytes / 2, 4L << 20, 64L << 20);
            var plan = ViewportRenderPlan.Create(pages, _viewMatrix, new(view.ActualWidth, view.ActualHeight), _dpiScale, budget);
            string key = $"{view.ScrollOffset:R}|{_viewMatrix}|" + string.Join(";", plan);
            if (key == _detailKey && !(_detailTask.IsCompleted && view.DetailTileCount == 0 && plan.Count > 0)) return;
            CancelViewportDetail();
            _detailKey = key;
            var source = new CancellationTokenSource(); _detailCts = source;
            int version = _detailVersion;
            _detailTask = RenderAsync(source.Token);

            async Task RenderAsync(CancellationToken token)
            {
                bool entered = false;
                try
                {
                    if (!immediate) await Task.Delay(60, token).ConfigureAwait(false);
                    await _detailGate.WaitAsync(token).ConfigureAwait(false); entered = true;
                    foreach (var tile in plan)
                    {
                        token.ThrowIfCancellationRequested();
                        var bitmap = await document.RenderRegionAsync(tile.Page, tile.Source, tile.PixelWidth, tile.PixelHeight, token).ConfigureAwait(false);
                        await view.Dispatcher.InvokeAsync(() =>
                        {
                            if (!token.IsCancellationRequested && version == _detailVersion
                                && ReferenceEquals(document, _document) && ReferenceEquals(view, _backgroundView))
                                view.SetDetailTile(tile, bitmap);
                        });
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                { if (!token.IsCancellationRequested) _logError?.Invoke("精化 PDF 可见区域失败", ex); }
                finally { if (entered) _detailGate.Release(); }
            }
        }
    }
}
