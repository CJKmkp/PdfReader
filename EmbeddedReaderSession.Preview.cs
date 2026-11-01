using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Ink_Canvas.Plugins;
using PdfReader.Views;

namespace PdfReader
{
    internal sealed partial class EmbeddedReaderSession
    {
        private void EnablePresentationPreview(PresentationSourceDescriptor descriptor)
        {
            // Additive SDK capability: keep the plugin loadable on older hosts.
            var render = typeof(PresentationSourceDescriptor).GetProperty("RenderPreviewAsync");
            var select = typeof(PresentationSourceDescriptor).GetProperty("SelectPageAsync");
            if (render == null || select == null) return;
            render.SetValue(descriptor, new Func<int, CancellationToken, Task<BitmapSource>>(RenderPreviewAsync));
            select.SetValue(descriptor, new Func<int, CancellationToken, Task<int>>(SelectPreviewPageAsync));
            descriptor.AllowPageNumberClick = true;
        }

        internal async Task<BitmapSource> RenderPreviewAsync(int pageNumber, CancellationToken token)
        {
            var document = _document;
            if (document == null || pageNumber < 1 || pageNumber > document.PageCount) return null;
            var size = document.GetPageSize(pageNumber - 1);
            int width = Math.Max(1, (int)Math.Min(240, 200 * size.Width / Math.Max(1, size.Height)));
            try
            {
                var bitmap = await document.RenderAsync(pageNumber - 1, width, token).ConfigureAwait(false);
                return ReferenceEquals(document, _document) ? bitmap : null;
            }
            catch (ObjectDisposedException) when (!ReferenceEquals(document, _document)) { return null; }
        }

        internal async Task<int> SelectPreviewPageAsync(int pageNumber, CancellationToken token)
        {
            var document = _document;
            if (document == null || pageNumber < 1 || pageNumber > document.PageCount) return 0;
            if (_displayMode == PdfDisplayMode.ContinuousScroll)
                await ScrollToPageTopAsync(pageNumber - 1, token, jump: true).ConfigureAwait(false);
            else
                await GoToPageAsync(pageNumber - 1, token).ConfigureAwait(false);
            return ReferenceEquals(document, _document) ? CurrentPage + 1 : 0;
        }
    }
}
