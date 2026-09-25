# FlowKey

目前版本：**1.0.2 Stable**。

Current release: **1.0.2 Stable**.

## 繁體中文

### 專案簡介

FlowKey 是一款適用於 Windows 的本機鍵盤操作錄製與回放工具。使用者可以錄下一段按鍵操作，為腳本命名並儲存在電腦上，之後以全域快捷鍵在指定的前景視窗重播。它適合重複執行固定的鍵盤流程，不需要雲端帳號或網路服務。

### 主要功能

- **錄製鍵盤操作：**記錄按下、長按期間的重複按下、放開，以及事件之間的等待時間。錄製可跨越一般 Windows 視窗。
- **管理本機腳本：**為錄製結果命名、檢查與刪除步驟、調整等待時間，並在腳本庫中選擇或刪除已儲存的腳本。
- **彈性執行：**支援執行一次、指定次數或持續執行；可設定每輪間隔，並以 F8、F9、F10 或 F11 啟動及停止。
- **視窗與結果設定：**可手動切換至目標視窗，或讓程式自動切換至指定視窗；執行結束後可選擇保留目標視窗或返回 FlowKey，並設定提示橫幅、提示音、對話框與邊框。

### 使用方式

1. 在專案根目錄按兩下 `FlowKey.exe`，並允許 Windows 的系統管理員權限提示。首次啟動若缺少 WebView2，程式會自動下載及安裝；遇到問題時會顯示原因與官方下載網址。
2. 在「錄製與編輯」頁面選擇快捷鍵，按下快捷鍵或點選「開始錄製」。
3. 在需要操作的視窗輸入按鍵；完成後再次按下快捷鍵或點選「停止錄製」，為腳本命名並儲存。
4. 前往「執行腳本」，選擇腳本與執行方式，再以快捷鍵或「開始執行」啟動。再次按下快捷鍵可停止執行。

一般使用不需輸入指令，也不需另外安裝 .NET。若需要桌面捷徑，可由使用者自行建立。

腳本儲存在 `%LOCALAPPDATA%\FlowKey\scripts\`。錄製內容可能包含敏感按鍵，請在輸入密碼前停止錄製並檢查腳本。回放只會向前景目標視窗送出輸入；目標失焦、關閉或尺寸改變時會停止。此版本不錄製滑鼠操作，也不提供背景執行。

### 系統需求與建置

正式桌面程式需要 Windows x64，並於缺少 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) 時自動安裝（需網路連線）。執行檔已包含 .NET 執行環境，並預設要求系統管理員權限，以便操作同樣以管理員權限執行的程式。只有從原始碼建置才需要 .NET 10 SDK。

```powershell
dotnet run --project FlowKey.Desktop/FlowKey.Desktop.csproj
dotnet publish FlowKey.Desktop/FlowKey.Desktop.csproj -c Release -o dist/FlowKey-single
```

從原始碼發布後，可執行 `dist/FlowKey-single/FlowKey.Desktop.exe`。根目錄提供可直接按兩下執行的 `FlowKey.exe`。

## English

### Overview

FlowKey is a local Windows application for recording and replaying keyboard actions. It lets users capture a sequence of keys, save it as a named script, and replay it in a selected foreground window with a global shortcut. It is intended for repeatable keyboard workflows and does not require a cloud account or online service.

### Features

- **Keyboard recording:** Captures key presses, repeated key-down events while a key is held, releases, and the time between events across ordinary Windows windows.
- **Local script library:** Name recordings, review or remove steps, edit delays, and select or delete saved scripts.
- **Playback options:** Run a script once, a set number of times, or continuously. Configure the interval between runs and use F8, F9, F10, or F11 to start and stop.
- **Window and completion settings:** Switch to the target window manually or automatically, choose where focus remains after playback, and configure banners, sounds, dialogs, and border notifications.

### Basic workflow

1. Double-click `FlowKey.exe` in the project root and approve the Windows administrator prompt. On first launch, FlowKey downloads and installs WebView2 if needed; it shows an explanation and the official download page if setup fails.
2. On **Record & Edit**, choose a shortcut and start recording with the shortcut or the on-screen button.
3. Type in the window you want to capture. Stop recording, give the script a name, and save it.
4. On **Run Scripts**, select the script and playback mode. Start with the shortcut or the on-screen button; press the shortcut again to stop.

Normal use requires no commands or separate .NET installation. Users can create a desktop shortcut if they want one.

Scripts are stored in `%LOCALAPPDATA%\FlowKey\scripts\`. Recorded keys may contain sensitive input, so stop recording before entering passwords and review saved steps. Playback sends input only to the foreground target window and stops if that window loses focus, closes, or changes size. This version does not record mouse actions or run scripts in the background.

### Requirements and build

The desktop application requires Windows x64. It installs the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) automatically when missing (an internet connection is required). The executable includes the .NET runtime and requests administrator permission by default so it can interact with other elevated programs. Only building from source requires the .NET 10 SDK.

```powershell
dotnet run --project FlowKey.Desktop/FlowKey.Desktop.csproj
dotnet publish FlowKey.Desktop/FlowKey.Desktop.csproj -c Release -o dist/FlowKey-single
```

After publishing from source, run `dist/FlowKey-single/FlowKey.Desktop.exe`. The repository root also provides `FlowKey.exe` for direct launch.

## 開發與驗證 / Development and verification

```powershell
dotnet run --project FlowKey.Core.Tests/FlowKey.Core.Tests.csproj
dotnet run --project FlowKey.Desktop.Tests/FlowKey.Desktop.Tests.csproj
dotnet build FlowKey.Desktop/FlowKey.Desktop.csproj
node --test
node --check desktop.js
./tests/Verify-Published.ps1 -Executable dist/FlowKey-single/FlowKey.Desktop.exe
```

桌面測試會在實際 Windows 視窗驗證錄製、快捷鍵、儲存與回放。設計細節請參閱[產品設計](docs/PRODUCT_DESIGN.md)及[技術設計](docs/TECHNICAL_DESIGN.md)。

Desktop tests exercise recording, shortcuts, saving, and playback in real Windows windows. See the [product design](docs/PRODUCT_DESIGN.md) and [technical design](docs/TECHNICAL_DESIGN.md) for further details.
