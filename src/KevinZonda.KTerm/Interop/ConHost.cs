using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KevinZonda.KTerm.Interop;

/// <summary>
/// Picks the best available pseudoconsole host. A side-by-side OpenConsole.exe
/// (passthrough ConPTY) is preferred because the inbox conhost on Windows 10
/// consumes VT sequences and repaints instead of forwarding them; the inbox
/// host is the fallback. Set KTERM_CONHOST=kernel to force the fallback.
/// </summary>
internal static class ConHost
{
    internal static IConHost Create(int columns, int rows, SafeFileHandle input, SafeFileHandle output)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("KTERM_CONHOST"), "kernel", StringComparison.OrdinalIgnoreCase))
        {
            var hostPath = FindOpenConsole();
            if (hostPath is not null)
            {
                try
                {
                    return OpenConsoleConHost.Create(columns, rows, input, output, hostPath);
                }
                catch (Exception error)
                {
                    System.Diagnostics.Debug.WriteLine($"OpenConsole host unavailable, falling back to inbox conhost: {error}");
                }
            }
        }

        return KernelConHost.Create(columns, rows, input, output);
    }

    private static string? FindOpenConsole()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidate = Path.Combine(baseDirectory, "OpenConsole.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        // Same architecture-subfolder layout winconpty probes.
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => null
        };
        if (architecture is not null)
        {
            candidate = Path.Combine(baseDirectory, architecture, "OpenConsole.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
