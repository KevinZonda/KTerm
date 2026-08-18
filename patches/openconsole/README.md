# OpenConsole ConPTY repaint patch

Patch against [Windows Terminal](https://github.com/microsoft/terminal) that
makes OpenConsole re-emit the full viewport as VT when the client application
stays silent after a resize. This is the source of
`tools/openconsole/OpenConsole.Enhanced.exe`; background and rationale in
`docs/OpenCon.md` and `docs/OpenCon.FixB.md`.

## Baseline

- Tag: `v1.25.1912.0`
- Commit: `1cea42d433253d95c4487a3037db48197b5e72f4` (2026-07-10)
- File: `kterm-conpty-repaint.patch` — applies cleanly with
  `git apply kterm-conpty-repaint.patch` on that commit (verified with a
  temporary index against HEAD).

## What the patch does

After a ConPTY resize, OpenConsole reflows its own buffer but emits nothing,
so a passthrough client (xterm.js) and OpenConsole desync and stale content
(ghosting) is left on screen — see `docs/OpenCon.md`. The patch makes
`_DoResizeWindow` arm a 350 ms quiet timer: if the application keeps writing
(counted via a global submit counter bumped in `VtIo::Writer::Submit`), the
repaint is abandoned and the application's own incremental redraws win; if
the application goes quiet, the current viewport is serialized and emitted
(CUP per row, full-fidelity SGR, BCE line-tail erase). A resize arriving
while armed re-arms on the final size. Consecutive resize messages are
coalesced.

## File inventory

- `src/host/ConptyRepaint.cpp` / `.hpp` (new): `EmitViewportRepaint()` buffer
  → VT serializer + output-submit counter.
- `src/host/PtySignalInputThread.cpp` / `.hpp`: activity gating in
  `_DoResizeWindow`, `_DrainPendingResizes` coalescing, `_HasPendingResize`.
- `src/host/VtIo.cpp`: `NoteConptyOutputSubmitted()` in `Writer::Submit`;
  full-fidelity `formatAttributes`.
- `src/host/host-common.vcxitems`: register the two new files.

## Build

```
git clone https://github.com/microsoft/terminal
cd terminal
git checkout v1.25.1912.0
git apply <kterm>/patches/openconsole/kterm-conpty-repaint.patch
# one-time WT toolchain setup, see WT docs: .github etc.
MSBuild.exe src/host/exe/Host.EXE.vcxproj -m -p:Configuration=Release -p:Platform=x64 \
  -p:SolutionDir=<terminal-repo>\ -p:WindowsTargetPlatformVersion=10.0.26100.0
```

(MSBuild here: `C:/Program Files (x86)/Microsoft Visual Studio/18/BuildTools/MSBuild/Current/Bin/MSBuild.exe`.)
The resulting `OpenConsole.exe` is copied to
`tools/openconsole/OpenConsole.Enhanced.exe` and embedded by the csproj.

## Provenance / verification

SHA256 of the shipped binaries in `tools/openconsole/`:

| Binary | SHA256 |
|---|---|
| `OpenConsole.exe` (stock, from installed WT package) | `b7fd936c2668b87b9ecf7b3366dc6568afc1c6f981874cba3e955a1c35cf8160` |
| `OpenConsole.Enhanced.exe` (baseline + this patch) | `eec281ccddc3d53c3eabfd0399b1714a47f2f5b3d13528d7454de05abb9e05dd` |

KTerm re-verifies extracted copies against the embedded resource (SHA256)
before every use, so a locally modified cache file is never executed.
