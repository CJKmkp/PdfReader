# PDF 阅读器插件 (PdfReader)

## 2.3.2 退出与窗口状态

- 退出阅读后切到桌面鼠标模式，不再返回白板；重复关闭不改变工具。
- 阅读期间使用宿主全屏服务，关闭或打开失败时恢复窗口状态。
- 桌面鼠标切换、任务栏与文件关联转发修复需要搭配本次 ICCCE 宿主更新；旧宿主缺少 Mouse 工具时会记录兼容性提示。

## 2.3.1 连续阅读

- 新安装和旧配置默认使用连续滚动；弹窗中仍可切换单页、双页，并记住选择。
- 滚轮按实际增量滚动，支持高精度滚轮；选择模式下单指移动、双指缩放，批注模式下双指移动/缩放整个连续文档。
- 只渲染视口和相邻页，离开视口的位图会释放；按 PDF 原始页面尺寸建立布局，异步加载位图时不改变页面位置。
- 页首、页尾会限制拖动范围；同一页内滚动不再反复保存配置。
- 本轮只调整页面显示和手势路由，墨迹存取与同步沿用原实现。

回归检查（Windows）：`dotnet run --project Tests/PdfReader.Tests.csproj -p:Platform=x64`。

ICC-CE 的嵌入式 PDF 批注插件。把 PDF 渲染成位图注入宿主画布**下方**作为背景，
批注仍由宿主自己的墨迹工具完成——所有既有笔、荧光笔、橡皮擦、时间机器都能直接用。
翻页时宿主按页自动存取墨迹，导出时把「页面 + 墨迹」合成为新的 PDF。

渲染基于系统 **Windows.Data.Pdf**，插件侧不引入任何第三方依赖。

## 工作原理

依赖宿主的 `ICanvasCompositionService`（SDK 接口，宿主实现）：

| 步骤 | 调用 |
| --- | --- |
| 注入背景层 | `InjectBackgroundLayer(factory)` — 元素置于 `InkCanvas` 下方，`IsHitTestVisible = false`，不抢书写事件 |
| 配置分页 | `ConfigurePages(pageCount, currentPage, pageRenderer)` — 交出离屏渲染回调供导出使用 |
| 翻页 | `SetCurrentPageAsync(pageIndex)` — 宿主存回原页墨迹、恢复目标页墨迹 |
| 导出 | `ExportWithInkAsync(outputPath, 0)` — 宿主逐页合成并用 PdfSharp 组装（整个文档） |

页面坐标系即背景元素的 `ActualWidth/ActualHeight`（DIP），墨迹坐标由宿主按
`TransformToVisual` 自动换算，因此双指缩放/平移画布时墨迹与页面内容保持对齐。

缩放/平移把矩阵作用在背景层根节点的 `RenderTransform` 上，宿主对墨迹的按页存取
自动包含该矩阵，墨迹始终锚定页面内容；双指手势由宿主 `Main_Grid_ManipulationDelta`
转发给插件（与视频展台同款机制），复用宿主的编辑模式与原生湿墨取消状态机。

## 特性

- 打开本地 PDF 作为画布背景（全屏铺满，`Stretch=Uniform` 保持比例）
- 上一页 / 下一页，墨迹按页自动切换
- 双指捏合缩放 / 双指平移 / Ctrl+滚轮缩放，墨迹实时跟随并始终锚定页面内容，弹窗可一键重置缩放
- 导出「页面 + 墨迹」为新 PDF（从当前页到末页）
- 可调渲染倍率（1×–4×）与页面缓存上限（32–1024 MB）
- 记忆上次打开的文档与页码
- 渲染取消与有界 LRU 缓存，超大文档保持流畅
- 中英双语界面（跟随宿主 UI 语言）

## 使用

1. 在工具栏组件库中添加「PDF」按钮。
2. 点击按钮打开弹窗 → **打开**，选择 PDF。
3. 直接用宿主的笔工具在页面上批注。
4. 用弹窗里的**上一页 / 下一页**翻页，墨迹自动按页保存。
5. **双指捏合**缩放页面、**双指拖动**平移（触摸屏），或按住 **Ctrl + 鼠标滚轮**缩放（无触摸屏）；缩放后墨迹贴合页面，弹窗显示当前缩放百分比，**重置缩放**可回到 100%。
6. **导出（含墨迹）** 写出完整文件（从第一页到最后一页）；**关闭 PDF** 移除背景层。

弹窗与宿主其它批注面板行为一致：再次点击工具栏按钮或点标题栏关闭按钮收起。

## 构建

默认 Debug x64：

```bash
dotnet build "PdfReader.csproj" -c Debug -p:Platform=x64
```

## 打包

生成 `.icpx`（内含 `manifest.json`、`PdfReader.dll`、`PdfReader.deps.json`）：

```powershell
.\pack.ps1 -Configuration Debug -Platform x64
```

## 本地调试

```powershell
.\build-and-run.ps1 -HostDir "e:\ICC CE\ICC CE main\community\Ink Canvas\bin\Debug\x64\net10.0-windows10.0.19041.0"
```

## 注意事项

- 需要宿主提供 `ICanvasCompositionService`；缺失时按钮会给出本地化提示而不是崩溃。
- 需要 Windows 10 1809 或更高版本（系统 PDF 组件）。插件初始化时探测，不可用时给出原因。
- 不支持加密 PDF。
- 渲染倍率越高越清晰，但内存占用也越大；导出合成倍率由宿主按位图分辨率自动决定（上限 4×）。
- 插件不打包宿主已嵌入的 WinRT 投影程序集（`WinRT.Runtime.dll`、`Microsoft.Windows.SDK.NET.dll`），
  SDK 与 Controls 引用均为 `Private=False`。

## 许可

MIT
