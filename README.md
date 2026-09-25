# FlowKey

FlowKey 是 Windows 指定窗口脚本录制工具。桌面版使用 .NET 10、WPF、WebView2 和 Win32 API；根目录的 `index.html` 仍可在浏览器中打开，作为不控制电脑的界面演示。

**要启动程序，请双击本资料夹最上层的 [FlowKey.exe](FlowKey.exe)。这就是最新的 Windows 执行档。** `dist/archive` 保存旧版与测试产物，`dist/FlowKey-single` 是建置用目录。

## 运行桌面版

Windows x64 用户可直接运行资料夹最上层的 `FlowKey.exe`。它内含 .NET Runtime 和界面资源，不需要与其他项目档案放在一起；电脑仍需安装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

从源码运行需准备 .NET 10 SDK。在 Windows PowerShell 中执行：

```powershell
dotnet run --project FlowKey.Desktop/FlowKey.Desktop.csproj
```

制作独立执行档：

```powershell
dotnet publish FlowKey.Desktop/FlowKey.Desktop.csproj -c Release -o dist/FlowKey-single
Copy-Item dist/FlowKey-single/FlowKey.Desktop.exe FlowKey.exe
```

在「录制与编辑」中刷新并选择当前可见的目标窗口，界面会显示「已确认」。新脚本没有步骤时，切回该窗口按设定的全局快捷键（默认 F10）即可开始录制，再按一次结束。也可点击「开始录制」后切回目标窗口。切离目标窗口会自动暂停；切回后点击「继续录制」，或按快捷键恢复。在工具中检查步骤、删除误操作、修改等待毫秒数，或手动添加与编辑文字步骤，然后保存。

「录制与编辑」页面不显示已保存脚本列表；可在此新建、录制和编辑当前脚本。进入「执行脚本」页面后，左侧才会显示脚本库，可选取、切换和删除已保存脚本。每个脚本保存在 `%LOCALAPPDATA%\FlowKey\scripts\<脚本编号>.json`；旧版的 `script.json` 会自动迁移。

在「执行脚本」中选好已保存脚本后，须在同一页面重新确认目标窗口，再选择只执行一次、指定次数或持续执行。后两种方式可设置每轮间隔（100–60000 毫秒）；指定次数范围为 1–10000。每个脚本分别保存执行方式和快捷键。「保存设置」会在此页显示保存结果；点击「开始执行」后切回目标窗口，或直接在目标窗口按快捷键，即按当前方式执行。此页会显示等待窗口和执行进度；再按一次快捷键可停止。持续执行会一直重复，直到手动停止或目标窗口失焦。可选择 F8、F9、F10 或 F11；F12 是系统保留键，不在桌面版中提供。

录制敏感输入前请暂停；文字步骤以明文写入本地脚本。每次只操作一个前台目标窗口，支持固定步骤和普通权限程序。窗口失焦、关闭或尺寸变化时会暂停录制或停止执行。

## 验证

```powershell
dotnet run --project FlowKey.Core.Tests/FlowKey.Core.Tests.csproj
dotnet run --project FlowKey.Desktop.Tests/FlowKey.Desktop.Tests.csproj
dotnet build FlowKey.Desktop/FlowKey.Desktop.csproj
node --test
node --check desktop.js
```

产品要求与技术方案分别见 [产品设计文档](docs/PRODUCT_DESIGN.md) 和 [技术方案](docs/TECHNICAL_DESIGN.md)。
