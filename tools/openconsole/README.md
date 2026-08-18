# OpenConsole.exe / OpenConsole.Enhanced.exe

`OpenConsole.exe` is the headless pseudoconsole host from
[Windows Terminal](https://github.com/microsoft/terminal) (MIT license),
copied from an installed Windows Terminal package.

`OpenConsole.Enhanced.exe` is the same host built from the Windows Terminal
source tree with the KTerm repaint patch applied: after a resize it waits for
the client application to go quiet and then re-emits the full viewport, so
static screen content that a TUI app fails to redraw itself (e.g. codex after
resize or Ctrl+C) is repainted. See `docs/OpenCon.FixB.md` for the patch
inventory and the build recipe:

```
MSBuild.exe src/host/exe/Host.EXE.vcxproj -m -p:Configuration=Release -p:Platform=x64 \
  -p:SolutionDir=<terminal-repo>\ -p:WindowsTargetPlatformVersion=10.0.26100.0
```

At build time both are embedded into the KevinZonda Terminal assembly
(`KevinZonda.Terminal.Binaries/OpenConsole.exe` and
`KevinZonda.Terminal.Binaries/OpenConsole.Enhanced.exe`); on first use each is
extracted to `%LOCALAPPDATA%\KTerm\bin\<hash>\<name>` and spawned from there
(`--headless --width --height --signal --server`) through the winconpty
protocol instead of the inbox `CreatePseudoConsole`. Cached files are
re-verified against the embedded copy (SHA256) before use; a mismatch is
surfaced to the user and the file is never executed. OpenConsole parses VT
sequences into its buffer and forwards them verbatim to the terminal
(passthrough ConPTY). The inbox conhost on Windows 10 consumes scroll-region
and other VT sequences and repaints the screen instead, which destroys
scrollback for TUI apps such as codex.

Which variant is used is controlled per environment:

- Settings → Shell → *Enable enhanced OpenConsole* (off by default), or
- `KTERM_CONHOST=enhanced` to force the enhanced build,
- `KTERM_CONHOST=kernel` to force the inbox conhost.

If a requested variant's resource is absent or extraction fails, KevinZonda
Terminal falls back to the stock OpenConsole and then to the inbox conhost.
