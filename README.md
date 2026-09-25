# FlowKey

## 新版介面與圖示

正式版採用 TailAdmin 風格的淺色卡片布局與繁體中文介面。錄製頁集中顯示快捷鍵、開始／停止、動作數及總時長；執行頁包含腳本庫、即時步驟與執行控制；設定入口位於本地腳本庫提示上方。

標題使用內建的 Noto Serif TC，操作文字使用 Windows 正黑體，無需連線下載字型。字型授權隨程式內建，原文見 `assets/OFL-NotoSerifTC.txt`。`assets/flowkey.svg` 為共用標誌來源，`assets/generate_icon.py` 使用 Pillow 產生九種尺寸的 Windows 圖示，供執行檔與視窗使用。

`FlowKey-UI-Demo.html` 是介面預覽，請與 `branding.css` 及 `assets` 資料夾一起保留；正式使用仍請雙擊最上層的 `FlowKey.exe`。

FlowKey 是 Windows 全域键盘脚本录制工具。桌面版使用 .NET 10、WPF、WebView2 和 Win32 API；根目录的 `index.html` 仍可在浏览器中打开，作为不控制电脑的界面演示。

**要启动程序，请双击本资料夹最上层的 [FlowKey.exe](FlowKey.exe)。这就是最新的 Windows 执行档。** `dist/archive` 保存旧版与测试产物，`dist/FlowKey-single` 是建置用目录。

## 运行桌面版

Windows x64 用户双击资料夹最上层的 `FlowKey.exe`，在 Windows 管理员授权窗口选择允许后启动；取消授权则不会启动。它内含 .NET Runtime 和界面资源，不需要与其他项目档案放在一起；电脑仍需安装 [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。

从源码运行需准备 .NET 10 SDK。在 Windows PowerShell 中执行：

```powershell
dotnet run --project FlowKey.Desktop/FlowKey.Desktop.csproj
```

制作独立执行档：

```powershell
dotnet publish FlowKey.Desktop/FlowKey.Desktop.csproj -c Release -o dist/FlowKey-single
Copy-Item dist/FlowKey-single/FlowKey.Desktop.exe FlowKey.exe
```

在「录制与编辑」中设置快捷键（默认 F10），按快捷键或点击「开始录制」即可开始。无需指定目标窗口，切换窗口、改变窗口尺寸或关闭原窗口都会继续记录键盘动作。再按快捷键或点击「停止录制」结束录制。

录制时，画面会实时显示每个按键首次按下与松开，以及各动作之前的等待毫秒数，包括开始录制到第一个动作的等待。左右修饰键与功能键分别记录，只排除当前控制快捷键及FlowKey 自己回放的输入；超过 60 秒的步骤等待也会保留，最大为 2147483647 毫秒。

结束非空录制后会立即弹出名称窗口。输入 1–100 个字符的脚本名称并确认后，保存脚本并进入「执行脚本」页面；没有录到步骤时不建立空脚本，也不要求命名。名称无效或保存失败会保留步骤和名称窗口，可以修正后重试；放弃录制需要另行确认。再次开始录制会建立另一份脚本，保留上一次已保存的录制。在录制页面检查已命名脚本、删除误操作、修改等待毫秒数，或编辑旧脚本已有的文字步骤；不提供添加文字步骤，修改会自动保存。页面不需要「新建脚本」「重新载入」或手动保存按钮。

进入「执行脚本」后，左侧会列出所有已保存脚本，可选取、切换和删除；录制页面不显示脚本库。每个脚本保存在 `%LOCALAPPDATA%\FlowKey\scripts\<脚本编号>.json`；旧版的 `script.json` 会自动迁移。

在「执行脚本」中选择脚本，再选择只执行一次、指定次数或持续执行。后两种方式可设置每轮间隔（100–60000 毫秒）；指定次数范围为 1–10000。执行方式、次数、间隔与快捷键的修改会自动保存，不需要「保存设置」。新录制的键盘脚本可切到任意普通目标窗口；旧脚本仍须符合保存的目标程序与窗口尺寸。按快捷键即可确认目标并执行；也可点击「开始执行」进入等待，再切到符合要求的目标窗口自动启动。再按一次快捷键可停止。执行页面会列出全部步骤，持续高亮当前执行步骤，并显示当前动作、轮次及轮次间隔等待状态；步骤在执行页面仅供查看。

持续执行会一直重复，直到手动停止或目标窗口失焦。可选择 F8、F9、F10 或 F11；F12 是系统保留键，不在桌面版中提供。

FlowKey 默认请求管理员权限，以支持同为管理员权限的目标程序。用户已确认以管理员模式运行后，貓貓 TMS 可以正常录制。源码测试宿主仍可在普通权限运行，因此保留权限差异提醒与管理员重启入口。

录制敏感输入前请先结束录制；文字步骤以明文写入本地脚本。录制可跨普通权限窗口，执行时只操作一个前台目标窗口。执行中窗口失焦、关闭或尺寸变化时停止执行。

## 设置执行流程

左侧底部、本地脚本库提示上方的「设置」→「执行时的窗口切换」，调整后自动保存，适用于所有脚本。

- 开始：选择「我来切换窗口」，或「自动切到指定窗口」。自动模式需先选择已打开的目标窗口，可用「刷新窗口」更新列表。
- 结束：选择「回到 FlowKey」或「保持当前窗口」。自然完成、手动停止及异常停止均按此设置处理；持续执行只在停止后处理。
- 流程预览会显示当前组合。默认保留手动切换及结束后保持当前窗口。
- 设置保存在本地 settings.json。重新打开目标程序后按程序与完整窗口名称匹配；窗口关闭、名称变化或重名时要求重新选择。自动切换会等待窗口稳定就绪，最长等待 5 秒；窗口未开启或找不到时明确显示窗口名称并提示先开启或重新选择。Windows 拒绝自动切换时不发送按键。

## 验证

```powershell
dotnet run --project FlowKey.Core.Tests/FlowKey.Core.Tests.csproj
dotnet run --project FlowKey.Desktop.Tests/FlowKey.Desktop.Tests.csproj
dotnet build FlowKey.Desktop/FlowKey.Desktop.csproj
node --test
node --check desktop.js
./tests/Verify-Published.ps1 -Executable dist/FlowKey-single/FlowKey.Desktop.exe
```

若 Windows 阻止测试窗口取得前台焦点，可先在 PowerShell 执行 `$env:FLOWKEY_E2E_MANUAL_FOCUS = '1'`，再运行桌面测试，并在 120 秒内点击出现的「FlowKey E2E target」输入区。测试结束后用 `Remove-Item Env:FLOWKEY_E2E_MANUAL_FOCUS` 清除此选项。测试会实际操作快捷键、窗口、命名窗口、保存与回放；键盘录制测试从独立进程通过 Windows SendInput 发送按键，检查真实监听、实时步骤、按住期间重复过滤及回放；游戏硬件按键另以人工验收确认。

产品要求与技术方案分别见 [产品设计文档](docs/PRODUCT_DESIGN.md) 和 [技术方案](docs/TECHNICAL_DESIGN.md)。
