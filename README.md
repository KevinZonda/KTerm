# KevinZonda.KTerm

KevinZonda.KTerm 是一个面向 Windows 的最小化 Terminal Emulator MVP。原生宿主使用 .NET 10 WinForms 和 WebView2；终端前端使用 xterm.js/WebGL；每个 Pane 都连接独立的 ConPTY 和 Shell 进程。终端会话优先使用随应用分发的 passthrough ConPTY（`OpenConsole.exe`，来自 Windows Terminal，MIT），让 DECSTBM 等 VT 序列原样到达前端；该文件缺失时自动回退到系统 inbox conhost。

## 当前功能

- 多 Tab，每个 Tab 保存独立的递归分屏布局。
- 同一窗口支持左右、上下拆分以及 2×2 多终端。
- 每个 Pane 独立运行 PowerShell、PowerShell 7 或 CMD。
- WebGL 渲染失败时自动回退。
- 拖动分隔线和窗口 resize 会同步更新 ConPTY 行列数。
- 支持终端选择、`Ctrl+Shift+C` 复制与 `Ctrl+Shift+V` 粘贴。
- codex 等 inline TUI 的历史通过 DECSTBM region scroll 进入终端 scrollback，滚轮可直接查看（依赖 passthrough ConPTY）。
- vim、less 等 alternate screen 应用中，滚轮自动转为方向键（alternate scroll）。
- 新 Tab / 分屏按当前 Pane 尺寸创建 ConPTY，减少全屏 TUI 的二次重绘。
- 关闭 Pane、Tab 或应用时回收相应 Shell、ConPTY 和 Win32 handle。

## 快捷键

| 快捷键 | 操作 |
| --- | --- |
| `Alt+T` | 新建 Tab |
| `Alt+\` | 将聚焦 Pane 拆成左右两列 |
| `Alt+-` | 将聚焦 Pane 拆成上下两行 |
| `Ctrl+Shift+C` | 复制终端选择 |
| `Ctrl+Shift+V` | 粘贴到聚焦终端 |

快捷键只在 KTerm 位于前台时生效。

## 构建和运行

源代码构建需要：

- Windows 10 1903 或更高版本
- .NET 10 SDK
- Node.js 与 pnpm
- Microsoft Edge WebView2 Evergreen Runtime
- （可选）`tools/openconsole/OpenConsole.exe`：passthrough ConPTY 主机，构建时嵌入程序集，首次运行释放到 `%LOCALAPPDATA%\KTerm\bin`；缺失或释放失败时自动回退系统 conhost

```powershell
dotnet build KevinZonda.KTerm.slnx
dotnet run --project src\KevinZonda.KTerm\KevinZonda.KTerm.csproj
```

`.csproj` 会执行前端的 `pnpm install --frozen-lockfile`（首次）和 `pnpm run build`，随后把 Vite 产物嵌入应用程序集。

运行 Debug 端到端 smoke test（自动创建两个 Tab，并在活动 Tab 构造 2×2）：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\smoke.ps1
```

发布启用 ReadyToRun 的 framework-dependent win-x64 版本：

```powershell
dotnet publish src\KevinZonda.KTerm\KevinZonda.KTerm.csproj -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true
```

详细架构、消息协议与验收标准参见 [docs/plan.md](docs/plan.md)。
