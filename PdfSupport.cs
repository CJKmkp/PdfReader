using System;
using System.Runtime.CompilerServices;

namespace PdfReader
{
    /// <summary>
    /// WinRT PDF 组件（Windows.Data.Pdf）可用性探测。
    /// 宿主使用 Costura 把 WinRT 投影程序集嵌入 exe，插件在独立 AssemblyLoadContext 中加载，
    /// 因此不能假设 Windows.Data.Pdf 一定能解析成功；这里在初始化阶段一次性探测并缓存结果，
    /// 失败时由调用方禁用入口并给出本地化原因，而不是等到点击时抛异常。
    /// </summary>
    internal static class PdfSupport
    {
        private static readonly object Gate = new object();
        private static bool _probed;
        private static bool _available;
        private static string _reason;

        /// <summary>系统 PDF 渲染是否可用。首次访问会触发探测。</summary>
        public static bool IsAvailable
        {
            get
            {
                Probe();
                return _available;
            }
        }

        /// <summary>不可用时的技术原因（异常消息），可用时为 null。</summary>
        public static string UnavailableReason
        {
            get
            {
                Probe();
                return _reason;
            }
        }

        /// <summary>
        /// 执行探测（幂等）。返回是否可用。
        /// </summary>
        public static bool Probe()
        {
            lock (Gate)
            {
                if (_probed) return _available;
                _probed = true;
                try
                {
                    _available = TouchWinRtPdfTypes();
                    _reason = _available ? null : "type resolution returned false";
                }
                catch (Exception ex)
                {
                    _available = false;
                    _reason = ex.GetType().Name + ": " + ex.Message;
                }
                return _available;
            }
        }

        /// <summary>
        /// 单独的方法，确保 JIT 解析 Windows.Data.Pdf 类型的时机被外层 try/catch 覆盖。
        /// 不可内联，否则类型解析可能提前到调用方 JIT 阶段而绕过异常捕获。
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TouchWinRtPdfTypes()
        {
            // 只做类型元数据触碰，不创建任何 WinRT 实例，代价极低。
            var docType = typeof(global::Windows.Data.Pdf.PdfDocument);
            var optType = typeof(global::Windows.Data.Pdf.PdfPageRenderOptions);
            var streamType = typeof(global::Windows.Storage.Streams.InMemoryRandomAccessStream);
            return docType != null && optType != null && streamType != null;
        }
    }
}
