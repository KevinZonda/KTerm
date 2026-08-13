using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using KevinZonda.KTerm.Configuration;
using KevinZonda.KTerm.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KevinZonda.KTerm.Messaging;

internal sealed class WebViewBridge : IDisposable
{
    private const int MaxOutputBatchChars = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebView2 _webView;
    private readonly TerminalSessionManager _sessions;
    private readonly Action _openSettings;
    private readonly Action<string> _openExternal;
    private readonly Func<double, Task<AppSettings>> _saveFontSize;
    private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _outputQueues = new();
    private readonly System.Windows.Forms.Timer _outputTimer;
    private AppSettings _settings;
    private int _disposed;

    internal WebViewBridge(
        WebView2 webView,
        TerminalSessionManager sessions,
        Action openSettings,
        Action<string> openExternal,
        Func<double, Task<AppSettings>> saveFontSize,
        AppSettings settings)
    {
        _webView = webView;
        _sessions = sessions;
        _openSettings = openSettings;
        _openExternal = openExternal;
        _saveFontSize = saveFontSize;
        _settings = settings;
        _sessions.OutputReceived += QueueOutput;
        _sessions.SessionExited += QueueExit;
        _webView.CoreWebView2.WebMessageReceived += HandleMessage;

        _outputTimer = new System.Windows.Forms.Timer
        {
            Interval = 12,
            Enabled = true
        };
        _outputTimer.Tick += FlushOutput;
    }

    internal void NotifyRuntimeFailure(string kind) =>
        Post("app.runtimeFailed", payload: new { kind });

    internal void SendWorkspaceCommand(string command) =>
        Post("workspace.command", payload: new { command });

    internal void SendSettingsChanged(AppSettings settings)
    {
        _settings = settings;
        Post("app.settingsChanged", payload: new { settings });
    }

    private async void HandleMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        BridgeMessage? message = null;

        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(eventArgs.WebMessageAsJson, JsonOptions);
            if (message is null || message.Version != 1 || string.IsNullOrWhiteSpace(message.Type))
            {
                throw new InvalidDataException("Unsupported bridge message.");
            }

            switch (message.Type)
            {
                case "app.ready":
                    Post("app.initialState", message.RequestId, payload: new
                    {
                        application = "KevinZonda.KTerm",
                        version = Application.ProductVersion,
                        settings = _settings
                    });
                    break;

                case "session.create":
                    await CreateSession(message);
                    break;

                case "session.input":
                    await _sessions.WriteAsync(
                        RequireSessionId(message),
                        GetString(message.Payload, "data"));
                    break;

                case "session.binaryInput":
                    await _sessions.WriteAsync(
                        RequireSessionId(message),
                        Convert.FromBase64String(GetString(message.Payload, "data")));
                    break;

                case "session.resize":
                    _sessions.Resize(
                        RequireSessionId(message),
                        GetInt32(message.Payload, "cols", 80),
                        GetInt32(message.Payload, "rows", 24));
                    break;

                case "session.close":
                    await _sessions.CloseAsync(RequireSessionId(message));
                    break;

                case "clipboard.read":
                    Post("clipboard.value", message.RequestId, payload: new
                    {
                        text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty
                    });
                    break;

                case "clipboard.write":
                    var text = GetString(message.Payload, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        Clipboard.SetText(text);
                    }
                    break;

                case "window.settings":
                    _webView.BeginInvoke(_openSettings);
                    break;

                case "window.openExternal":
                    _openExternal(GetString(message.Payload, "uri"));
                    break;

                case "settings.fontSize":
                    _settings = await _saveFontSize(GetDouble(message.Payload, "size", 14));
                    Post("settings.saved", message.RequestId, payload: new { settings = _settings });
                    break;

                default:
                    throw new InvalidDataException($"Unknown bridge message type '{message.Type}'.");
            }
        }
        catch (Exception exception)
        {
            Post(
                "session.error",
                message?.RequestId,
                message?.SessionId,
                new { message = exception.Message });
        }
    }

    private async Task CreateSession(BridgeMessage message)
    {
        var session = await _sessions.CreateAsync(
            GetInt32(message.Payload, "cols", 80),
            GetInt32(message.Payload, "rows", 24));

        Post("session.created", message.RequestId, session.Id, new
        {
            shellName = session.ShellName,
            processId = session.ProcessId
        });
    }

    private void QueueOutput(string sessionId, string data)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _outputQueues.GetOrAdd(sessionId, static _ => new ConcurrentQueue<string>()).Enqueue(data);
    }

    private void QueueExit(string sessionId, uint exitCode)
    {
        if (Volatile.Read(ref _disposed) != 0 || _webView.IsDisposed)
        {
            return;
        }

        try
        {
            _webView.BeginInvoke(() => Post("session.exited", sessionId: sessionId, payload: new { exitCode }));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void FlushOutput(object? sender, EventArgs eventArgs)
    {
        foreach (var (sessionId, queue) in _outputQueues)
        {
            if (queue.IsEmpty)
            {
                continue;
            }

            var builder = new StringBuilder();
            while (builder.Length < MaxOutputBatchChars && queue.TryDequeue(out var chunk))
            {
                builder.Append(chunk);
            }

            if (builder.Length > 0)
            {
                Post("session.output", sessionId: sessionId, payload: new { data = builder.ToString() });
            }

            if (queue.IsEmpty)
            {
                _outputQueues.TryRemove(new KeyValuePair<string, ConcurrentQueue<string>>(sessionId, queue));
            }
        }
    }

    private void Post(
        string type,
        string? requestId = null,
        string? sessionId = null,
        object? payload = null)
    {
        if (Volatile.Read(ref _disposed) != 0 || _webView.IsDisposed || _webView.CoreWebView2 is null)
        {
            return;
        }

        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            type,
            requestId,
            sessionId,
            payload = payload ?? new { }
        }, JsonOptions);
        _webView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private static string RequireSessionId(BridgeMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.SessionId))
        {
            throw new InvalidDataException("The message is missing a session ID.");
        }

        return message.SessionId;
    }

    private static string GetString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int GetInt32(JsonElement payload, string propertyName, int defaultValue)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var value))
        {
            return value;
        }

        return defaultValue;
    }

    private static double GetDouble(JsonElement payload, string propertyName, double defaultValue)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(propertyName, out var property) &&
            property.TryGetDouble(out var value))
        {
            return value;
        }

        return defaultValue;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _outputTimer.Stop();
        _outputTimer.Tick -= FlushOutput;
        _outputTimer.Dispose();
        _sessions.OutputReceived -= QueueOutput;
        _sessions.SessionExited -= QueueExit;

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.WebMessageReceived -= HandleMessage;
        }
    }
}
