using System.Runtime.InteropServices;
using System.Text;
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
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly object _resizeLock = new();
    private Task? _readTask;
    private Task? _waitTask;
    private int _columns;
    private int _rows;
    private int _disposed;
    private int _exitRaised;

    private TerminalSession(
        string id,
        string shellPath,
        uint processId,
        FileStream input,
        FileStream output,
        SafePseudoConsoleHandle pseudoConsole,
        SafeKernelHandle process,
        int columns,
        int rows)
    {
        Id = id;
        ShellPath = shellPath;
        ProcessId = processId;
        _input = input;
        _output = output;
        _pseudoConsole = pseudoConsole;
        _process = process;
        _columns = columns;
        _rows = rows;
    }

    internal string Id { get; }

    internal string ShellPath { get; }

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
    }

    internal static TerminalSession Start(string id, int columns, int rows)
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

            var shellPath = ResolveDefaultShell();
            var startupInfo = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>()
                },
                lpAttributeList = attributeList
            };

            var commandLine = new StringBuilder($"\"{shellPath}\"");
            var created = NativeMethods.CreateProcessW(
                shellPath,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                IntPtr.Zero,
                Environment.CurrentDirectory,
                ref startupInfo,
                out var processInformation);

            if (!created)
            {
                throw NativeMethods.LastError($"Unable to start shell '{shellPath}'.");
            }

            _ = NativeMethods.CloseHandle(processInformation.hThread);
            process = new SafeKernelHandle(processInformation.hProcess);
            inputStream = new FileStream(hostInput, FileAccess.Write, BufferSize, isAsync: false);
            outputStream = new FileStream(hostOutput, FileAccess.Read, BufferSize, isAsync: false);

            return new TerminalSession(
                id,
                shellPath,
                processInformation.dwProcessId,
                inputStream,
                outputStream,
                pseudoConsole,
                process,
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
        }
    }

    internal async Task WriteAsync(string data)
    {
        if (Volatile.Read(ref _disposed) != 0 || string.IsNullOrEmpty(data))
        {
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(data);
        await _inputLock.WaitAsync(_lifetime.Token).ConfigureAwait(false);
        try
        {
            await _input.WriteAsync(bytes, _lifetime.Token).ConfigureAwait(false);
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
                    OutputReceived?.Invoke(this, new string(chars, 0, charsUsed));
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
            var pumps = new[] { _readTask, _waitTask }.OfType<Task>();
            await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _process.Dispose();
        _inputLock.Dispose();
        _lifetime.Dispose();
    }

    private static string ResolveDefaultShell()
    {
        foreach (var candidate in new[] { "pwsh.exe", "powershell.exe" })
        {
            var resolved = FindOnPath(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        var commandProcessor = Environment.GetEnvironmentVariable("COMSPEC");
        if (!string.IsNullOrWhiteSpace(commandProcessor) && File.Exists(commandProcessor))
        {
            return Path.GetFullPath(commandProcessor);
        }

        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (File.Exists(cmd))
        {
            return cmd;
        }

        throw new FileNotFoundException("No supported command shell was found.");
    }

    private static string? FindOnPath(string fileName)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var pathEntry in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(pathEntry.Trim(), fileName));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return null;
    }
}
