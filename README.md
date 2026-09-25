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

在「录制与编辑」中设置快捷键（默认 F10）及目标窗口的完整名称。设置好后，切到该窗口按一次快捷键开始录制，再按一次结束；不需要按开始或结束按钮。名称须与当前前台窗口完整匹配（不区分大小写），名称为空或窗口不符时不会开始录制。切离目标窗口会自动暂停，暂停后按快捷键会结束本次录制，不会恢复录制或改换目标。

录制时，画面会实时显示每次按键按下、重复按下、松开，以及各动作之前的等待毫秒数，包括开始录制到第一个动作的等待。左右修饰键与功能键分别记录，只排除当前控制快捷键及程序注入的输入；超过 60 秒的步骤等待也会保留，最大为 2147483647 毫秒。

结束非空录制后会立即弹出名称窗口。输入 1–100 个字符的脚本名称并确认后，保存脚本并进入「执行脚本」页面；没有录到步骤时不建立空脚本，也不要求命名。名称无效或保存失败会保留步骤和名称窗口，可以修正后重试；放弃录制需要另行确认。再次开始录制会建立另一份脚本，保留上一次已保存的录制。在录制页面检查已命名脚本、删除误操作、修改等待毫秒数，或手动添加与编辑文字步骤，修改会自动保存。页面不需要「新建脚本」「重新载入」或手动保存按钮。

进入「执行脚本」后，左侧会列出所有已保存脚本，可选取、切换和删除；录制页面不显示脚本库。每个脚本保存在 `%LOCALAPPDATA%\FlowKey\scripts\<脚本编号>.json`；旧版的 `script.json` 会自动迁移。

在「执行脚本」中选择脚本，再选择只执行一次、指定次数或持续执行。后两种方式可设置每轮间隔（100–60000 毫秒）；指定次数范围为 1–10000。执行方式、次数、间隔与快捷键的修改会自动保存，不需要「保存设置」。切到符合该脚本目标程序与窗口内容区尺寸的窗口，按快捷键即可确认目标并执行；也可点击「开始执行」进入等待，再切到符合要求的目标窗口自动启动。再按一次快捷键可停止。执行页面会列出全部步骤，持续高亮当前执行步骤，并显示当前动作、轮次及轮次间隔等待状态；步骤在执行页面仅供查看。

持续执行会一直重复，直到手动停止或目标窗口失焦。可选择 F8、F9、F10 或 F11；F12 是系统保留键，不在桌面版中提供。

录制敏感输入前请先结束录制；文字步骤以明文写入本地脚本。每次只操作一个前台目标窗口，支持固定步骤和普通权限程序。窗口失焦、关闭或尺寸变化时会暂停录制或停止执行。

## 验证

```powershell
dotnet run --project FlowKey.Core.Tests/FlowKey.Core.Tests.csproj
dotnet run --project FlowKey.Desktop.Tests/FlowKey.Desktop.Tests.csproj
dotnet build FlowKey.Desktop/FlowKey.Desktop.csproj
node --test
node --check desktop.js
```

若 Windows 阻止测试窗口取得前台焦点，可先在 PowerShell 执行 `$env:FLOWKEY_E2E_MANUAL_FOCUS = '1'`，再运行桌面测试，并在 45 秒内点击出现的「FlowKey E2E target」输入区。测试结束后用 `Remove-Item Env:FLOWKEY_E2E_MANUAL_FOCUS` 清除此选项。测试会实际操作快捷键、窗口、命名窗口、保存与回放；键盘录制事件使用模拟物理事件的录制回调数据，不能替代对实际键盘操作的验收。

产品要求与技术方案分别见 [产品设计文档](docs/PRODUCT_DESIGN.md) 和 [技术方案](docs/TECHNICAL_DESIGN.md)。
