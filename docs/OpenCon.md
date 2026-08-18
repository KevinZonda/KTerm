# OpenConsole resize 不同步调查：残影问题的根因

本文记录 2026-08 对"resize 后屏幕残留旧内容（残影）"问题的调查结论。
与 `conhost.md` 的主线问题（scrollback 丢失）同源：都是**客户端本地 buffer
语义与 ConPTY buffer 语义冲突**，那次是 scrollback，这次是 resize。

修复方案另见：`OpenCon.FixA.md`（缓解：settle 后清视口——**已验证为
错误方向并被否决回滚**，见该文件；xterm.js 6.0 的 `\x1b[2J` 只擦视口
不动 scrollback，清视口反而丢内容）、`OpenCon.FixB.md`（终极：自编译
OpenConsole，resize 后合成全屏重绘；对应调查分析中的"方案 C"——
**已落地为 `OpenConsole.Enhanced.exe`**，见该文件第 8 节）。

## 1. 现象

passthrough OpenConsole 上线、并设置 `windowsPty: { backend: 'conpty',
buildNumber: 19045 }` 之后（见 `conhost.md` 第 6 节），大残影消失，但
resize 仍有残留：

- BCE 进度条（`scripts/resize-progress.ps1`）在拖动 resize 后留下纯色
  残块——某个中间几何下的背景填充，此后永不被擦除。
- 目录列表在收窄/拉宽后出现折行碎片（`KevinZonda.T` / `erminal.slnx`）
  与宽布局并存；正在输入的 PSReadLine 命令行（如 `clear`）残留在旧位置。

同一操作在 Windows Terminal 中干净。

## 2. 源码实证（`../terminal`，OpenConsole 与 WT 同树）

### 2.1 resize 时 OpenConsole 不向终端输出任何 VT

signal pipe 只有 4 种消息（`src/host/PtySignalInputThread.hpp:39`）：

```
ShowHideWindow = 1, ClearBuffer = 2, SetParent = 3, ResizeWindow = 8
```

**没有 repaint 类信号。** resize 处理链：

```
_DoResizeWindow            (src/host/PtySignalInputThread.cpp:171)
  └─ ConhostInternalGetSet::ResizeWindow  (src/host/outputStream.cpp:372)
       └─ ResizeWithReflow / ResizeTraditional  (src/host/screenInfo.cpp:1276)
```

全程不碰 VT writer。resize 后终端屏幕上没有任何来自 OpenConsole 的新
内容，只有：(a) 终端自己重排过的旧帧；(b) 应用之后的输出。

### 2.2 但 OpenConsole 会 reflow 自己的 buffer

`screenInfo.cpp:1381`：wrap 开启且非 alt buffer 时走 `ResizeWithReflow`
——收窄时把长行**重新换行**；cursor 溢出视口底部时，viewport 整体下移
（`outputStream.cpp:406-412`）。alt buffer 不 reflow（GH#3493，注释见
`screenInfo.cpp:1373-1380`）。

### 2.3 微软明知会 desync，只补救光标

`screenInfo.cpp:1412` 原注释：

> If we're ConPTY, our copy of the buffer may be out of sync with the
> terminal, because our VT, resize reflow, etc., implementation may be
> different.

resize 后置 `SetConptyCursorPositionMayBeWrong`，之后用 DSR/CPR 反向询问
终端"光标实际在哪"来重新同步（`screenInfo.cpp:1477` 附近）。**只补救
光标位置，不补救屏幕内容。**

### 2.4 新架构的 VT 输出模型：逐 API 直译，无帧生成器

旧 ConPTY（Win10 inbox）靠 VtEngine 渲染器做帧差分重绘；2026 架构把它
删了。现在 VT 输出有两条路径：

- 应用直接写 VT：`WriteCharsVT`（`src/host/_stream.cpp:380`）解析进
  buffer 的同时原始序列逐字转发；
- 应用走 console API（`FillConsoleOutput` 等，PowerShell 进度条就是）：
  每个 API 调用点直接翻译出对应 VT（`src/host/getset.cpp`、
  `src/host/_output.cpp` 里的 `GetVtWriterForBuffer` 调用）。

两条路径都只覆盖"应用造成的变化"。resize 不是应用造成的，所以什么都没
有。

## 3. WT 为什么干净

WT 客户端 resize 时在自己一侧跑**同一份** `TextBuffer::ResizeWithReflow`
代码（client 与 server 同树共享）。两侧 buffer 逐格一致，之后应用的增量
重绘落点吻合。WT 从不指望 ConPTY 重发屏幕——它靠共享算法保持同步。

xterm.js 没有、也不可能有与 conhost 逐格一致的 reflow 实现。

## 4. KTerm 中残影的形成机制

1. 拖动 resize 期间，xterm.js 对每个中间帧立即按自己的语义重排本地
   buffer（`buildNumber: 19045` 下：收窄=截断、拉宽=右侧补空、拉高=
   底部补空行、缩矮=顶行入 scrollback）。
2. 同一时刻 OpenConsole 按 rewrap + viewport 平移重排自己的 buffer，
   不发任何输出。
3. resize 结束后，应用（进度条、PSReadLine、codex）按 OpenConsole 新
   布局的坐标做**增量**重绘，写到 xterm 这块按另一种方式重排过的屏幕
   上。
4. 没被新输出覆盖的格子 = 残影。它们是"错误且永久"的——没有任何后续
   机制会擦它们，直到 `\x1b[2J` / 全屏重绘。

对照截图：

- 青色残块：中间几何时进度条的 BCE 填充；进度条每步只重绘自己 3 行，
  其余行永远不被触碰。
- 折行碎片：OpenConsole 收窄 rewrap 出的布局（后续输出按这套坐标写）
  与 xterm 截断后的旧行并存。
- 黄色 `clear`：PSReadLine 输入行在旧位置的残留（resize 后 PSReadLine
  只重绘新位置的 prompt 区）。

附带结论：`buildNumber: 26100`（打开 xterm 自己的 reflow）反而更糟——
xterm 的 reflow 与 conhost 的 reflow 是两套不同算法，两边都重排时分歧
更大。

## 5. 方案概览

| 方案 | 内容 | 成本 | 效果 |
|---|---|---|---|
| A（`OpenCon.FixA.md`） | resize settle 后在 xterm 本地清视口，残影变临时空白，等应用下一帧填回 | 小 | **已否决**：`\x1b[2J` 只擦视口，静态内容被清掉后无人重绘，比残影更糟；代码已回滚 |
| B | 让 xterm 完全模拟 conhost reflow | 不可行 | xterm 无同款算法 |
| C（`OpenCon.FixB.md`） | 自编译 OpenConsole 打补丁，resize 后合成全屏 VT 重绘 | 大（维护 fork） | 真正逐格同步；**已实现**（活动门控版），打包为 `OpenConsole.Enhanced.exe`，默认关 |
| D | 不动 | 0 | 残影在下次 clear / 全屏重绘时自愈；codex 场景已无感 |

同类问题在 VSCode 终端（同为 xterm.js + ConPTY）同样存在，属 ConPTY
passthrough 架构对客户端的固有要求。
