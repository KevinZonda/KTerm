using System.Collections;
using System.Runtime.InteropServices;
using System.Text;
using KevinZonda.KTerm.Configuration;
using KevinZonda.KTerm.Interop;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.KTerm.Terminal;

internal sealed class TerminalSession : IAsyncDisposable
{
    private const int BufferSize = 16 * 1024;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly SafePseudoConsoleHandle _pseudoConsole;
    private readonly SafeKernelHandle _process;
    private readonly TerminalThemePreset _theme;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly object _resizeLock = new();
    private Task? _readTask;
    private Task? _waitTask;
    private Task? _paletteTask;
    private int _columns;
    private int _rows;
    private int _disposed;
    private int _exitRaised;

    private TerminalSession(
        string id,
        string shellName,
        uint processId,
        FileStream input,
        FileStream output,
        SafePseudoConsoleHandle pseudoConsole,
        SafeKernelHandle process,
        TerminalThemePreset theme,
        int columns,
        int rows)
    {
        Id = id;
        ShellName = shellName;
        ProcessId = processId;
        _input = input;
        _output = output;
        _pseudoConsole = pseudoConsole;
        _process = process;
        _theme = theme;
        _columns = columns;
        _rows = rows;
    }

    internal string Id { get; }

    internal string ShellName { get; }

    internal uint ProcessId { get; }

    internal event Action<TerminalSession, string>? OutputReceived;

    internal event Action<TerminalSession, uint>? Exited;

    internal void StartPumps()
    {
        if (_readTask is not null || _waitTask is not null)
        {
            throw new InvalidOperationException("The terminal session pumps have already started.");
        }

        _readTask = Task.Run(ReadLoop);
        _waitTask = Task.Run(WaitForExit);
        _paletteTask = ApplyConsoleThemeAfterStartup();
    }

    internal static TerminalSession Start(
        string id,
        int columns,
        int rows,
        ShellLaunchSpec shell,
        TerminalThemePreset theme,
        string startingDirectory)
    {
        columns = Math.Clamp(columns, 2, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);

        if (!NativeMethods.CreatePipe(out var pseudoInput, out var hostInput, IntPtr.Zero, 0))
        {
            throw NativeMethods.LastError("Unable to create the ConPTY input pipe.");
        }

        if (!NativeMethods.CreatePipe(out var hostOutput, out var pseudoOutput, IntPtr.Zero, 0))
        {
            pseudoInput.Dispose();
            hostInput.Dispose();
            throw NativeMethods.LastError("Unable to create the ConPTY output pipe.");
        }

        SafePseudoConsoleHandle? pseudoConsole = null;
        SafeKernelHandle? process = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;

        try
        {
            var result = NativeMethods.CreatePseudoConsole(
                new NativeMethods.Coord(columns, rows),
                pseudoInput.DangerousGetHandle(),
                pseudoOutput.DangerousGetHandle(),
                0,
                out var pseudoConsoleValue);
            Marshal.ThrowExceptionForHR(result);
            pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleValue);

            pseudoInput.Dispose();
            pseudoOutput.Dispose();

            nuint attributeListSize = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref attributeListSize);

            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw NativeMethods.LastError("Unable to initialize the process attribute list.");
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    NativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole.DangerousGetHandle(),
                    (nuint)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw NativeMethods.LastError("Unable to attach the pseudoconsole to the child process.");
            }

            var startupInfo = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>()
                },
                lpAttributeList = attributeList
            };

            var commandLine = new StringBuilder($"\"{shell.ExecutablePath}\"");
            if (!string.IsNullOrWhiteSpace(shell.Arguments))
            {
                commandLine.Append(' ').Append(shell.Arguments);
            }

            environmentBlock = CreateShellEnvironmentBlock(shell);
            var created = NativeMethods.CreateProcessW(
                shell.ExecutablePath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                environmentBlock,
                startingDirectory,
                ref startupInfo,
                out var processInformation);

            if (!created)
            {
                throw NativeMethods.LastError($"Unable to start shell '{shell.ExecutablePath}'.");
            }

            _ = NativeMethods.CloseHandle(processInformation.hThread);
            process = new SafeKernelHandle(processInformation.hProcess);
            inputStream = new FileStream(hostInput, FileAccess.Write, BufferSize, isAsync: false);
            outputStream = new FileStream(hostOutput, FileAccess.Read, BufferSize, isAsync: false);

            return new TerminalSession(
                id,
                shell.DisplayName,
                processInformation.dwProcessId,
                inputStream,
                outputStream,
                pseudoConsole,
                process,
                theme,
                columns,
                rows);
        }
        catch
        {
            inputStream?.Dispose();
            outputStream?.Dispose();
            process?.Dispose();
            pseudoConsole?.Dispose();
            hostInput.Dispose();
            hostOutput.Dispose();
            pseudoInput.Dispose();
            pseudoOutput.Dispose();
            throw;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    private async Task ApplyConsoleThemeAfterStartup()
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
            {
                await ConsoleThemeHelper.ApplyAfterStartup(
                    ProcessId,
                    _theme,
                    _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private static IntPtr CreateShellEnvironmentBlock(ShellLaunchSpec shell)
    {
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            if (variable.Key is string name && variable.Value is string value)
            {
                environment[name] = value;
            }
        }

        if (shell.Environment is not null)
        {
            foreach (var variable in shell.Environment)
            {
                environment[variable.Key] = variable.Value;
            }
        }

        environment["TERM"] = "xterm-256color";
        environment["COLORTERM"] = "truecolor";
        var block = string.Join('\0', environment.Select(variable => $"{variable.Key}={variable.Value}"))
            + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    internal async Task WriteAsync(string data)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(data))
        {
            return;
        }

        TerminalProtocolTrace.Observe(Id, "renderer->process", data);
        var bytes = Encoding.UTF8.GetBytes(data);
        await WriteAsync(bytes).ConfigureAwait(false);
        TerminalProtocolTrace.Observe(Id, "renderer->pipe", data);
    }

    internal async Task WriteAsync(ReadOnlyMemory<byte> data)
    {
        if (Volatile.Read(ref _disposed) != 0 || data.IsEmpty)
        {
            return;
        }

        await _inputLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(data, _lifetime.Token).ConfigureAwait(false);
            await _input.FlushAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _inputLock.Release();
        }
    }

    internal void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        columns = Math.Clamp(columns, 2, short.MaxValue);
        rows = Math.Clamp(rows, 1, short.MaxValue);

        lock (_resizeLock)
        {
            if (_columns == columns && _rows == rows)
            {
                return;
            }

            var result = NativeMethods.ResizePseudoConsole(
                _pseudoConsole.DangerousGetHandle(),
                new NativeMethods.Coord(columns, rows));
            Marshal.ThrowExceptionForHR(result);
            _columns = columns;
            _rows = rows;
        }
    }

    private void ReadLoop()
    {
        var bytes = new byte[BufferSize];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(BufferSize)];
        var decoder = Encoding.UTF8.GetDecoder();

        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var count = _output.Read(bytes, 0, bytes.Length);
                if (count == 0)
                {
                    break;
                }

                decoder.Convert(
                    bytes,
                    0,
                    count,
                    chars,
                    0,
                    chars.Length,
                    flush: false,
                    out _,
                    out var charsUsed,
                    out _);

                if (charsUsed > 0)
                {
                    var data = new string(chars, 0, charsUsed);
                    TerminalProtocolTrace.Observe(Id, "process->renderer", data);
                    OutputReceived?.Invoke(this, data);
                }
            }
        }
        catch (Exception) when (_lifetime.IsCancellationRequested || Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private void WaitForExit()
    {
        var waitResult = NativeMethods.WaitForSingleObject(_process.DangerousGetHandle(), uint.MaxValue);
        if (waitResult != NativeMethods.WaitObject0)
        {
            return;
        }

        var exitCode = 0u;
        _ = NativeMethods.GetExitCodeProcess(_process.DangerousGetHandle(), out exitCode);
        RaiseExited(exitCode);
    }

    private void RaiseExited(uint exitCode)
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) == 0)
        {
            Exited?.Invoke(this, exitCode);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetime.Cancel();
        _input.Dispose();

        await Task.Run(() =>
        {
            _pseudoConsole.Dispose();

            var waitResult = NativeMethods.WaitForSingleObject(_process.DangerousGetHandle(), 750);
            if (waitResult == NativeMethods.WaitTimeout)
            {
                _ = NativeMethods.TerminateProcess(_process.DangerousGetHandle(), 1);
                _ = NativeMethods.WaitForSingleObject(_process.DangerousGetHandle(), 750);
            }

            _output.Dispose();
        }).ConfigureAwait(false);

        try
        {
            var pumps = new[] { _readTask, _waitTask, _paletteTask }.OfType<Task>();
            await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _process.Dispose();
        _inputLock.Dispose();
        _lifetime.Dispose();
    }

}
