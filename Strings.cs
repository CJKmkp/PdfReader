using System.Globalization;

namespace PdfReader
{
    /// <summary>
    /// 插件内置中英双语文案。按 <see cref="CultureInfo.CurrentUICulture"/> 惰性求值，
    /// 与宿主 Settings.Appearance.Language 设置的进程 UI 文化保持一致。
    /// 不使用字典 + Get(key) 形式，避免出现取不到的死分支。
    /// </summary>
    internal static class Strings
    {
        private static bool IsEnglish =>
            !CultureInfo.CurrentUICulture.Name.StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);

        // 插件与工具栏
        public static string PluginName => IsEnglish ? "PDF Reader" : "PDF 阅读器";
        public static string ToolbarButton => IsEnglish ? "PDF" : "PDF";
        public static string ToolbarDescription => IsEnglish
            ? "Load a PDF as the canvas background and annotate it directly"
            : "把 PDF 加载为画布背景，直接在上面批注";

        // 命令
        public static string Open => IsEnglish ? "Open" : "打开";
        public static string Close => IsEnglish ? "Close PDF" : "关闭 PDF";
        public static string PrevPage => IsEnglish ? "Previous page" : "上一页";
        public static string NextPage => IsEnglish ? "Next page" : "下一页";
        public static string Export => IsEnglish ? "Export with ink" : "导出（含墨迹）";
        public static string DoublePage => IsEnglish ? "Two pages" : "双页";
        public static string SinglePage => IsEnglish ? "Single page" : "单页";
        public static string ContinuousScroll => IsEnglish ? "Continuous" : "连续滚动";
        public static string ResetZoom => IsEnglish ? "Reset zoom" : "重置缩放";
        public static string ZoomFormat => IsEnglish ? "Zoom: {0}%" : "缩放：{0}%";

        // 状态与提示
        public static string Loading => IsEnglish ? "Loading…" : "正在加载…";
        public static string Exporting => IsEnglish ? "Exporting…" : "正在导出…";
        public static string PageOfFormat => IsEnglish ? "Page {0} / {1}" : "第 {0} / {1} 页";
        public static string OpenedFormat => IsEnglish
            ? "Loaded {0} ({1} pages). Annotate directly on the canvas."
            : "已加载 {0}（共 {1} 页）。可直接在画布上批注。";
        public static string ClosedNotice => IsEnglish
            ? "The PDF background has been removed."
            : "已移除 PDF 背景。";
        public static string ExportDoneFormat => IsEnglish
            ? "Exported with ink: {0}"
            : "已导出（含墨迹）：{0}";

        // 错误
        public static string ErrorTitle => IsEnglish ? "PDF Reader" : "PDF 阅读器";
        public static string ErrorFileNotFound => IsEnglish
            ? "The file does not exist or cannot be accessed."
            : "文件不存在或无法访问。";
        public static string ErrorPasswordProtected => IsEnglish
            ? "This PDF is password protected and cannot be opened."
            : "该 PDF 受密码保护，无法打开。";
        public static string ErrorNotPdf => IsEnglish
            ? "Not a valid PDF file, or the file is damaged."
            : "不是有效的 PDF 文件，或文件已损坏。";
        public static string ErrorRenderFailedFormat => IsEnglish
            ? "Failed to render page {0}."
            : "渲染第 {0} 页失败。";
        public static string ErrorOpenFailedFormat => IsEnglish
            ? "Failed to open the document: {0}"
            : "打开文档失败：{0}";
        public static string ErrorExportFailedFormat => IsEnglish
            ? "Export failed: {0}"
            : "导出失败：{0}";
        public static string ErrorNoDocument => IsEnglish
            ? "No PDF is loaded yet."
            : "尚未加载 PDF。";
        public static string ErrorNoWinRtPdf => IsEnglish
            ? "The system PDF component (Windows.Data.Pdf) is unavailable, so this plugin cannot render documents. Windows 10 1809 or later is required."
            : "系统 PDF 组件（Windows.Data.Pdf）不可用，插件无法渲染文档。需要 Windows 10 1809 或更高版本。";
        public static string ErrorNoComposition => IsEnglish
            ? "This host build does not provide the canvas composition service, so the PDF cannot be used as a canvas background."
            : "当前宿主版本未提供画布合成服务，无法把 PDF 作为画布背景使用。";
        public static string UnavailableSuffixFormat => IsEnglish ? " ({0})" : "（{0}）";

        // 文件对话框
        public static string DialogTitle => IsEnglish ? "Open PDF" : "打开 PDF";
        public static string DialogFilter => IsEnglish
            ? "PDF documents (*.pdf)|*.pdf|All files (*.*)|*.*"
            : "PDF 文档 (*.pdf)|*.pdf|所有文件 (*.*)|*.*";
        public static string ExportDialogTitle => IsEnglish ? "Export PDF with ink" : "导出含墨迹的 PDF";
        public static string ExportDialogFilter => IsEnglish
            ? "PDF documents (*.pdf)|*.pdf"
            : "PDF 文档 (*.pdf)|*.pdf";
        public static string ExportSuffix => IsEnglish ? "-annotated" : "-批注";

        // 设置页
        public static string SettingsHeader => IsEnglish ? "PDF Reader" : "PDF 阅读器";
        public static string SettingsIntro => IsEnglish
            ? "Add the PDF button from the toolbar component library, then click it to load a PDF as the canvas background. Ink is drawn with the host's own tools and is remembered per page; export writes the pages together with the ink into a new PDF."
            : "在工具栏组件库中添加「PDF」按钮，点击即可把 PDF 加载为画布背景。批注使用宿主自带的墨迹工具，按页自动记忆；导出会把页面与墨迹一起写入新的 PDF。";
        public static string SettingsRenderScale => IsEnglish ? "Render scale" : "渲染倍率";
        public static string QualityPerformance => IsEnglish ? "Performance" : "性能";
        public static string QualityBalanced => IsEnglish ? "Balanced" : "均衡";
        public static string QualityQuality => IsEnglish ? "Quality" : "质量";
        public static string QualityWarning => IsEnglish
            ? "Quality mode renders pages at full resolution for the 8× maximum zoom. Memory usage is very high (a single page can take 100 MB – 1 GB). Consider raising the page cache budget; page turns on large documents will be slower."
            : "质量模式会按最大缩放（8×）所需的完整分辨率渲染页面，内存占用很大（单页可达 100MB – 1GB）。建议调大页面缓存上限，大文档翻页会变慢。";
        public static string SettingsRememberLast => IsEnglish
            ? "Reload the last document and page"
            : "重新打开时恢复上次的文档与页码";
        public static string SettingsCacheBudget => IsEnglish ? "Page cache budget (MB)" : "页面缓存上限（MB）";
        public static string SettingsShortcutsHeader => IsEnglish ? "Usage" : "使用说明";
        public static string SettingsShortcuts => IsEnglish
            ? "Click the toolbar PDF button to open a document.\nUse the popup's page buttons or the mouse wheel to turn pages; ink is saved per page automatically.\nPinch with two fingers, or hold Ctrl and scroll the mouse wheel, to zoom the page; drag with two fingers to pan.\nExport writes the whole document, from the first page to the last."
            : "点击工具栏「PDF」按钮打开文档。\n用弹窗里的翻页按钮或鼠标滚轮翻页，墨迹按页自动保存。\n双指捏合或按住 Ctrl + 滚轮可缩放页面，双指拖动可平移；缩放后可点弹窗里的「重置缩放」复原。\n导出会写出完整文档，从第一页到最后一页。";
        public static string SettingsNotesHeader => IsEnglish ? "Notes" : "注意事项";
        public static string SettingsNotes => IsEnglish
            ? "• The PDF sits below the host ink canvas, so all existing pen tools work on it.\n• Turning pages swaps the ink: each page keeps its own strokes.\n• Zoom keeps the ink glued to the page; the reset button restores 100%.\n• Encrypted PDFs are not supported.\n• A higher render scale looks sharper but uses more memory."
            : "• PDF 位于宿主墨迹画布下方，因此所有既有笔工具都能直接用。\n• 翻页会切换墨迹：每页各自保留自己的笔迹。\n• 缩放时墨迹始终贴合页面，重置按钮可恢复到 100%。\n• 不支持加密 PDF。\n• 渲染倍率越高越清晰，但内存占用也越大。";

        // 设置页 · 文件关联
        public static string AssocSectionHeader => IsEnglish ? "Open PDF with Ink Canvas" : "用 ICC 打开 .pdf";
        public static string AssocStatusRegisteredFormat => IsEnglish
            ? "Registered: double-clicking a .pdf opens it in Ink Canvas ({0})."
            : "已关联：双击 .pdf 会用 ICC 打开（{0}）。";
        public static string AssocStatusUnregistered => IsEnglish
            ? "Not registered: double-clicking a .pdf keeps the current behavior."
            : "未关联：双击 .pdf 保持当前行为。";
        public static string AssocStatusUnavailable => IsEnglish
            ? "File association is not supported by this host build."
            : "当前宿主版本不支持文件关联。";
        public static string AssocRegister => IsEnglish ? "Register" : "注册关联";
        public static string AssocUnregister => IsEnglish ? "Unregister" : "注销关联";
        public static string AssocRegisterDesc => IsEnglish
            ? "Associate the .pdf extension (current user only, no admin rights needed). Double-clicking a PDF then opens it in Ink Canvas."
            : "为 .pdf 扩展名建立关联（仅当前用户，无需管理员权限）。此后双击 PDF 会用 ICC 打开。";
        public static string AssocUnregisterDesc => IsEnglish
            ? "Remove the .pdf association registered by this plugin."
            : "移除本插件注册的 .pdf 关联。";
        public static string AssocDescription => IsEnglish ? "PDF Document (Ink Canvas)" : "PDF 文档（ICC）";
        public static string AssocRegistered => IsEnglish
            ? "PDF association registered. Double-click a .pdf to open it in Ink Canvas now."
            : "已注册 .pdf 关联，现在双击 PDF 即可用 ICC 打开。";
        public static string AssocUnregistered => IsEnglish
            ? "PDF association removed."
            : "已注销 .pdf 关联。";
        public static string AssocRegisterFailed => IsEnglish
            ? "Failed to register the .pdf association."
            : "注册 .pdf 关联失败。";
        public static string AssocUnregisterFailed => IsEnglish
            ? "Failed to remove the .pdf association."
            : "注销 .pdf 关联失败。";
        public static string AssocUnavailable => IsEnglish
            ? "File association is not supported by this host build."
            : "当前宿主版本不支持文件关联。";
    }
}
