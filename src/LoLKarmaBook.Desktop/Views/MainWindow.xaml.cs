using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Diagnostics;
using Microsoft.Win32;
using LoLKarmaBook.Desktop.Infrastructure;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Desktop.ViewModels;

namespace LoLKarmaBook.Desktop.Views;

/// <summary>
/// Shell window. Owns only view concerns: window placement persistence, optional
/// always-on-top, closing safely, and wiring the OS file/folder dialogs the view
/// models ask for. It never steals focus and never talks to a game client.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _main;
    private readonly DesktopSettings _settings;
    private EndOfGameWindow? _endOfGame;
    private ReminderToastWindow? _reminderToast;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _closing;
    private bool _exitRequested;
    private LoLKarmaBook.Hexdata.HexdataBrowserLogin? _hexdataLogin;

    private const string TrayToolTip = "LoLKarmaBook";

    public MainWindow(MainViewModel main)
    {
        _main = main;
        _settings = main.PersistedSettings;
        InitializeComponent();
        DataContext = main;

        RestorePlacement();

        main.PropertyChanged += OnMainPropertyChanged;
        main.WorkspaceClosing += (_, _) => SavePlacement();
        main.ShutdownRequested += (_, _) => RequestExit();
        main.EndOfGameChanged += OnEndOfGameChanged;
        main.RemindersDetected += ShowReminderToast;
        Closed += (_, _) => main.EndOfGameChanged -= OnEndOfGameChanged;

        // Host-provided dialogs. If a dialog cannot be shown, the corresponding
        // view-model action reports it as unavailable rather than failing silently.
        main.Performance.PickJsonFile = PickJsonFile;
        main.Performance.CreateRiotClient = CreateLiveRiotClient;
        main.Performance.SaveTextFile = SaveTextFile;
        main.Settings.SaveFilePicker = SaveFile;
        main.Settings.PickBackupFile = PickBackupFile;
        main.Settings.OpenFolder = OpenFolder;
        main.Settings.PickDataRootFolder = PickDataRootFolder;
        main.PickClientDirectory = initialDirectory =>
        {
            var dialog = new OpenFolderDialog { Title = "选择 LOL 客户端目录（包含 LeagueClientUx.exe 或 lockfile）" };
            if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
                dialog.InitialDirectory = initialDirectory;
            return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
        };

        Confirmation.Handler = AskConfirmation;
        main.Records.OpenStandaloneEditor = ShowNoteEditorAsync;
        main.Hexdata.OpenLoginPage = OpenHexdataLogin;
        main.Hexdata.ReadSessionAsync = ReadHexdataSessionAsync;
        main.Hexdata.CurrentGameId = () => main.LatestAccountLabel;

        Topmost = main.AlwaysOnTop;
        Closing += OnClosing;
        StateChanged += OnWindowStateChanged;

        if (OperatingSystem.IsWindows() && _settings.MinimizeToTray)
        {
            CreateTrayIcon();
        }
    }

    /// <summary>
    /// The production Riot client. Its HttpClient gets a ceiling comfortably above
    /// the per-request timeout so the per-request token is the one that fires
    /// first, and nothing about the key is held here.
    /// </summary>
    private static LoLKarmaBook.Riot.RiotApiClient CreateLiveRiotClient(LoLKarmaBook.Riot.RiotApiOptions options)
        => new(requestTimeout: options.RequestTimeout + TimeSpan.FromSeconds(5));

    // ---------------- Tray ----------------

    /// <summary>
    /// Creates the notification-area icon. The icon exists for the lifetime of the
    /// window so a minimised or hidden window can be brought back.
    /// </summary>
    private void CreateTrayIcon()
    {
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("打开 LoLKarmaBook", null, (_, _) => ShowFromTray()));
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(new System.Windows.Forms.ToolStripMenuItem("退出", null, (_, _) => RequestExit()));

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = TrayToolTip,
                Icon = LoadTrayIcon(),
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.DoubleClick += (_, _) => ShowFromTray();
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowFromTray();
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            _main.Log.Warn("tray", "Notification-area icon unavailable", ex);
            _trayIcon = null;
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(new Uri("/Assets/app-icon.ico", UriKind.Relative));
        if (resource is null) return System.Drawing.SystemIcons.Application;
        using System.Drawing.Icon source = new(resource.Stream);
        // NotifyIcon shows the small icon from the same file; clone so the stream can close.
        return (System.Drawing.Icon)source.Clone();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Real exit from the tray menu: saves placement, flushes drafts and closes the database.</summary>
    private void RequestExit()
    {
        _exitRequested = true;
        Close();
    }

    private void DisposeTrayIcon()
    {
        System.Windows.Forms.NotifyIcon? icon = _trayIcon;
        _trayIcon = null;
        if (icon is null) return;
        icon.Visible = false;
        icon.ContextMenuStrip?.Dispose();
        icon.Dispose();
    }

    /// <summary>
    /// Shows or closes the bottom-right end-of-match card. It is shown without
    /// activation so a running game keeps focus.
    /// </summary>
    private void OnEndOfGameChanged(EndOfGameViewModel? viewModel)
    {
        EndOfGameWindow? existing = _endOfGame;
        _endOfGame = null;
        existing?.Close();

        if (viewModel is null || _closing) return;
        var window = new EndOfGameWindow(viewModel);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_endOfGame, window)) _endOfGame = null;
        };
        _endOfGame = window;
        window.Show();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AlwaysOnTop))
        {
            Topmost = _main.AlwaysOnTop;
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        // Restoring a minimized window returns it to the normal state without
        // activating it over a game window unless the user clicked the taskbar.
        if (WindowState == WindowState.Minimized)
        {
            SavePlacement();
            // Minimising is how the app gets out of the way during a match; it
            // keeps running (and detecting) from the notification area.
            if (_trayIcon is not null) Hide();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closing)
        {
            return;
        }

        // 「关闭」reaches the tray; only the tray menu's 退出 (or an explicit
        // shutdown request) really ends the process.
        if (!_exitRequested && _trayIcon is not null)
        {
            e.Cancel = true;
            SavePlacement();
            _main.TrySaveSettings();
            Hide();
            return;
        }

        _closing = true;
        DisposeTrayIcon();
        EndOfGameWindow? popup = _endOfGame;
        _endOfGame = null;
        popup?.Close();
        try
        {
            // Flush the DPAPI-protected draft before the database closes.
            _main.Current.FlushDraftOnClose();
            SavePlacement();
            _main.TrySaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _main.Log.Warn("close", "Flush/save failed during close", ex);
        }
    }

    private void RestorePlacement()
    {
        string? placement = _settings.WindowPlacement;
        if (string.IsNullOrWhiteSpace(placement))
        {
            return;
        }

        // Placement is only stored for the normal (restored) window state, so a
        // maximized window does not overwrite the size with screen bounds.
        // Format: "Normal|left|top|width|height" (no window state is restored).
        string[] parts = placement.Split('|');
        if (parts.Length != 5
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double left)
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double top)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double width)
            || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out double height)
            || !double.IsFinite(width) || !double.IsFinite(height)
            || width < MinWidth || height < MinHeight)
        {
            return;
        }

        // Guard against an off-screen position (monitor removed / DPI change).
        if (!IsOnVisibleScreen(left, top, width, height))
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private static bool IsOnVisibleScreen(double left, double top, double width, double height)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        return left >= virtualLeft - 20
               && top >= virtualTop - 20
               && left + Math.Min(width, 200) <= virtualRight
               && top + 80 <= virtualBottom;
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        var bounds = new Rect(Left, Top, Width, Height);
        _settings.WindowPlacement = string.Format(
            CultureInfo.InvariantCulture,
            "Normal|{0}|{1}|{2}|{3}",
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height);
    }

    // ---------------- Dialogs ----------------

    private async Task<string?> PickDataRootFolder(string initialDirectory)
    {
        await Task.CompletedTask;
        var dialog = new OpenFolderDialog { Title = "选择放置 LoLKarmaBook 数据的文件夹（记录、日志、备份都在这里）" };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private (string path, string content)? PickJsonFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "本地 JSON 数据 (*.json)|*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
            RestoreDirectory = true
        };

        return dialog.ShowDialog(this) == true
            ? (dialog.FileName, BoundedJsonFile.ReadText(dialog.FileName))
            : null;
    }

    private string? SaveTextFile(string suggestedName, string content)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存到本机",
            FileName = suggestedName,
            Filter = "JSON 文件 (*.json)|*.json|文本文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(_main.Paths.ExportDirectory)
                ? _main.Paths.ExportDirectory
                : null,
            OverwritePrompt = true,
            RestoreDirectory = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return null;
        }

        File.WriteAllText(dialog.FileName, content);
        return dialog.FileName;
    }

    private string? SaveFile(string suggestedName, string initialDirectory)
    {
        Directory.CreateDirectory(initialDirectory);
        var dialog = new SaveFileDialog
        {
            Title = "导出同机备份快照",
            FileName = suggestedName,
            Filter = "LoLKarmaBook 快照 (*.db)|*.db",
            DefaultExt = ".db",
            AddExtension = true,
            InitialDirectory = initialDirectory,
            OverwritePrompt = true,
            RestoreDirectory = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PickBackupFile(string title)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "LoLKarmaBook 快照 (*.db)|*.db|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(_main.Paths.BackupDirectory)
                ? _main.Paths.BackupDirectory
                : null,
            RestoreDirectory = true
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void OpenHexdataLogin()
    {
        _hexdataLogin?.Dispose();
        _hexdataLogin = new LoLKarmaBook.Hexdata.HexdataBrowserLogin(Path.Combine(_main.Paths.Root, "hexdata", "browser"));
        _ = LaunchHexdataAsync(_hexdataLogin);
    }

    private async Task LaunchHexdataAsync(LoLKarmaBook.Hexdata.HexdataBrowserLogin login)
    {
        try
        {
            await login.LaunchAsync(TimeSpan.FromSeconds(40));
        }
        catch (LoLKarmaBook.Hexdata.HexdataException ex)
        {
            _main.Status.Warn(ex.Message);
        }
    }

    private async Task<IReadOnlyList<string>?> ReadHexdataSessionAsync()
    {
        if (_hexdataLogin is null) return null;
        try { return await _hexdataLogin.ReadAsync(TimeSpan.FromSeconds(15)); }
        catch (LoLKarmaBook.Hexdata.HexdataException ex) { _main.Status.Warn(ex.Message); return null; }
    }
    /// <summary>Edits a stored record that has no row in the current match roster.</summary>
    private async Task<bool> ShowNoteEditorAsync(LoLKarmaBook.Core.StoredNote note)
    {
        var viewModel = new NoteEditViewModel(_main, note);
        var window = new NoteEditWindow(viewModel) { Owner = this };
        bool saved = window.ShowDialog() == true;
        await Task.CompletedTask;
        return saved;
    }


    /// <summary>Bottom-right notice for players already in the notebook; one at a time.</summary>
    private void ShowReminderToast(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return;
        _reminderToast?.Close();
        var toast = new ReminderToastWindow(names);
        _reminderToast = toast;
        toast.Closed += (_, _) =>
        {
            if (ReferenceEquals(_reminderToast, toast)) _reminderToast = null;
        };
        toast.Show();
        toast.StartCountdown();
    }
    private void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        // Owned window, normal priorities: opens Explorer without touching any game.
        var startInfo = new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        };
        Process.Start(startInfo);
    }

    private bool AskConfirmation(string title, string message, string confirmLabel)
    {
        MessageBoxResult result = MessageBox.Show(
            this,
            message + "\n\n按钮「确定」= " + confirmLabel + "；「取消」= 不执行。",
            title,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        return result == MessageBoxResult.OK;
    }
}
