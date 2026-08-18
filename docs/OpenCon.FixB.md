# Fix B：自编译 OpenConsole，resize 后合成全屏 VT 重绘（终极方案）

对应 `OpenCon.md` 第 5 节方案 C（调查中编号为 C，落盘文件名为 FixB）。
目标：让 ConPTY 一侧在 resize 完成后主动向终端发送一帧全屏重绘，
从根本上消除"client/server buffer 各自重排、无人对账"的 desync，
而不是像 Fix A 那样用空白掩盖。

## 1. 为什么必须自编译

- resize 不发 VT 是 OpenConsole 的既有行为（signal pipe 仅
  ShowHideWindow / ClearBuffer / SetParent / ResizeWindow 四种消息，
  见 `src/host/PtySignalInputThread.hpp:39`），WT 靠客户端共享
  `TextBuffer::ResizeWithReflow` 规避，xterm.js 无法复制该算法。
- 我们已通过 `tools/openconsole/` + EmbeddedResource 自分发
  OpenConsole.exe（见 `conhost.md` 4.2），替换为自编译二进制的分发
  链路是现成的——SHA256 校验的是嵌入副本，嵌入什么就校验什么，
  无需改校验逻辑，只需重新取哈希嵌入。

## 2. 补丁落点

在 `src/host/PtySignalInputThread.cpp` 的 `_DoResizeWindow` 中，
`_api.ResizeWindow(data.sx, data.sy)` 成功返回后，追加一次全屏重绘
发射。重绘内容来自 `GetActiveOutputBuffer()` 的当前视口。

## 3. 需要新写的组件：buffer → VT 序列化器

2026 架构删掉了旧 VtEngine 帧生成器（现在 VT 在各 console API 调用点
直译，见 `OpenCon.md` 2.4），没有现成的"dump 整屏"可调用，需要自己
写一个最简版本：

1. 取视口（`GetViewport()`，注意 origin 可能非 0——resize 缩矮时
   viewport 会下移）；
2. 首行 `\x1b[H` 归位，逐行遍历视口内 `TextBuffer` 行；
3. 每行按 run 输出：属性变化处发 SGR（前景/背景/粗斜下划线，对齐
   `TextAttribute` → SGR 的既有映射代码），文本段直接写；
4. 行尾处理 BCE：当前行剩余部分用当前背景色擦除（`\x1b[K`），与
   conhost 历来行为一致；
5. 行间 `\r\n`；末尾把光标恢复到 OpenConsole 记录的 cursor 位置
   （或依赖既有的 DSR/CPR 光标再同步机制兜底）；
6. 通过 `gci.GetVtWriterForBuffer(&screenInfo)` 写出（与
   `WriteCharsVT` 的转发同通道），在 console lock 内完成。

滚出视口顶部的行（scrollback 方向）无法靠这一帧恢复——终端本地的
scrollback 由 DECSTBM 直通维护（`conhost.md` 主线修复），本方案只
对视口负责。

## 4. 与 xterm.js `windowsPty` 设置的配合

补丁上线后，前端设置应相应调整（`terminal-controller.ts`）：

- OpenConsole 重绘帧到达时，xterm 本地必须处于"刚按 resize 语义重排
  过"的状态，重绘帧以绝对坐标（CUP 逐行）覆盖后两者一致；
- `buildNumber: 19045`（禁 reflow、拉高底部补空）维持不变即可——
  重绘帧会修正补空/截断造成的所有偏差。

## 5. 构建与分发链路

1. 从 `../terminal`（WT 同树）以 conhost.slnf / 对应工程编译
   OpenConsole.exe（MSVC 工具链，x64/arm64 按需）；
2. 替换 `tools/openconsole/OpenConsole.exe`；
3. 重新计算 SHA256，更新嵌入/校验侧的预期哈希（提取器校验的是嵌入
   资源本身，理论上一致即可，重点是发布流程里别混入旧二进制）；
4. 版本标记：建议在二进制里加可识别标记（如资源版本字符串
   `kterm-patched`），便于现场确认跑的是补丁版。

## 6. 成本与风险

- **fork 维护**：上游 WT 持续演进，需定期 rebase；补丁面尽量收敛在
  `_DoResizeWindow` 一处 + 新增一个序列化器文件，降低冲突面。
- **行为风险**：全屏重绘是一帧较大的输出（百行级），resize 拖动中
  连发会造成流量尖峰——应只在最终尺寸上发一次（OpenConsole 侧可做
  简单合并：连续 resize 消息只重绘最后一次）。
- **一致性风险**：序列化器的 SGR 映射若与终端理解不一致，会产生新
  类残影；需要 `scripts/resize-progress.ps1` + 真机 codex/PowerShell
  矩阵回归。
- 若验证有效，可考虑反馈上游（WT 对非 WT 客户端的 ConPTY resize
  体验有长期 issue 讨论），减少 fork 寿命。

## 7. 验证

- 同 Fix A 第 4 节的四项场景，预期标准更高：resize settle 后屏幕应
  与 OpenConsole buffer **逐格一致**，无残影、无留白、无闪烁（重绘
  帧即最终内容）。
- 与 WT 并排对比同一操作序列的最终画面。

## 8. 实施结果（2026-08，已落地为 OpenConsole.Enhanced.exe）

最终实现在 `../terminal`（WT 源码树，未提交）：

- 新增 `src/host/ConptyRepaint.cpp/.hpp`：`EmitViewportRepaint()` 把
  当前视口序列化为 VT 帧（CUP 逐行 + 全保真 SGR 属性映射 + BCE 行尾
  擦除），附带一个全局"ConPTY 输出提交计数器"。
- 改 `src/host/PtySignalInputThread.cpp/.hpp`：`_DoResizeWindow` 加
  **活动门控**；`_DrainPendingResizes` 合并连续 resize；
  `_HasPendingResize` 查询。
- 改 `src/host/VtIo.cpp`：`Writer::Submit` 里
  `NoteConptyOutputSubmitted()` 递增计数器；`formatAttributes` 扩展为
  全保真。

### 迭代过程

- **v1：resize 后立即无条件全屏重绘。** 结果：拖动 resize 期间应用
  还在持续输出，重绘帧与应用输出交错，屏幕被旧几何的 BCE 背景淹没
  （"青色洪水"）。结论：重绘时机错了，比不画更糟。
- **v2：重绘帧对空白行补 `\x1b[0m\x1b[K` 擦除。** 离线模拟改善，但
  真机仍有碎片行——根因同样是时机，不是擦除不干净。
- **v3（最终）：活动门控。** resize 后先放锁等 350ms，用
  `VtIo::Writer::Submit` 的全局计数器判断应用是否沉默：应用仍在输出
  则放弃本次重绘（让应用自己的增量重绘主导），应用沉默才 dump 一帧；
  等待期间来了新 resize 也放弃，由最后一次尺寸触发新一轮门控。

### WT 对照结论

- WT 拖动中干净靠的是 client/server 共享 `ResizeWithReflow`（见
  `OpenCon.md` 第 3 节），不是 ConPTY 发了什么；
- Ctrl+C 后 WT 同样会露出碎片——即"应用不重绘时残留"在 WT 也存在，
  增强版在这个场景反而优于 WT。

### 离线验证（emulator）

`../dump/` 下的 `conpty-dump`（C# ConPTY 桥）+ `emulate.mjs`
（node + @xterm/headless，`windowsPty:{backend:'conpty',buildNumber:19045}`）
双场景验证：

- 进度条场景（应用持续输出）：v3 与原版行为完全一致，干净收敛——
  门控正确放弃了所有重绘。
- 静态长行场景（应用沉默）：内容按新宽度完整折行恢复。

### KTerm 侧分发

补丁版以 `OpenConsole.Enhanced.exe` 与官方原版 `OpenConsole.exe` 一并
嵌入（`tools/openconsole/`），默认不启用：

- 设置 → Shell → "Enable enhanced OpenConsole"（新开标签页生效）；
- `KTERM_CONHOST=enhanced` 强制增强版；`KTERM_CONHOST=kernel` 强制
  系统 conhost；
- 回退链：enhanced（如选）→ stock OpenConsole → 系统 conhost。

真机回归（`make run` 三项：进度条 resize / 长列表 resize /
codex resume）待确认。
