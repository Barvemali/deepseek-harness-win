using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;

namespace DshShell;

/// <summary>
/// The shell window: a toolbar over a WebView2 hosting the dsh web GUI. On
/// launch it probes the configured URL, starts the configured server command
/// when nothing is listening, and only then navigates — the page is served by
/// dsh web, which is the only source that injects window.__DSH_BOOT__.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>How often the watchdog asks the loaded page whether it still runs.</summary>
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);

    /// <summary>How long one watchdog answer may take before the page counts as unresponsive.</summary>
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a navigation may stay unfinished before it counts as a frozen page.</summary>
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Consecutive missed answers that rebuild the control instead of reloading it.</summary>
    private const int WatchdogFailuresBeforeRebuild = 3;

    private readonly DshServerProcess _server = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly DispatcherQueueTimer _watchdog;
    private ShellSettings _settings = new();
    private WebView2? _webView;
    private string _serverUrl = string.Empty;
    private DateTime _navigationStartedAt = DateTime.MinValue;
    private int _watchdogFailures;
    private bool _recovering;
    private bool _closing;

    /// <summary>Whether a navigation started recently enough to still be running.</summary>
    private bool NavigationInFlight => DateTime.UtcNow - _navigationStartedAt < NavigationTimeout;

    public MainWindow()
    {
        InitializeComponent();
        Title = "DeepSeek Harness";
        ApplyWindowIcon();
        SizeAndCenter();

        _server.Exited += OnServerExited;
        Closed += OnWindowClosed;

        // A WebView2 renderer can stop painting and stop answering input while
        // the rest of the window keeps working, which leaves a window that
        // looks alive but ignores every click; reloading it does not help
        // because the frozen process is the one that would reload. The page
        // therefore answers one script call per interval, and a page that
        // stops answering is reloaded and then rebuilt from scratch.
        _watchdog = DispatcherQueue.CreateTimer();
        _watchdog.Interval = WatchdogInterval;
        _watchdog.Tick += OnWatchdogTick;

        _ = InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            _settings = ShellSettings.Load();
            await EnsureServerAndNavigateAsync();
            _watchdog.Start();
        }
        catch (OperationCanceledException)
        {
            // The window is closing; nothing to show.
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to initialize: {ex.Message}", busy: false, error: true);
        }
    }

    /// <summary>
    /// Attach to an already-running server or spawn the configured command and
    /// wait until the URL answers, then load it in the WebView.
    /// </summary>
    private async Task EnsureServerAndNavigateAsync()
    {
        var url = _settings.Url.TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus($"Invalid server URL: {_settings.Url}", busy: false, error: true);
            return;
        }
        _serverUrl = url;

        SetStatus($"Checking {url} …", busy: true, error: false);
        if (await DshServerProcess.IsReachableAsync(url))
        {
            SetStatus($"Attached to server at {url}", busy: false, error: false);
            await NavigateAsync(url);
            return;
        }

        if (string.IsNullOrWhiteSpace(_settings.Command))
        {
            SetStatus($"No server at {url} and no launch command is configured", busy: false, error: true);
            return;
        }

        SetStatus("Starting dsh web server…", busy: true, error: false);
        var result = await _server.EnsureStartedAsync(_settings, _lifetimeCts.Token);
        if (result.Kind == DshServerProcess.StartResultKind.Failed)
        {
            var exit = result.ExitCode is int code ? $" (exit {code})" : string.Empty;
            var message = $"Server failed to start: {result.Message}{exit}";
            var remedy = _server.DiagnoseStartupFailure();
            SetStatus(message, busy: false, error: true);
            await ShowStartupFailureAsync(message, remedy);
            return;
        }

        SetStatus($"dsh web ready at {url}", busy: false, error: false);
        await NavigateAsync(url);
    }

    private async Task NavigateAsync(string url)
    {
        // From dsh 0.1.3-alpha.1 the Web GUI requires the process token the
        // server prints on its readiness line (`dsh web: http://…?token=…`),
        // which mints the browser cookie. Navigate to that authenticated URL
        // when the shell spawned the server; attach mode relies on the cookie
        // a previous spawn left in the persistent WebView2 profile.
        var target = url;
        if (_server.OwnsProcess)
        {
            for (var i = 0; i < 30 && _server.AuthenticatedUrl is null; i++)
            {
                await Task.Delay(100);
            }
            if (_server.AuthenticatedUrl is { } authenticated)
            {
                target = authenticated;
            }
        }
        await EnsureWebViewAsync();
        _serverUrl = target;
        _webView!.Source = new Uri(target);
    }

    // ── WebView lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Create the WebView control when the window does not have one yet and
    /// prepare its CoreWebView2: settings, lifecycle events, and the HTTP
    /// cache clear. A control whose CoreWebView2 is already created is reused.
    /// </summary>
    private async Task EnsureWebViewAsync()
    {
        if (_webView is { CoreWebView2: not null })
        {
            return;
        }
        var view = _webView ?? AttachWebView();
        _webView = view;
        await view.EnsureCoreWebView2Async();
        var core = view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.ProcessFailed += OnProcessFailed;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        // The server serves index.html without cache validators, so the
        // persistent WebView2 profile can keep serving a stale GUI (old
        // bundle revs calling old RPC contracts) after `dsh web` is rebuilt
        // by a git pull. Clear the HTTP cache on every launch so the GUI
        // always loads the current dist.
        await core.CallDevToolsProtocolMethodAsync("Network.clearBrowserCache", "{}");
    }

    private WebView2 AttachWebView()
    {
        var view = new WebView2();
        ViewHost.Children.Add(view);
        return view;
    }

    /// <summary>Remove and close the WebView control, releasing its browser process.</summary>
    private void CloseWebView()
    {
        var view = _webView;
        _webView = null;
        _watchdogFailures = 0;
        _navigationStartedAt = DateTime.MinValue;
        if (view is null)
        {
            return;
        }
        try
        {
            ViewHost.Children.Remove(view);
        }
        catch
        {
            // The element is already detached; closing it is what matters.
        }
        try
        {
            view.Close();
        }
        catch
        {
            // A control whose browser process is gone has nothing left to close.
        }
    }

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        _navigationStartedAt = DateTime.UtcNow;
        // The navigation URL carries the process token; log the address only.
        var address = Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Path) : args.Uri;
        _server.LogShellMessage($"webview: navigating to {address}");
    }

    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        _navigationStartedAt = DateTime.MinValue;
        if (!args.IsSuccess)
        {
            _server.LogShellMessage($"webview: navigation failed ({args.WebErrorStatus})");
            SetStatus($"The view failed to load ({args.WebErrorStatus})", busy: false, error: true);
            return;
        }
        _server.LogShellMessage("webview: navigation completed");
        SetStatus($"dsh web ready at {_settings.Url}", busy: false, error: false);
    }

    /// <summary>
    /// React to a failed WebView process. Losing the browser process takes the
    /// whole control with it, so the control is rebuilt; a failed renderer,
    /// GPU, or frame process leaves a window that never paints again, so the
    /// page is reloaded and the watchdog rebuilds the control when the reload
    /// does not bring it back.
    /// </summary>
    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        var kind = args.ProcessFailedKind;
        var reason = args.Reason;
        PostToUi(() =>
        {
            if (_closing)
            {
                return;
            }
            _server.LogShellMessage($"webview: process failed ({kind}, {reason})");
            if (kind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                _ = RecoverWebViewAsync($"the browser process exited ({reason})");
                return;
            }
            SetStatus($"The view lost its {kind} process — reloading", busy: true, error: false);
            Reload();
        });
    }

    /// <summary>
    /// Ask the loaded page whether it still runs. A frozen renderer never
    /// answers, and a closed control throws instead of answering; both count
    /// as a miss.
    /// </summary>
    private static async Task<bool> IsPageResponsiveAsync(WebView2 view)
    {
        var ping = view.CoreWebView2.ExecuteScriptAsync("1");
        var answer = ping.AsTask();
        try
        {
            if (await Task.WhenAny(answer, Task.Delay(WatchdogTimeout)) != answer)
            {
                // A renderer that never answers would otherwise keep the
                // operation pending for the life of the process.
                ping.Cancel();
                return false;
            }
            await answer;
            return true;
        }
        catch
        {
            // Only a closed or failed CoreWebView2 throws here.
            return false;
        }
    }

    private async void OnWatchdogTick(DispatcherQueueTimer sender, object args)
    {
        var view = _webView;
        if (_closing || _recovering || NavigationInFlight || view?.CoreWebView2 is null || _serverUrl.Length == 0)
        {
            return;
        }
        if (await IsPageResponsiveAsync(view))
        {
            _watchdogFailures = 0;
            return;
        }
        _watchdogFailures++;
        _server.LogShellMessage($"webview: heartbeat missed ({_watchdogFailures}/{WatchdogFailuresBeforeRebuild})");
        if (_watchdogFailures < WatchdogFailuresBeforeRebuild)
        {
            SetStatus("The view stopped answering — reloading", busy: true, error: false);
            Reload();
            return;
        }
        _watchdogFailures = 0;
        await RecoverWebViewAsync("the page stopped answering");
    }

    /// <summary>
    /// Replace the WebView control and load the current server URL again. This
    /// recovers a window whose page froze without touching the server process,
    /// so a recovery never costs the sessions the running server holds.
    /// </summary>
    private async Task RecoverWebViewAsync(string reason)
    {
        if (_recovering || _closing)
        {
            return;
        }
        _recovering = true;
        try
        {
            _server.LogShellMessage($"webview: rebuilding the view — {reason}");
            SetStatus($"Rebuilding the view — {reason}", busy: true, error: false);
            CloseWebView();
            // The replacement environment opens the same user data folder, so
            // the closed browser process needs a moment to release it.
            await Task.Delay(600);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await EnsureWebViewAsync();
                    break;
                }
                catch (Exception ex) when (attempt < 3)
                {
                    _server.LogShellMessage($"webview: rebuild attempt {attempt} failed: {ex.Message}");
                    await Task.Delay(1500);
                }
            }
            if (_serverUrl.Length > 0)
            {
                _webView!.Source = new Uri(_serverUrl);
            }
            SetStatus($"View rebuilt at {_settings.Url}", busy: false, error: false);
        }
        catch (Exception ex)
        {
            _server.LogShellMessage($"webview: rebuild failed: {ex}");
            SetStatus($"Rebuilding the view failed: {ex.Message} — use Restart server", busy: false, error: true);
        }
        finally
        {
            _recovering = false;
        }
    }

    // ── server lifecycle ────────────────────────────────────────────────────

    /// <summary>
    /// Show the launch failure with the remediation its output implies, so a
    /// checkout that needs a rebuild says so instead of only reporting an exit
    /// code. A failure that happens while the window is closing, or before the
    /// XAML root can host a dialog, stays in the status bar.
    /// </summary>
    private async Task ShowStartupFailureAsync(string message, string? remedy)
    {
        try
        {
            var body = new StackPanel { Spacing = 8 };
            body.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
            if (remedy is { Length: > 0 })
            {
                body.Children.Add(new TextBlock
                {
                    Text = remedy,
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                });
            }
            body.Children.Add(new TextBlock
            {
                Text = $"Log: {_server.LogPath}",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            });
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = "dsh web failed to start",
                Content = body,
                PrimaryButtonText = "Open log",
                SecondaryButtonText = "Retry",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
            };
            switch (await dialog.ShowAsync())
            {
                case ContentDialogResult.Primary:
                    OpenLog();
                    break;
                case ContentDialogResult.Secondary:
                    try
                    {
                        await EnsureServerAndNavigateAsync();
                    }
                    catch (OperationCanceledException)
                    {
                        // The window is closing.
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus(remedy is { Length: > 0 } ? $"{message} — {remedy}" : $"{message} — {ex.Message}", busy: false, error: true);
        }
    }

    private void OnServerExited(int exitCode)
    {
        PostToUi(() =>
        {
            if (_closing || _lifetimeCts.IsCancellationRequested)
            {
                return;
            }
            SetStatus($"dsh web exited (code {exitCode}) — retry to restart", busy: false, error: true);
        });
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _closing = true;
        _watchdog.Stop();
        _lifetimeCts.Cancel();
        _server.Stop();
        CloseWebView();
    }

    // ── toolbar actions ─────────────────────────────────────────────────────

    private void OnBack(object sender, RoutedEventArgs e)
    {
        if (_webView is { CanGoBack: true } view)
        {
            view.GoBack();
        }
    }

    private void OnForward(object sender, RoutedEventArgs e)
    {
        if (_webView is { CanGoForward: true } view)
        {
            view.GoForward();
        }
    }

    private void OnReload(object sender, RoutedEventArgs e) => Reload();

    private void OnReloadAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Reload();
    }

    private void Reload()
    {
        if (_webView?.CoreWebView2 is not null)
        {
            _webView.Reload();
        }
    }

    private async void OnRebuildView(object sender, RoutedEventArgs e)
    {
        await RecoverWebViewAsync("manual rebuild");
    }

    private async void OnRebuildViewAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RecoverWebViewAsync("manual rebuild");
    }

    private void OnHome(object sender, RoutedEventArgs e)
    {
        if (_serverUrl.Length > 0 && _webView is not null)
        {
            _webView.Source = new Uri(_serverUrl);
        }
    }

    private async void OnRestartServer(object sender, RoutedEventArgs e)
    {
        try
        {
            _server.Stop();
            await EnsureServerAndNavigateAsync();
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
    }

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        if (_serverUrl.Length == 0)
        {
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(_serverUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Failed to open browser: {ex.Message}", busy: false, error: true);
        }
    }

    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(_settings)
        {
            XamlRoot = Content.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings = dialog.Result;
            _settings.Save();
            try
            {
                _server.Stop();
                await EnsureServerAndNavigateAsync();
            }
            catch (OperationCanceledException)
            {
                // The window is closing.
            }
        }
    }

    private async void OnRetry(object sender, RoutedEventArgs e)
    {
        try
        {
            await EnsureServerAndNavigateAsync();
        }
        catch (OperationCanceledException)
        {
            // The window is closing.
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e) => OpenLog();

    /// <summary>Reveal the combined shell/server log in File Explorer.</summary>
    private void OpenLog()
    {
        var logPath = _server.LogPath;
        if (File.Exists(logPath))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{logPath}\"") { UseShellExecute = true });
            }
            catch
            {
                // Explorer is not available; nothing else to do.
            }
        }
    }

    // ── chrome helpers ──────────────────────────────────────────────────────

    private void SetStatus(string text, bool busy, bool error)
    {
        StatusText.Text = text;
        StatusRing.IsActive = busy;
        StatusRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Foreground = error
            ? GetBrush("SystemFillColorCriticalBrush")
            : GetBrush("TextFillColorSecondaryBrush");
        RetryButton.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        OpenLogButton.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
    }

    private static SolidColorBrush GetBrush(string key)
    {
        return (SolidColorBrush)Application.Current.Resources[key];
    }

    private void PostToUi(Action action)
    {
        DispatcherQueue.TryEnqueue(() => action());
    }

    private void ApplyWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                AppWindow.SetIcon(iconPath);
            }
            catch
            {
                // The icon is cosmetic; the embedded exe icon still applies.
            }
        }
    }

    private void SizeAndCenter()
    {
        try
        {
            const int width = 1360;
            const int height = 860;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var x = area.X + Math.Max(0, (area.Width - width) / 2);
            var y = area.Y + Math.Max(0, (area.Height - height) / 2);
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch
        {
            // Default placement is fine when the display cannot be queried.
        }
    }
}
