# OpenConsole.exe

`OpenConsole.exe` is the headless pseudoconsole host from
[Windows Terminal](https://github.com/microsoft/terminal) (MIT license),
copied from an installed Windows Terminal package.

KTerm spawns it (`--headless --width --height --signal --server`) through the
winconpty protocol instead of the inbox `CreatePseudoConsole`, because this
OpenConsole parses VT sequences into its buffer and forwards them verbatim to
the terminal (passthrough ConPTY). The inbox conhost on Windows 10 consumes
scroll-region and other VT sequences and repaints the screen instead, which
destroys scrollback for TUI apps such as codex.

If this file is absent, KTerm automatically falls back to the inbox conhost.
