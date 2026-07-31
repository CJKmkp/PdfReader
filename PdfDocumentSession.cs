using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PdfReader
{
    /// <summary>
    /// 一个打开的 PDF 文档会话。文档只加载一次，页面渲染通过信号量串行化，
    /// 避免同一个 PdfDocument 同时访问时触发 WinRT 组件的线程安全问题。
    /// </summary>
    internal sealed class PdfDocumentSession : IDisposable
    {
        private readonly PdfDocument _document;

        /// <summary>只保护 GetPage：同一个 PdfDocument 上的 WinRT 调用不保证线程安全。</summary>
        private readonly SemaphoreSlim _pageGate = new SemaphoreSlim(1, 1);
        private int _disposed;

        private PdfDocumentSession(PdfDocument document, string path)
        {
            _document = document;
            FilePath = path;
        }

        public string FilePath { get; }
        public uint PageCount => _document?.PageCount ?? 0;
        public bool IsPasswordProtected => _document?.IsPasswordProtected ?? false;

        /// <summary>打开一个 PDF 并保留 PdfDocument 实例。</summary>
        public static async Task<PdfDocumentSession> OpenAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                throw new FileNotFoundException(Strings.ErrorFileNotFound, path);

            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(path).AsTask(cancellationToken);
            PdfDocument document = await PdfDocument.LoadFromFileAsync(file).AsTask(cancellationToken);
            if (document.IsPasswordProtected)
            {
                // PdfDocument 未实现 IClosable，没有可释放的句柄，交给 GC 即可。
                throw new InvalidOperationException(Strings.ErrorPasswordProtected);
            }

            return new PdfDocumentSession(document, path);
        }

        /// <summary>读取一页的 PDF 点尺寸。</summary>
        public Windows.Foundation.Size GetPageSize(int pageIndex)
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));

            var page = _document.GetPage((uint)pageIndex);
            try
            {
                return page.Size;
            }
            finally
            {
                (page as IDisposable)?.Dispose();
            }
        }

        /// <summary>
        /// 把页面渲染成已经 Freeze 的 WPF BitmapSource。
        /// pixelWidth 是目标位图宽度；高度由 PDF 页面比例自动决定。
        /// </summary>
        public async Task<BitmapSource> RenderAsync(int pageIndex, int pixelWidth, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (pageIndex < 0 || pageIndex >= PageCount)
                throw new ArgumentOutOfRangeException(nameof(pageIndex));
            if (pixelWidth < 1) pixelWidth = 1;

            // 只有 GetPage 需要串行（同一个 PdfDocument 上的 WinRT 调用不保证线程安全）；
            // 拿到独立的 PdfPage 后，渲染与解码可以多页并发，导出时这一点是主要提速来源。
            PdfPage page;
            await _pageGate.WaitAsync(cancellationToken);
            try
            {
                ThrowIfDisposed();
                cancellationToken.ThrowIfCancellationRequested();
                page = _document.GetPage((uint)pageIndex);
            }
            finally
            {
                _pageGate.Release();
            }

            try
            {
                using (var ras = new InMemoryRandomAccessStream())
                {
                    var options = new PdfPageRenderOptions
                    {
                        DestinationWidth = (uint)Math.Min(pixelWidth, ZoomModel.MaxPixelDimension)
                    };
                    await page.RenderToStreamAsync(ras, options).AsTask(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    ras.Seek(0);

                    using (var netStream = ras.AsStreamForRead())
                    using (var memory = new MemoryStream())
                    {
                        await netStream.CopyToAsync(memory, 81920, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        memory.Position = 0;
                        return await CreateBitmapAsync(memory, cancellationToken);
                    }
                }
            }
            finally
            {
                (page as IDisposable)?.Dispose();
            }
        }

        private static Task<BitmapSource> CreateBitmapAsync(Stream source, CancellationToken cancellationToken)
        {
            // BitmapImage 不要求 UI 线程：OnLoad 会在 EndInit 时把像素读进内存，
            // Freeze 后即可安全跨线程使用。放在调用方线程（渲染任务所在的线程池线程）解码，
            // 避免每页都排队进 Dispatcher —— 导出多页时那会成为主要瓶颈。
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateBitmap(source));
        }

        private static BitmapSource CreateBitmap(Stream source)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = source;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(PdfDocumentSession));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            // PdfDocument 未实现 IClosable，无需显式释放。
            try { _pageGate.Dispose(); } catch { }
        }
    }
}
