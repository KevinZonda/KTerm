using System.Diagnostics;
using System.Text.Json;
using KevinZonda.KTerm.Messaging;
using KevinZonda.KTerm.Terminal;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KevinZonda.KTerm;

internal sealed class MainForm : Form
{
    private const string AppHostName = "app.kterm";

    private readonly WebView2 _webView;
    private readonly TerminalSessionManager _sessions = new();
    private WebViewBridge? _bridge;
    private bool _initialized;
    private bool _allowClose;

    internal MainForm()
    {
        Text = "KTerm";
        BackColor = Color.FromArgb(12, 15, 20);
        ClientSize = new Size(1100, 720);
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterScreen;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = BackColor
        };
        Controls.Add(_webView);

        Shown += HandleShown;
        FormClosing += HandleFormClosing;
    }

    private async void HandleShown(object? sender, EventArgs eventArgs)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;

        try
        {
            await InitializeWebView();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"KTerm could not initialize WebView2.\n\n{exception.Message}",
                "KTerm startup error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private async Task InitializeWebView()
    {
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var indexFile = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexFile))
        {
            throw new FileNotFoundException("The KTerm web assets are missing.", indexFile);
        }

        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KTerm",
            "WebView2");
        Directory.CreateDirectory(userDataFolder);

        var environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: userDataFolder);
        await _webView.EnsureCoreWebView2Async(environment);

        var core = _webView.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping(
            AppHostName,
            webRoot,
            CoreWebView2HostResourceAccessKind.DenyCors);

        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
#if DEBUG
        core.Settings.AreDevToolsEnabled = true;
#else
        core.Settings.AreDevToolsEnabled = false;
#endif

        core.NavigationStarting += HandleNavigationStarting;
#if DEBUG
        core.NavigationCompleted += HandleDebugNavigationCompleted;
#endif
        core.NewWindowRequested += HandleNewWindowRequested;
        core.ProcessFailed += HandleProcessFailed;
        _bridge = new WebViewBridge(_webView, _sessions);

        core.Navigate($"https://{AppHostName}/index.html");
    }

#if DEBUG
    private async void HandleDebugNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess || Environment.GetEnvironmentVariable("KTERM_SMOKE_TEST") != "1")
        {
            return;
        }

        _webView.CoreWebView2.NavigationCompleted -= HandleDebugNavigationCompleted;
        await Task.Delay(1_500);
        await DispatchDebugShortcut("KeyT", "t", 0x54);
        await Task.Delay(500);
        await DispatchDebugShortcut("Backslash", "\\", 0xDC);
        await Task.Delay(500);
        await DispatchDebugShortcut("Minus", "-", 0xBD);
        await Task.Delay(500);
        await DispatchDebugClick(250, 250);
        await Task.Delay(250);
        await DispatchDebugShortcut("Minus", "-", 0xBD);
        await Task.Delay(750);
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync(
            "Input.insertText",
            JsonSerializer.Serialize(new { text = "echo KTERM_SMOKE" }));
        await DispatchDebugShortcut("Enter", "\r", 0x0D, modifiers: 0);
    }

    private async Task DispatchDebugShortcut(string code, string key, int virtualKey, int modifiers = 1)
    {
        var arguments = JsonSerializer.Serialize(new
        {
            type = "keyDown",
            modifiers,
            windowsVirtualKeyCode = virtualKey,
            nativeVirtualKeyCode = virtualKey,
            code,
            key
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", arguments);

        arguments = JsonSerializer.Serialize(new
        {
            type = "keyUp",
            modifiers,
            windowsVirtualKeyCode = virtualKey,
            nativeVirtualKeyCode = virtualKey,
            code,
            key
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", arguments);
    }

    private async Task DispatchDebugClick(int x, int y)
    {
        var arguments = JsonSerializer.Serialize(new
        {
            type = "mousePressed",
            x,
            y,
            button = "left",
            clickCount = 1
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", arguments);

        arguments = JsonSerializer.Serialize(new
        {
            type = "mouseReleased",
            x,
            y,
            button = "left",
            clickCount = 1
        });
        await _webView.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", arguments);
    }
#endif

    private void HandleNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (Uri.TryCreate(eventArgs.Uri, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Host, AppHostName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        eventArgs.Cancel = true;
        OpenExternal(eventArgs.Uri);
    }

    private void HandleNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs eventArgs)
    {
        eventArgs.Handled = true;
        OpenExternal(eventArgs.Uri);
    }

    private void HandleProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs eventArgs) =>
        _bridge?.NotifyRuntimeFailure(eventArgs.ProcessFailedKind.ToString());

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri)
        {
            UseShellExecute = true
        });
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if ((keyData & Keys.Modifiers) == Keys.Alt)
        {
            var command = (keyData & Keys.KeyCode) switch
            {
                Keys.T => "newTab",
                Keys.Oem5 => "splitColumns",
                Keys.OemMinus => "splitRows",
                _ => null
            };

            if (command is not null && _bridge is not null)
            {
                _bridge.SendWorkspaceCommand(command);
                return true;
            }
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    private async void HandleFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_allowClose)
        {
            return;
        }

        eventArgs.Cancel = true;
        Enabled = false;
        _bridge?.Dispose();
        _bridge = null;
        await _sessions.DisposeAsync();

        if (_webView.CoreWebView2 is not null)
        {
            _webView.CoreWebView2.NavigationStarting -= HandleNavigationStarting;
#if DEBUG
            _webView.CoreWebView2.NavigationCompleted -= HandleDebugNavigationCompleted;
#endif
            _webView.CoreWebView2.NewWindowRequested -= HandleNewWindowRequested;
            _webView.CoreWebView2.ProcessFailed -= HandleProcessFailed;
        }

        _webView.Dispose();
        _allowClose = true;
        Close();
    }
}
