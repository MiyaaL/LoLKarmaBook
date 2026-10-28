using System.Globalization;
using System.IO;
using System.Windows.Input;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Infrastructure;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// 设置/关于: local data location, same-machine snapshot/restore, preferences
/// and data-protection status.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly PathRelocation _pathRelocation;
    private int _defaultReminderDays = NoteCategories.DefaultReminderDays;
    private string _reminderDaysText = NoteCategories.DefaultReminderDays.ToString(CultureInfo.InvariantCulture);
    private bool _isBusy;
    private string _maintenanceMessage = string.Empty;
    private string _maintenanceSeverity = "info";
    private string _logTail = string.Empty;
    private string _protectorStatus = string.Empty;
    private string _profileDescription = string.Empty;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        _pathRelocation = new PathRelocation(main.Paths, main.DataRootPointerPath);
        _defaultReminderDays = main.PersistedSettings.DefaultReminderDays;
        _reminderDaysText = _defaultReminderDays == 0
            ? string.Empty
            : _defaultReminderDays.ToString(CultureInfo.InvariantCulture);
        _maintenanceSeverity = "info";

        SavePreferencesCommand = new RelayCommand(SavePreferences);
        OpenDataFolderCommand = new RelayCommand(OpenDataFolder, () => !_main.IsMaintenanceBusy);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        RefreshLogCommand = new RelayCommand(() => LogTail = _main.Log.ReadTail());
        ExportSnapshotCommand = new AsyncRelayCommand(ExportSnapshotAsync, CanRunMaintenance);
        RestoreSnapshotCommand = new AsyncRelayCommand(RestoreSnapshotAsync, CanRunMaintenance);
        ClearProfileCommand = new AsyncRelayCommand(ClearProfileAsync, CanRunMaintenance);
        ResetPreferencesCommand = new RelayCommand(ResetPreferences);
        ChangeDataRootCommand = new AsyncRelayCommand(ChangeDataRootAsync, () => !_main.IsMaintenanceBusy,
            ex => _main.ShowError(ex, "更改数据位置失败"));
        ChangeDatabasePathCommand = new AsyncRelayCommand(() => ChangePathAsync("database"), CanRunMaintenance, ex => _main.ShowError(ex, "修改数据库位置失败"));
        ChangeSettingsPathCommand = new AsyncRelayCommand(() => ChangePathAsync("settings"), () => !_main.IsMaintenanceBusy, ex => _main.ShowError(ex, "修改设置文件位置失败"));
        ChangeLogPathCommand = new AsyncRelayCommand(() => ChangePathAsync("logs"), () => !_main.IsMaintenanceBusy, ex => _main.ShowError(ex, "修改日志位置失败"));
        RefreshEnvironment();
    }

    /// <summary>
    /// Reminder length for new notes. Callers bind <see cref="ReminderDaysText"/>
    /// for the day count and <see cref="IsPermanentReminder"/> for "永久".
    /// </summary>
    public IAsyncCommand ChangeDataRootCommand { get; }
    public IAsyncCommand ChangeDatabasePathCommand { get; }
    public IAsyncCommand ChangeSettingsPathCommand { get; }
    public IAsyncCommand ChangeLogPathCommand { get; }
    public bool HasPendingPathChanges => _pathRelocation.HasPending;
    public Task ApplyPendingPathChangesAsync() => _pathRelocation.ApplyAsync();

    /// <summary>Folder chooser supplied by the window; null in headless hosts.</summary>
    public Func<string, Task<string?>>? PickDataRootFolder { get; set; }

    public string AppVersion => _main.AppVersion;

    private AppPaths DisplayPaths => AppPaths.FromOverrides(_pathRelocation.Target);
    public string DataRoot => DisplayPaths.Root;

    public string SettingsFilePath => DisplayPaths.SettingsFile;

    public string LogFilePath => _main.Log.Path;
    public string LogDirectoryPath => DisplayPaths.LogDirectory;

    public string CurrentProfileId => _main.CurrentProfileId;

    public string CurrentDatabasePath => _main.IsProfileReady ? DisplayPaths.GetDatabasePath(_main.CurrentProfileId) : "（尚未打开）";

    /// <summary>
    /// The table shows paths relative to the data root when they live under it,
    /// so four long absolute paths do not all truncate to the same prefix; the
    /// tooltip and the folder picker keep the absolute location.
    /// </summary>
    public string DataRootDisplay => Compact(DataRoot);

    public string DatabaseDisplay => Compact(RelativeToRoot(CurrentDatabasePath));

    public string SettingsFileDisplay => Compact(RelativeToRoot(SettingsFilePath));

    public string LogDirectoryDisplay => Compact(RelativeToRoot(LogDirectoryPath));

    private string RelativeToRoot(string path)
    {
        string root = DisplayPaths.Root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length > 0
            && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return path[(root.Length + 1)..];
        }

        return path;
    }

    /// <summary>
    /// Keeps both ends of a long path so the file name stays visible in the table;
    /// the tooltip still carries the full value.
    /// </summary>
    private static string Compact(string path, int maxLength = 42)
    {
        if (path.Length <= maxLength)
        {
            return path;
        }

        int head = maxLength * 2 / 5;
        int tail = maxLength - head - 1;
        return path[..head] + "…" + path[^tail..];
    }

    public string ProfileDescription
    {
        get => _profileDescription;
        private set => SetProperty(ref _profileDescription, value);
    }

    public string ProtectorStatus
    {
        get => _protectorStatus;
        private set => SetProperty(ref _protectorStatus, value);
    }

    public bool AlwaysOnTop
    {
        get => _main.AlwaysOnTop;
        set
        {
            if (_main.AlwaysOnTop != value)
            {
                _main.AlwaysOnTop = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Minimising or closing keeps the process in the notification area.</summary>
    public bool MinimizeToTray
    {
        get => _main.PersistedSettings.MinimizeToTray;
        set
        {
            if (_main.PersistedSettings.MinimizeToTray == value) return;
            _main.PersistedSettings.MinimizeToTray = value;
            OnPropertyChanged();
            _main.TrySaveSettings();
        }
    }

    public bool IsTrayAvailable => OperatingSystem.IsWindows();

    public string MinimizeToTrayHint => IsTrayAvailable
        ? "最小化和关闭窗口都会收到托盘；右键托盘图标可以真正退出。只保留一份进程，实时检测继续运行。"
        : "仅 Windows 支持系统托盘；本机不可用。";

    /// <summary>
    /// The reminder length for new notes. Typed as a number of days; 0 means
    /// "永久" and is chosen with the checkbox rather than by typing a zero.
    /// </summary>
    public int DefaultReminderDays
    {
        get => _defaultReminderDays;
        set
        {
            int clamped = value <= 0 ? 0 : Math.Clamp(value, 1, MaxReminderDays);
            if (!SetProperty(ref _defaultReminderDays, clamped))
            {
                return;
            }

            _reminderDaysText = clamped == 0 ? string.Empty : clamped.ToString(CultureInfo.InvariantCulture);
            OnPropertyChanged(nameof(ReminderDaysText));
            OnPropertyChanged(nameof(IsPermanentReminder));
            OnPropertyChanged(nameof(CanEditReminderDays));
            OnPropertyChanged(nameof(ReminderDaysHint));
        }
    }

    public string ReminderDaysText
    {
        get => _reminderDaysText;
        set
        {
            if (!SetProperty(ref _reminderDaysText, value ?? string.Empty))
            {
                return;
            }

            OnPropertyChanged(nameof(ReminderDaysHint));
            if (!TryReadReminderDays(_reminderDaysText, out int days) || days == _defaultReminderDays)
            {
                return;
            }

            _defaultReminderDays = days;
            OnPropertyChanged(nameof(DefaultReminderDays));
            OnPropertyChanged(nameof(IsPermanentReminder));
            OnPropertyChanged(nameof(CanEditReminderDays));
            OnPropertyChanged(nameof(ReminderDaysHint));
        }
    }

    public bool IsPermanentReminder
    {
        get => _defaultReminderDays == 0;
        set
        {
            if (value == IsPermanentReminder)
            {
                return;
            }

            DefaultReminderDays = value ? 0 : NoteCategories.DefaultReminderDays;
        }
    }

    public bool CanEditReminderDays => !IsPermanentReminder;

    public string ReminderDaysHint => IsPermanentReminder
        ? "永久：新记录不会到期，始终参与再遇提醒。"
        : TryReadReminderDays(_reminderDaysText, out int days)
            ? $"新记录默认提醒 {days} 天。过期只关闭提醒，不删除记录。"
            : $"请输入 1–{MaxReminderDays} 之间的天数，或勾选「永久」。";

    private const int MaxReminderDays = 3650;

    private static bool TryReadReminderDays(string? text, out int days)
    {
        days = 0;
        if (string.IsNullOrWhiteSpace(text) || !int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return false;
        }

        days = Math.Clamp(parsed, 1, MaxReminderDays);
        return true;
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                BusyChanged?.Invoke(this, EventArgs.Empty);
                ExportSnapshotCommand.RaiseCanExecuteChanged();
                RestoreSnapshotCommand.RaiseCanExecuteChanged();
                ClearProfileCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string MaintenanceMessage
    {
        get => _maintenanceMessage;
        private set
        {
            if (SetProperty(ref _maintenanceMessage, value))
            {
                OnPropertyChanged(nameof(HasMaintenanceMessage));
            }
        }
    }

    public bool HasMaintenanceMessage => !string.IsNullOrEmpty(MaintenanceMessage);

    public string MaintenanceSeverity
    {
        get => _maintenanceSeverity;
        private set => SetProperty(ref _maintenanceSeverity, value);
    }

    public string LogTail
    {
        get => _logTail;
        private set => SetProperty(ref _logTail, value);
    }

    /// <summary>Lets the view disable maintenance buttons in the same pass.</summary>
    public event EventHandler? BusyChanged;

    // Injected by the window.
    public Func<string, string, string?>? SaveFilePicker { get; set; }

    public Func<string, string?>? PickBackupFile { get; set; }

    public Action<string>? OpenFolder { get; set; }

    public ICommand SavePreferencesCommand { get; }

    public ICommand OpenDataFolderCommand { get; }

    public ICommand OpenLogFolderCommand { get; }

    public ICommand RefreshLogCommand { get; }

    public IAsyncCommand ExportSnapshotCommand { get; }

    public IAsyncCommand RestoreSnapshotCommand { get; }

    public IAsyncCommand ClearProfileCommand { get; }

    public ICommand ResetPreferencesCommand { get; }

    public void RefreshEnvironment()
    {
        ProtectorStatus = _main.ProtectorStatus;
        ProfileDescription = _main.IsDemoProfile
            ? "模拟演示空间：只包含虚构玩家与 A/B 演示数据，与默认空间不共享数据库。"
            : "默认空间：你自己的笔记。切换空间时当前数据库会先关闭保存。";
        OnPropertyChanged(nameof(CurrentProfileId));
        OnPropertyChanged(nameof(CurrentDatabasePath));
        OnPropertyChanged(nameof(DatabaseDisplay));
        OnPropertyChanged(nameof(DataRootDisplay));
        OnPropertyChanged(nameof(SettingsFileDisplay));
        OnPropertyChanged(nameof(LogDirectoryDisplay));
        OnPropertyChanged(nameof(AlwaysOnTop));
        ChangeDataRootCommand.RaiseCanExecuteChanged();
        ChangeDatabasePathCommand.RaiseCanExecuteChanged();
        ChangeSettingsPathCommand.RaiseCanExecuteChanged();
        ChangeLogPathCommand.RaiseCanExecuteChanged();
        // These commands depend on IsProfileReady, which is only true after the
        // workspace opens; without this they stay disabled forever after launch.
        ExportSnapshotCommand.RaiseCanExecuteChanged();
        RestoreSnapshotCommand.RaiseCanExecuteChanged();
        ClearProfileCommand.RaiseCanExecuteChanged();
        LogTail = _main.Log.ReadTail();
    }

    /// <summary>
    /// Asks for a new data folder and records it for the next launch. Nothing is
    /// moved or reopened: the current workspace keeps writing to the old root
    /// until the app restarts.
    /// </summary>
    private Task ChangeDataRootAsync() => ChangePathAsync("root");

    private async Task ChangePathAsync(string item)
    {
        string profileId = _main.CurrentProfileId;
        if (PickDataRootFolder is null)
        {
            _main.Status.Warn("当前宿主无法选择文件夹。");
            return;
        }

        string current = item switch
        {
            "database" => Path.GetDirectoryName(CurrentDatabasePath)!,
            "settings" => Path.GetDirectoryName(SettingsFilePath)!,
            "logs" => LogDirectoryPath,
            _ => DataRoot
        };
        string? chosen = await PickDataRootFolder(current).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(chosen)) return;
        if (item == "database" && profileId != _main.CurrentProfileId)
        {
            _main.Status.Warn("空间已变化，请重新选择位置。");
            return;
        }
        string? root = DataRootLocation.Normalize(chosen);
        if (root is null)
        {
            _main.Status.Error("该文件夹路径无效。");
            return;
        }

        try
        {
            PathOverrides target = _pathRelocation.Target.Clone();
            switch (item)
            {
                case "database": target.ProfileDatabases[profileId] = Path.Combine(root, ProfilePaths.DatabaseFileName); break;
                case "settings": target.SettingsFile = Path.Combine(root, "desktop-settings.json"); break;
                case "logs": target.LogDirectory = root; break;
                default: target.DataRoot = root; break;
            }
            _pathRelocation.Queue(target);
            _main.Status.Success("位置已记录，退出并重启后生效。");
            OnPropertyChanged(nameof(DataRoot));
            OnPropertyChanged(nameof(CurrentDatabasePath));
            OnPropertyChanged(nameof(DatabaseDisplay));
            OnPropertyChanged(nameof(SettingsFilePath));
            OnPropertyChanged(nameof(SettingsFileDisplay));
            OnPropertyChanged(nameof(LogDirectoryPath));
            OnPropertyChanged(nameof(LogDirectoryDisplay));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Status.Error("位置未修改：" + StatusMessageViewModel.Describe(ex));
        }
    }

    public void SavePreferences()
    {
        ApplyToSettings();
        // TrySaveSettings reports exactly once (in the status strip) on failure.
        if (_main.TrySaveSettings())
        {
            _main.Status.Success("设置已保存到本机。");
        }
    }

    private void ApplyToSettings()
    {
        _main.PersistedSettings.DefaultReminderDays = DefaultReminderDays;
        _main.PersistedSettings.AlwaysOnTop = _main.AlwaysOnTop;
    }

    private void ResetPreferences()
    {
        DefaultReminderDays = NoteCategories.DefaultReminderDays;
        _main.AlwaysOnTop = false;
        MinimizeToTray = true;
        OnPropertyChanged(nameof(AlwaysOnTop));
        ApplyToSettings();
        if (_main.TrySaveSettings())
        {
            _main.Status.Show("设置已恢复默认值（置顶关闭、提醒 365 天）。", StatusSeverity.Info);
        }
    }

    private void OpenDataFolder()
    {
        if (OpenFolder is null)
        {
            MaintenanceMessage = "不可用：当前宿主无法打开资源管理器路径。";
            MaintenanceSeverity = "warning";
            return;
        }

        try
        {
            Directory.CreateDirectory(_main.Paths.DataRoot);
            OpenFolder(_main.Paths.DataRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MaintenanceMessage = "打开数据目录失败：" + StatusMessageViewModel.Describe(ex);
            MaintenanceSeverity = "error";
        }
    }

    private void OpenLogFolder()
    {
        if (OpenFolder is null)
        {
            MaintenanceMessage = "不可用：当前宿主无法打开资源管理器路径。";
            MaintenanceSeverity = "warning";
            return;
        }

        try
        {
            OpenFolder(_main.Paths.LogDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MaintenanceMessage = "打开日志目录失败：" + StatusMessageViewModel.Describe(ex);
            MaintenanceSeverity = "error";
        }
    }

    private bool CanRunMaintenance() =>
        _main.IsProfileReady && !_isBusy && !_main.IsMaintenanceBusy;

    private async Task ExportSnapshotAsync()
    {
        if (!_main.IsProfileReady || SaveFilePicker is null)
        {
            MaintenanceMessage = SaveFilePicker is null
                ? "当前宿主无法选择保存位置，备份不可用。"
                : "尚未打开笔记空间。";
            MaintenanceSeverity = "warning";
            return;
        }

        string suggested = $"lolkarmabook-{_main.CurrentProfileId}-{DateTime.Now:yyyyMMdd-HHmmss}.db";
        string? target = SaveFilePicker(suggested, _main.Paths.BackupDirectory);
        if (target is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            string profileId = _main.CurrentProfileId;
            await _main.RunMaintenanceAsync("正在生成快照…", async lease =>
            {
                // Consistency backup of the open SQLite database; profile changes are
                // excluded for the whole operation.
                await SqliteProfileMaintenance.BackupAsync(
                        lease.Workspace.DatabasePath,
                        target,
                        profileId)
                    .ConfigureAwait(true);
            }).ConfigureAwait(true);

            MaintenanceMessage = "备份完成：" + target
                                 + "。备份与当前 Windows 用户绑定（内容受 DPAPI 保护），不能直接给其他电脑/账户使用。";
            MaintenanceSeverity = "success";
            _main.Log.Info("maintenance", "Backup completed.");
        }
        catch (Exception ex)
        {
            MaintenanceMessage = "备份失败：" + StatusMessageViewModel.Describe(ex);
            MaintenanceSeverity = "error";
            _main.Log.Error("maintenance", "Backup failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreSnapshotAsync()
    {
        if (!_main.IsProfileReady || PickBackupFile is null)
        {
            MaintenanceMessage = PickBackupFile is null
                ? "当前宿主无法选择文件，恢复不可用。"
                : "尚未打开笔记空间。";
            MaintenanceSeverity = "warning";
            return;
        }

        string profileId = _main.CurrentProfileId;
        string? source = PickBackupFile("选择同机备份数据库（.db）");
        if (source is null)
        {
            return;
        }

        bool confirmed = Confirmation.Ask(
            "从备份恢复当前空间？",
            "恢复会用备份内容覆盖当前空间的记录，之后无法在应用内撤销。\n"
            + "只允许恢复属于当前空间（" + profileId + "）的备份；其他空间、损坏或无法解密的文件会在写入前被拒绝。\n"
            + "恢复期间空间会短暂关闭并自动重新打开。",
            "确认恢复");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _main.RestoreBackupAsync(source, profileId).ConfigureAwait(true);

            await _main.Current.RefreshAsync().ConfigureAwait(true);
            await _main.Records.ReloadAfterMaintenanceAsync().ConfigureAwait(true);
            MaintenanceMessage = "已从备份恢复当前空间：" + source;
            MaintenanceSeverity = "success";
            _main.Log.Info("maintenance", "Restore completed.");
        }
        catch (Exception ex)
        {
            MaintenanceMessage = "恢复失败：" + StatusMessageViewModel.Describe(ex)
                                 + "（不是当前空间的备份、损坏或无法解密时，不会写入任何内容。）";
            MaintenanceSeverity = "error";
            _main.Log.Error("maintenance", "Restore failed", ex);
        }
        finally
        {
            IsBusy = false;
            RefreshEnvironment();
        }
    }

    private async Task ClearProfileAsync()
    {
        if (!_main.IsProfileReady)
        {
            return;
        }

        string profileId = _main.CurrentProfileId;
        bool confirmed = Confirmation.Ask(
            "清空当前空间的所有笔记？",
            "将删除当前空间（" + profileId + "）的所有记录、类别、账号关联与提醒状态。"
            + "其他空间不受影响；本操作无法在应用内撤销。\n"
            + "建议先「导出快照」备份。",
            "确认清空");
        if (!confirmed)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _main.RunMaintenanceAsync("正在清空当前空间…", lease =>
                lease.Notebook.ClearAllAsync()).ConfigureAwait(true);

            await _main.Current.RefreshAsync().ConfigureAwait(true);
            await _main.Records.ReloadAfterMaintenanceAsync().ConfigureAwait(true);
            MaintenanceMessage = "已清空当前空间的笔记。";
            MaintenanceSeverity = "success";
            _main.Log.Info("maintenance", "Profile cleared.");
        }
        catch (Exception ex)
        {
            MaintenanceMessage = "清空失败：" + StatusMessageViewModel.Describe(ex);
            MaintenanceSeverity = "error";
            _main.Log.Error("maintenance", "Clear failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
