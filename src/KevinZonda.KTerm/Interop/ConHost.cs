using System.Runtime.InteropServices;
using System.Security.Cryptography;
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

        return ExtractBundledOpenConsole();
    }

    /// <summary>
    /// Raised when the extracted OpenConsole.exe fails its integrity check.
    /// Return true to re-extract from the embedded copy, false to fall back to
    /// the inbox conhost. When unset, mismatches are repaired silently. The
    /// mismatched file is never executed regardless of the outcome.
    /// </summary>
    internal static Func<string, bool>? IntegrityConflictHandler;

    // Single-file distribution: OpenConsole.exe is embedded as a resource and
    // extracted once to a content-addressed cache directory, because Windows
    // can only CreateProcess a real file. The directory name carries the hash
    // of the embedded copy, so upgrades extract fresh and unchanged installs
    // reuse the cache. Reused files are re-verified against the embedded copy:
    // a mismatch means corruption or tampering, and the file is never used.
    private static string? ExtractBundledOpenConsole()
    {
        try
        {
            using var stream = typeof(ConHost).Assembly
                .GetManifestResourceStream("KTerm.Binaries/OpenConsole.exe");
            if (stream is null)
            {
                return null;
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            var expectedHash = SHA256.HashData(bytes);

            var hash = Convert.ToHexString(expectedHash)[..8].ToLowerInvariant();
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "KTerm", "bin", hash);
            var candidate = Path.Combine(directory, "OpenConsole.exe");

            if (File.Exists(candidate))
            {
                if (HashesMatch(candidate, expectedHash))
                {
                    return candidate;
                }

                // Corruption or tampering: surface it, never run the file.
                System.Diagnostics.Debug.WriteLine(
                    $"Extracted OpenConsole.exe failed its integrity check: {candidate}");
                if (IntegrityConflictHandler?.Invoke(candidate) == false)
                {
                    return null;
                }
            }

            Directory.CreateDirectory(directory);
            // Write-then-move so concurrent KTerm instances never see a partial file.
            var temporary = $"{candidate}.{Environment.ProcessId}.tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, candidate, overwrite: true);

            // Re-verify what actually landed on disk before using it.
            return HashesMatch(candidate, expectedHash) ? candidate : null;
        }
        catch (Exception error)
        {
            System.Diagnostics.Debug.WriteLine($"Unable to extract the bundled OpenConsole.exe: {error}");
            return null;
        }
    }

    private static bool HashesMatch(string path, byte[] expectedHash)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return SHA256.HashData(stream).AsSpan().SequenceEqual(expectedHash);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
