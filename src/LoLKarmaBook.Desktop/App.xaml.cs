using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using LoLKarmaBook.Desktop.Infrastructure;
using LoLKarmaBook.Desktop.Runtime;
using LoLKarmaBook.Desktop.Security;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Desktop.ViewModels;
using LoLKarmaBook.Desktop.Views;

namespace LoLKarmaBook.Desktop;

/// <summary>
/// Application entry. Startup failures are reported in a readable dialog (and in
/// a requested failure log) instead of a silent crash.
/// </summary>
public partial class App : Application
{
    static App()
    {
        // Draw selection behind the glyphs so SelectionTextBrush is honored.
        // The legacy adorner layer would obscure text with an opaque highlight.
        AppContext.SetSwitch("Switch.System.Windows.Controls.Text.UseAdornerForTextboxSelectionRendering", false);
    }

    private MainViewModel? _main;
    private StartupInfo _startup = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        _startup = StartupInfo.Parse(e.Args);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        // The .NET SDK installs the invariant/English resources; make WPF's stock
        // strings (copy, paste, scroll tooltips) Chinese without extra downloads.
        ApplyChineseWpfLanguage();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        try
        {
            string dataRootPointer = DataRootLocation.GetPointerPath();
            string? pathChangeError = null;
            if (_startup.DataRoot is null)
            {
                try { await PathRelocation.ApplyPendingAsync(dataRootPointer); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                               or ArgumentException or System.Text.Json.JsonException or Microsoft.Data.Sqlite.SqliteException)
                {
                    pathChangeError = "位置修改未完成，原位置仍在使用；请重新选择空位置。";
                }
            }
            AppPaths paths = _startup.DataRoot is { } customRoot
                ? new AppPaths(customRoot)
                : AppPaths.FromOverrides(DataRootLocation.Read(dataRootPointer));
            paths.EnsureDirectories();

            var log = new DiagnosticLog(paths.LogDirectory);
            var protector = new DpapiTextProtector();
            log.Info("startup", $"LoLKarmaBook {typeof(App).Assembly.GetName().Version} starting on {Environment.OSVersion.VersionString}.");

            DesktopSettings settings = DesktopSettings.Load(paths.SettingsFile);
            _main = new MainViewModel(paths, log, protector, settings,
                enableRealtime: _startup.SmokeMilliseconds <= 0,
                dataRootPointerPath: dataRootPointer);
            var window = new MainWindow(_main);
            MainWindow = window;

            window.Closed += async (_, _) =>
            {
                try
                {
                    if (_main is not null)
                    {
                        await _main.DisposeAsync();
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                {
                    log.Warn("shutdown", "Dispose failed", ex);
                    if (_main?.Settings.HasPendingPathChanges == true)
                        MessageBox.Show("位置修改未完成，原数据保持不变。下次启动会重试。", "LoLKarmaBook", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                Shutdown();
            };

            window.Show();
            _main.Start();
            if (pathChangeError is not null) _main.Status.Error(pathChangeError);

            if (_startup.SmokeMilliseconds > 0)
            {
                StartSmokeTimer(_startup.SmokeMilliseconds, log);
            }
        }
        catch (Exception ex)
        {
            ReportStartupFailure(ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_main is not null)
            {
                // Block briefly so the proxy database closes even on abrupt exit.
                _main.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex) when (ex is AggregateException or InvalidOperationException or ObjectDisposedException)
        {
            // Nothing useful can be done during exit.
        }

        base.OnExit(e);
    }

    private void StartSmokeTimer(int milliseconds, DiagnosticLog log)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            log.Info("smoke", "Smoke run finished; closing window.");
            if (_main is { } main)
            {
                main.RequestShutdown();
            }
            else
            {
                Shutdown(0);
            }
        };
        timer.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // A UI exception must not strand the process; the user sees it and the app
        // stays in a defined state. Storage failures keep their input in the view.
        if (_main is not null)
        {
            WriteFailureLog(e.Exception, notifyUser: false);
            _main.ShowError(e.Exception, "界面出现未处理的错误");
            e.Handled = true;
            return;
        }

        if (MainWindow is { IsLoaded: true })
        {
            // The shell exists but not the view model: keep the window usable.
            WriteFailureLog(e.Exception, notifyUser: false);
            MessageBox.Show(
                "操作失败：" + StatusMessageViewModel.Describe(e.Exception),
                "LoLKarmaBook · 错误",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            e.Handled = true;
            return;
        }

        ReportStartupFailure(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            // A background failure must reach the user too; it is never swallowed.
            WriteFailureLog(ex, notifyUser: false);
            try
            {
                Dispatcher.BeginInvoke(() => _main?.ShowError(ex, "后台操作出现未处理的错误"));
            }
            catch (InvalidOperationException)
            {
                // Dispatcher already shut down; the failure log is all we can do.
            }
        }
    }

    private void ReportStartupFailure(Exception exception)
    {
        WriteFailureLog(exception, _startup.SmokeMilliseconds <= 0);
        try
        {
            MessageBox.Show(
                "LoLKarmaBook无法启动：\n" + (exception is DataDirectoryUnavailableException ? exception.Message : StatusMessageViewModel.Describe(exception))
                + "\n\n如果提示「DPAPI 不可用」，请确认在 Windows 上运行；本程序不会以明文方式保存。\n"
                + "诊断信息只包含异常类型，不含笔记内容。",
                "LoLKarmaBook · 启动失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Nothing else to do if even the message box fails.
        }

        Shutdown(1);
    }

    /// <summary>
    /// Writes a sanitized failure report. Exception messages can echo file paths or
    /// user input, so only the type, time, non-sensitive metadata and cleaned stack
    /// frames are recorded; never the raw message or note content.
    /// </summary>
    private void WriteFailureLog(Exception exception, bool notifyUser = true)
    {
        if (_startup.FailureLogPath is null)
        {
            if (notifyUser)
            {
                MessageBox.Show(
                    "LoLKarmaBook遇到无法恢复的错误，已安全停止。"
                    + "\n（本版本只在本机记录脱敏诊断信息；未写入原始错误消息。）",
                    "LoLKarmaBook · 运行错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return;
        }

        try
        {
            var builder = new StringBuilder();
            builder.AppendLine("LoLKarmaBook failed");
            builder.AppendLine("time: " + DateTimeOffset.Now.ToString("O"));
            builder.AppendLine("os: " + Environment.OSVersion.VersionString);
            builder.AppendLine("runtime: " + Environment.Version);
            builder.AppendLine("exception: " + exception.GetType().FullName);

            var visited = new HashSet<Exception>();
            Exception? current = exception;
            int depth = 0;
            while (current is not null && depth < 4 && visited.Add(current))
            {
                builder.AppendLine($"inner[{depth}]: " + current.GetType().FullName);
                if (current is System.Reflection.TargetInvocationException { InnerException: { } inner })
                {
                    current = inner;
                    depth++;
                    continue;
                }

                current = current.InnerException;
                depth++;
            }

            builder.AppendLine("stack:");
            builder.AppendLine(SanitizeStack(exception));
            File.WriteAllText(_startup.FailureLogPath, builder.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ignore: logging failure must not mask the original error.
        }
    }

    /// <summary>Keeps type and method names only; drops paths, messages and arguments.</summary>
    private static string SanitizeStack(Exception exception)
    {
        string? stack = exception.StackTrace;
        if (string.IsNullOrWhiteSpace(stack))
        {
            return "（无堆栈）";
        }

        var lines = new List<string>();
        foreach (string line in stack.Split('\n'))
        {
            string trimmed = line.Trim();
            int fileMarker = trimmed.IndexOf(" in ", StringComparison.Ordinal);
            if (fileMarker > 0)
            {
                trimmed = trimmed[..fileMarker];
            }

            lines.Add(trimmed.Length > 240 ? trimmed[..240] : trimmed);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static void ApplyChineseWpfLanguage()
    {
        try
        {
            var culture = new System.Globalization.CultureInfo("zh-CN");
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
            FrameworkElement.LanguageProperty.OverrideMetadata(
                typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
        catch (ArgumentException)
        {
            // An unusual ICU setup may reject the tag; English defaults remain.
        }
    }
}
