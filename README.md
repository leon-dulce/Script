# FlowKey

FlowKey 是 Windows 指定窗口脚本录制工具。桌面版使用 .NET 10、WPF、WebView2 和 Win32 API；根目录的 `index.html` 仍可在浏览器中打开，作为不控制电脑的界面演示。

## 运行桌面版

准备 .NET 10 SDK 和 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。在 Windows PowerShell 中执行：

```powershell
dotnet run --project FlowKey.Desktop/FlowKey.Desktop.csproj
```

也可以制作无需另装 .NET Runtime 的便携版（仍需 WebView2 Runtime）：

```powershell
dotnet publish FlowKey.Desktop/FlowKey.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist/FlowKey
```

在工具中刷新并选择当前可见的目标窗口，点击「开始录制」，再切回目标窗口。录制会在目标窗口回到前台后开始。切离目标窗口会自动暂停；切回后点击「继续录制」。按设定的全局快捷键（默认 F10）可结束录制。在工具中检查步骤、删除误操作、修改等待毫秒数，或手动添加与编辑文字步骤，然后保存。

脚本保存在 `%LOCALAPPDATA%\FlowKey\script.json`。重新启动后载入脚本，重新选择同一程序且内容区尺寸相同的窗口。让目标窗口位于前台，按 F10 执行；再次按 F10 停止。也可选择 F8、F9 或 F11。F12 是系统保留键，不在桌面版中提供。

录制敏感输入前请暂停；文字步骤以明文写入本地脚本。第一版只支持一个前台窗口、固定步骤和普通权限程序。窗口失焦、关闭或尺寸变化时会暂停录制或停止执行。

## 验证

```powershell
dotnet run --project FlowKey.Core.Tests/FlowKey.Core.Tests.csproj
dotnet build FlowKey.Desktop/FlowKey.Desktop.csproj
node --test
node --check desktop.js
```

产品要求与技术方案分别见 [产品设计文档](docs/PRODUCT_DESIGN.md) 和 [技术方案](docs/TECHNICAL_DESIGN.md)。
