using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using PdfReader.Views;

namespace PdfReader
{
    internal sealed partial class EmbeddedReaderSession
    {
        private readonly SemaphoreSlim _stripRenderGate = new(1, 1);
        private CancellationTokenSource _stripRenderCts;
        private int _stripRenderVersion;
        private string _stripRenderKey;
        private PdfDocumentSession _stripRenderDocument;
        private Task _stripRenderTask = Task.CompletedTask;
        private Task _stripVisibleTask = Task.CompletedTask;

        private void CancelStripRendering()
        {
            Interlocked.Increment(ref _stripRenderVersion);
            var previous = Interlocked.Exchange(ref _stripRenderCts, null);
            if (previous != null) { previous.Cancel(); previous.Dispose(); }
            _stripRenderKey = null; _stripRenderDocument = null;
        }

        private void QueueStripRender() => _ = RenderStripSafelyAsync();

        private async Task RenderStripSafelyAsync()
        {
            try { if (!_modeSwitching) await RenderStripViewportAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _logError?.Invoke("更新 PDF 可见页失败", ex); }
        }

        private Task RenderStripViewportAsync(CancellationToken cancellationToken, bool waitForVisibleOnly = false)
        {
            var view = _backgroundView;
            if (view == null || _displayMode != PdfDisplayMode.ContinuousScroll || !IsOpen || _disposed)
                return Task.CompletedTask;
            if (!view.Dispatcher.CheckAccess())
                return view.Dispatcher.InvokeAsync(() => RenderStripViewportAsync(cancellationToken, waitForVisibleOnly)).Task.Unwrap();

            if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            var document = _document; var cache = _cache;
            if (document == null) return Task.CompletedTask;
            var visible = view.GetStripVisiblePageRects().Select(p => (int)p.PageIndex)
                .Where(page => page >= 0 && page < document.PageCount).ToArray();
            var pages = new List<int>(visible.Length + 2);
            pages.AddRange(visible.Length == 0 ? new[] { CurrentPage } : visible);
            int first = pages.Min(), last = pages.Max();
            if (last + 1 < document.PageCount) pages.Add(last + 1);
            if (first > 0) pages.Add(first - 1);

            // Cached bitmaps and image sources are both bounded. In particular,
            // quality mode must not allocate a gigabyte for each offscreen page.
            var visibleSet = visible.ToHashSet();
            if (visibleSet.Count == 0) visibleSet.Add(CurrentPage);
            int neighbors = pages.Count - visibleSet.Count;
            var widths = pages.Select(page =>
            {
                // Reserve most of the budget for pages on screen; prefetch must not
                // force the visible page to remain blurry after zooming.
                double share = visibleSet.Contains(page)
                    ? (neighbors == 0 ? 1.0 : .8) / visibleSet.Count
                    : .2 / Math.Max(1, neighbors);
                long pixelsPerPage = Math.Max(65536, (long)(_config.CacheBudgetBytes * share / 4));
                var size = document.GetPageSize(page);
                int budgetWidth = (int)Math.Max(1, Math.Sqrt(pixelsPerPage * size.Width / Math.Max(1, size.Height)));
                int requested = Math.Min(ComputeRenderWidth(document, page, 1), budgetWidth);
                return requested < ZoomModel.WidthBucket ? Math.Max(1, requested) : requested / ZoomModel.WidthBucket * ZoomModel.WidthBucket;
            }).ToArray();
            string key = string.Join(";", pages.Select((page, index) => page + ":" + widths[index]));
            if (ReferenceEquals(document, _stripRenderDocument) && key == _stripRenderKey)
                return waitForVisibleOnly ? _stripVisibleTask : _stripRenderTask;

            CancelStripRendering();
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = source.Token;
            _stripRenderCts = source;
            int version = Interlocked.Increment(ref _stripRenderVersion);
            _stripRenderDocument = document; _stripRenderKey = key;
            view.RetainStripSources(pages.ToHashSet());
            var visibleReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stripVisibleTask = visibleReady.Task;
            _stripRenderTask = RenderStripWindowAsync(document, cache, view, pages, widths, visibleSet.Count, visibleReady, version, token);
            return waitForVisibleOnly ? _stripVisibleTask : _stripRenderTask;
        }

        private async Task RenderStripWindowAsync(PdfDocumentSession document, PageRenderCache cache, PdfBackgroundView view,
            IReadOnlyList<int> pages, IReadOnlyList<int> widths, int visibleCount,
            TaskCompletionSource visibleReady, int version, CancellationToken token)
        {
            bool entered = false;
            try
            {
                await _stripRenderGate.WaitAsync(token).ConfigureAwait(false); entered = true;
                // First paint only missing visible pages at bounded resolution.
                // Existing images remain on screen while sharper renders arrive.
                for (int i = 0; i < visibleCount; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int page = pages[i];
                    bool hasImage = await view.Dispatcher.InvokeAsync(() => view.GetStripPageImage(page)?.Source != null);
                    if (hasImage) continue;
                    int previewWidth = Math.Min(768, widths[i]);
                    if (!cache.TryGet(page, widths[i] / ZoomModel.WidthBucket, out var bitmap)
                        && !cache.TryGet(page, previewWidth / ZoomModel.WidthBucket, out bitmap))
                    {
                        bitmap = await document.RenderAsync(page, previewWidth, token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        cache.Put(page, previewWidth / ZoomModel.WidthBucket, bitmap);
                    }
                    await PublishStripPageAsync(document, view, page, bitmap, version, token).ConfigureAwait(false);
                }
                visibleReady.TrySetResult();
                for (int i = 0; i < pages.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    int page = pages[i], bucket = widths[i] / ZoomModel.WidthBucket;
                    BitmapSource bitmap;
                    if (!cache.TryGet(page, bucket, out bitmap))
                    {
                        bitmap = await document.RenderAsync(page, widths[i], token).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        cache.Put(page, bucket, bitmap);
                    }
                    await PublishStripPageAsync(document, view, page, bitmap, version, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                if (version == Volatile.Read(ref _stripRenderVersion)) _stripRenderKey = null;
                if (!token.IsCancellationRequested) _logError?.Invoke("渲染 PDF 可见页失败", ex);
            }
            finally { visibleReady.TrySetResult(); if (entered) _stripRenderGate.Release(); }
        }

        private Task PublishStripPageAsync(PdfDocumentSession document, PdfBackgroundView view, int page,
            BitmapSource bitmap, int version, CancellationToken token) => view.Dispatcher.InvokeAsync(() =>
        {
            if (token.IsCancellationRequested || version != Volatile.Read(ref _stripRenderVersion)
                || !ReferenceEquals(document, _document) || !ReferenceEquals(view, _backgroundView)
                || _displayMode != PdfDisplayMode.ContinuousScroll) return;
            view.SetStripPageSource(page, bitmap);
        }).Task;

    }
}
