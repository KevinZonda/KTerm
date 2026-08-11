using System.Collections.Concurrent;
using KevinZonda.KTerm.Configuration;

namespace KevinZonda.KTerm.Terminal;

internal sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly object _prewarmLock = new();
    private ShellSettings _shellSettings;
    private Task<TerminalSession>? _prewarmedSession;
    private int _disposed;

    internal event Action<string, string>? OutputReceived;

    internal event Action<string, uint>? SessionExited;

    internal TerminalSessionManager(ShellSettings shellSettings)
    {
        _shellSettings = ShellSettings.Normalize(shellSettings);
    }

    internal void Prewarm(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_prewarmLock)
        {
            if (_prewarmedSession is not null || Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            var id = Guid.NewGuid().ToString("N");
            var shellSettings = _shellSettings;
            _prewarmedSession = Task.Run(() => TerminalSession.Start(
                id,
                columns,
                rows,
                ShellProfileCatalog.Resolve(shellSettings)));
        }
    }

    internal async Task<TerminalSessionInfo> CreateAsync(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var prewarmedSession = TakePrewarmedSession();
        var shellSettings = GetShellSettings();
        var session = prewarmedSession is not null
            ? await prewarmedSession.ConfigureAwait(false)
            : await Task.Run(() => TerminalSession.Start(
                Guid.NewGuid().ToString("N"),
                columns,
                rows,
                ShellProfileCatalog.Resolve(shellSettings))).ConfigureAwait(false);

        if (Volatile.Read(ref _disposed) != 0)
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(TerminalSessionManager));
        }

        try
        {
            session.Resize(columns, rows);
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        session.OutputReceived += HandleOutput;
        session.Exited += HandleExit;

        if (!_sessions.TryAdd(session.Id, session))
        {
            session.OutputReceived -= HandleOutput;
            session.Exited -= HandleExit;
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Unable to register the new terminal session.");
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            await CloseAsync(session.Id).ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(TerminalSessionManager));
        }

        session.StartPumps();

        return new TerminalSessionInfo(
            session.Id,
            session.ShellName,
            session.ProcessId);
    }

    internal async Task UpdateShellAsync(ShellSettings shellSettings)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var normalized = ShellSettings.Normalize(shellSettings);
        Task<TerminalSession>? previousPrewarm;
        lock (_prewarmLock)
        {
            if (_shellSettings == normalized)
            {
                return;
            }

            _shellSettings = normalized;
            previousPrewarm = _prewarmedSession;
            _prewarmedSession = null;
        }

        if (previousPrewarm is not null)
        {
            await DisposePrewarmedSession(previousPrewarm).ConfigureAwait(false);
        }
    }

    private ShellSettings GetShellSettings()
    {
        lock (_prewarmLock)
        {
            return _shellSettings;
        }
    }

    private Task<TerminalSession>? TakePrewarmedSession()
    {
        lock (_prewarmLock)
        {
            var session = _prewarmedSession;
            _prewarmedSession = null;
            return session;
        }
    }

    internal Task WriteAsync(string sessionId, string data) =>
        Get(sessionId).WriteAsync(data);

    internal void Resize(string sessionId, int columns, int rows) =>
        Get(sessionId).Resize(columns, rows);

    internal async Task CloseAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.OutputReceived -= HandleOutput;
            session.Exited -= HandleExit;
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    private TerminalSession Get(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new KeyNotFoundException($"Terminal session '{sessionId}' does not exist.");
        }

        return session;
    }

    private void HandleOutput(TerminalSession session, string data) =>
        OutputReceived?.Invoke(session.Id, data);

    private void HandleExit(TerminalSession session, uint exitCode) =>
        SessionExited?.Invoke(session.Id, exitCode);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var sessions = _sessions.ToArray();
        _sessions.Clear();
        var prewarmedSession = TakePrewarmedSession();

        var disposalTasks = sessions.Select(async pair =>
        {
            pair.Value.OutputReceived -= HandleOutput;
            pair.Value.Exited -= HandleExit;
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }).ToList();

        if (prewarmedSession is not null)
        {
            disposalTasks.Add(DisposePrewarmedSession(prewarmedSession));
        }

        await Task.WhenAll(disposalTasks).ConfigureAwait(false);
    }

    private static async Task DisposePrewarmedSession(Task<TerminalSession> sessionTask)
    {
        try
        {
            var session = await sessionTask.ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // TerminalSession.Start releases partially created resources before propagating a failure.
        }
    }
}

internal sealed record TerminalSessionInfo(string Id, string ShellName, uint ProcessId);
