using System.Collections.Concurrent;

namespace KevinZonda.KTerm.Terminal;

internal sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private readonly object _prewarmLock = new();
    private Task<TerminalSession>? _prewarmedSession;
    private int _disposed;

    internal event Action<string, string>? OutputReceived;

    internal event Action<string, uint>? SessionExited;

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
            _prewarmedSession = Task.Run(() => TerminalSession.Start(id, columns, rows));
        }
    }

    internal async Task<TerminalSessionInfo> CreateAsync(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var prewarmedSession = TakePrewarmedSession();
        var session = prewarmedSession is not null
            ? await prewarmedSession.ConfigureAwait(false)
            : await Task.Run(() => TerminalSession.Start(
                Guid.NewGuid().ToString("N"),
                columns,
                rows)).ConfigureAwait(false);

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
            Path.GetFileNameWithoutExtension(session.ShellPath),
            session.ProcessId);
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
