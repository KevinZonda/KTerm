# OpenConsole.exe

`OpenConsole.exe` is the headless pseudoconsole host from
[Windows Terminal](https://github.com/microsoft/terminal) (MIT license),
copied from an installed Windows Terminal package.

At build time it is embedded into the KTerm assembly (`KTerm.Binaries/OpenConsole.exe`);
on first use it is extracted to `%LOCALAPPDATA%\KTerm\bin\<hash>\OpenConsole.exe`
and spawned from there (`--headless --width --height --signal --server`) through
the winconpty protocol instead of the inbox `CreatePseudoConsole`. Cached files
are re-verified against the embedded copy (SHA256) before use; a mismatch is
surfaced to the user and the file is never executed. This
OpenConsole parses VT sequences into its buffer and forwards them verbatim to
the terminal (passthrough ConPTY). The inbox conhost on Windows 10 consumes
scroll-region and other VT sequences and repaints the screen instead, which
destroys scrollback for TUI apps such as codex.

If the resource is absent or extraction fails, KTerm automatically falls back
to the inbox conhost.
