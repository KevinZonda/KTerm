using System.Collections.Concurrent;

namespace KevinZonda.KTerm.Terminal;

internal sealed class TerminalSessionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new();
    private int _disposed;

    internal event Action<string, string>? OutputReceived;

    internal event Action<string, uint>? SessionExited;

    internal async Task<TerminalSessionInfo> CreateAsync(int columns, int rows)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var id = Guid.NewGuid().ToString("N");
        var session = await Task.Run(() => TerminalSession.Start(id, columns, rows)).ConfigureAwait(false);
        session.OutputReceived += HandleOutput;
        session.Exited += HandleExit;

        if (!_sessions.TryAdd(id, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Unable to register the new terminal session.");
        }

        session.StartPumps();

        return new TerminalSessionInfo(
            id,
            Path.GetFileNameWithoutExtension(session.ShellPath),
            session.ProcessId);
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

        await Task.WhenAll(sessions.Select(async pair =>
        {
            pair.Value.OutputReceived -= HandleOutput;
            pair.Value.Exited -= HandleExit;
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        })).ConfigureAwait(false);
    }
}

internal sealed record TerminalSessionInfo(string Id, string ShellName, uint ProcessId);
