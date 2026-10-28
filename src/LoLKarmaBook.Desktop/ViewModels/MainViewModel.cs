using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Infrastructure;
using LoLKarmaBook.Desktop.Security;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// Application shell: owns the open profile, the shared session state, the page
/// view models and the global status strip. Switching profile closes the current
/// database before opening the next one so no write can land in the wrong space.
///
/// Profile selection is serialised through one task chain: a selection made while
/// a switch or maintenance operation is running is applied after it finishes, so
/// the UI never shows a space that is not the open database.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly string[] TabKeys =
    {
        "current", "records", "performance", "settings"
    };

    private static readonly TimeSpan SwitchIdleTimeout = TimeSpan.FromSeconds(30);

    private readonly AppPaths _paths;
    private readonly DiagnosticLog _log;
    private readonly DpapiTextProtector _protector;
    private ProfileWorkspace? _workspace;
    private SessionStore? _sessionStore;
    private AutosaveJournal? _autosave;
    private string _activeTab = "current";
    private bool _isBusy;
    private string _busyText = string.Empty;
    private int _maintenanceDepth;
    private int _commandGeneration;
    private bool _isShuttingDown;
    private ProfileDescriptor? _selectedProfile;
    private string _profileSwitchError = string.Empty;
    private DesktopSettings _preferences;
    private Task _switchChain = Task.CompletedTask;
    private CancellationTokenSource? _switchCts;
    private bool _switchQueued;
    private Task? _disposeTask;

    public MainViewModel(AppPaths paths, DiagnosticLog log, DpapiTextProtector protector)
        : this(paths, log, protector, DesktopSettings.Load(paths.SettingsFile))
    {
    }

    public MainViewModel(AppPaths paths, DiagnosticLog log, DpapiTextProtector protector, DesktopSettings settings,
        bool enableRealtime = false, LoLKarmaBook.Client.ILcuClientService? clientService = null,
        string? dataRootPointerPath = null)
    {
        _paths = paths;
        DataRootPointerPath = dataRootPointerPath ?? DataRootLocation.GetPointerPath();
        _log = log;
        _protector = protector;
        RiotKeys = new RiotKeyStore(paths, protector, log);
        _preferences = settings ?? throw new ArgumentNullException(nameof(settings));
        AlwaysOnTop = _preferences.AlwaysOnTop;

        Status = new StatusMessageViewModel();
        Profiles = new ObservableCollection<ProfileDescriptor>(ProfileCatalog.Discover(paths.DataRoot, paths.GetOverrides().ProfileDatabases));

        Current = new CurrentMatchViewModel(this);
        Records = new RecordsViewModel(this);
        Performance = new PerformanceViewModel(this);
        Settings = new SettingsViewModel(this);
        Tags = new TagCatalogService(paths.Root);
        Hexdata = new HexdataViewModel(Path.Combine(paths.Root, "hexdata"), protector, tags: Tags);

        SelectTabCommand = new RelayCommand<string>(SelectTab);
        RefreshCommand = new AsyncRelayCommand(RefreshActiveTabAsync, () => IsProfileReady,
            ex => Status.Report(ex, "刷新页面失败", _log));
        DismissStatusCommand = new RelayCommand(Status.Clear);
        InitializeRealtime(enableRealtime, clientService);
        ProfileSwitched += (_, _) => OnProfileSwitched();
        InitializeEndOfGame();

        AppVersion = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(MainViewModel).Assembly)
            ?.InformationalVersion.Split('+')[0]
            ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        ProtectorStatus = protector.DescribeAvailability();
        IsWindows = OperatingSystem.IsWindows();
    }

    public string AppVersion { get; }

    public string ProtectorStatus { get; }

    public bool IsWindows { get; }

    public StatusMessageViewModel Status { get; }

    public ObservableCollection<ProfileDescriptor> Profiles { get; }

    /// <summary>
    /// Two-way bound by the profile selector. Assignments are serialised; while a
    /// switch or maintenance action runs the setter only records the request and
    /// the effective selection is applied once the chain reaches it.
    /// </summary>
    public ProfileDescriptor? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value is null || ReferenceEquals(_selectedProfile, value))
            {
                return;
            }

            if (_isShuttingDown || IsMaintenanceBusy)
            {
                RevertSelection();
                return;
            }

            // Optimistic UI: show the requested space, then make it real.
            SetProperty(ref _selectedProfile, value);
            EnqueueSwitch(value);
        }
    }

    public string ActiveTab
    {
        get => _activeTab;
        private set
        {
            if (SetProperty(ref _activeTab, value))
            {
                OnPropertyChanged(nameof(IsCurrentTab));
                OnPropertyChanged(nameof(IsRecordsTab));
                OnPropertyChanged(nameof(IsPerformanceTab));
                OnPropertyChanged(nameof(IsSettingsTab));
            }
        }
    }

    public bool IsCurrentTab => ActiveTab == "current";
    public bool IsRecordsTab => ActiveTab == "records";
    public bool IsPerformanceTab => ActiveTab == "performance";
    public bool IsSettingsTab => ActiveTab == "settings";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string BusyText
    {
        get => _busyText;
        private set => SetProperty(ref _busyText, value);
    }

    private bool _isProfileSwitching;

    /// <summary>True in the window between "opening" and "workspace ready".</summary>
    public bool IsProfileSwitching
    {
        get => _isProfileSwitching;
        private set
        {
            if (SetProperty(ref _isProfileSwitching, value))
            {
                OnPropertyChanged(nameof(CanChangeProfile));
            }
        }
    }

    /// <summary>
    /// The profile selector is disabled while a switch is running (selection would
    /// otherwise show a space that is not open yet) or during maintenance.
    /// </summary>
    public bool CanChangeProfile => !_isProfileSwitching && !IsMaintenanceBusy && !_isShuttingDown;

    /// <summary>A backup/restore/clear/reset is running; profile changes wait for it.</summary>
    public bool IsMaintenanceBusy => _maintenanceDepth > 0;

    private void SetMaintenanceDepth(int depth)
    {
        _maintenanceDepth = depth;
        OnPropertyChanged(nameof(IsMaintenanceBusy));
        OnPropertyChanged(nameof(CanChangeProfile));
    }

    public bool IsProfileReady => _workspace is not null;

    public string ProfileSwitchError
    {
        get => _profileSwitchError;
        private set
        {
            if (SetProperty(ref _profileSwitchError, value))
            {
                OnPropertyChanged(nameof(HasProfileSwitchError));
            }
        }
    }

    public bool HasProfileSwitchError => !string.IsNullOrEmpty(ProfileSwitchError);

    public bool IsDemoProfile => CurrentProfileId == ProfileCatalog.DemoProfileId;

    public string DataRootDisplay => _paths.DataRoot;

    /// <summary>Id of the actually open database (empty when none is open).</summary>
    public string CurrentProfileId => _workspace?.ProfileId ?? string.Empty;

    public string CurrentProfileTitle =>
        _selectedProfile?.Title ?? NotePresenter.ProfileLabel(CurrentProfileId);

    public CurrentMatchViewModel Current { get; }

    public RecordsViewModel Records { get; }

    public PerformanceViewModel Performance { get; }

    public HexdataViewModel Hexdata { get; }

    /// <summary>Official champion/item catalog used by the tag search box.</summary>
    public TagCatalogService Tags { get; }

    public SettingsViewModel Settings { get; }

    public ProfileWorkspace Workspace =>
        _workspace ?? throw new InvalidOperationException("尚未打开任何笔记空间。");

    /// <summary>Open workspace, or null. Read it once per operation.</summary>
    public ProfileWorkspace? TryGetWorkspace() => _workspace;

    /// <summary>Encryptor used for stored notes and drafts (needed by restore).</summary>
    public DpapiTextProtector Protector => _protector;

    /// <summary>DPAPI-backed storage for the Riot API key and lookup choices.</summary>
    public RiotKeyStore RiotKeys { get; }

    /// <summary>Closes the open database without opening another one.</summary>
    public Task<ProfileWorkspace?> CloseWorkspaceAsync(string busyText = "正在关闭笔记空间…") =>
        EnqueueClose(busyText);

    public NotebookService Notebook => Workspace.Notebook;

    public SessionStore SessionStore =>
        _sessionStore ?? throw new InvalidOperationException("尚未打开任何笔记空间。");

    public AutosaveJournal Autosave =>
        _autosave ?? throw new InvalidOperationException("尚未打开任何笔记空间。");

    public AppPaths Paths => _paths;

    /// <summary>Small pointer file that remembers a user-chosen data root.</summary>
    public string DataRootPointerPath { get; }

    /// <summary>True when the current data root is not the default install-directory one.</summary>
    public bool IsDataRootSelected => DataRootLocation.ReadSelectedRoot(DataRootPointerPath) is not null;

    /// <summary>Persisted non-sensitive desktop preferences (not the settings page VM).</summary>
    public DesktopSettings PersistedSettings => _preferences;

    public void ReplaceSettings(DesktopSettings settings)
    {
        _preferences = settings ?? throw new ArgumentNullException(nameof(settings));
        AlwaysOnTop = settings.AlwaysOnTop;
    }

    public DiagnosticLog Log => _log;

    public ICommand SelectTabCommand { get; }

    public IAsyncCommand RefreshCommand { get; }

    public ICommand DismissStatusCommand { get; }

    /// <summary>Optional always-on-top is a user choice, never forced.</summary>
    public bool AlwaysOnTop
    {
        get => _preferences.AlwaysOnTop;
        set
        {
            if (_preferences.AlwaysOnTop != value)
            {
                _preferences.AlwaysOnTop = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Raised with the opened workspace after a successful switch.</summary>
    public event EventHandler<ProfileWorkspace>? ProfileSwitched;

    /// <summary>
    /// Raised synchronously right before the open workspace is closed. MainWindow
    /// uses it to persist window placement while preferences are still valid.
    /// </summary>
    public event EventHandler? WorkspaceClosing;

    /// <summary>Raised when the window should close for a safe shutdown.</summary>
    public event EventHandler? ShutdownRequested;

    public void Start()
    {
        string? preferred = _preferences.LastProfileId;
        ProfileDescriptor initial = Profiles.FirstOrDefault(p => p.Id == preferred) ?? Profiles[0];
        _selectedProfile = initial;
        OnPropertyChanged(nameof(SelectedProfile));
        _ = StartAndConnectAsync(initial);
    }

    /// <summary>
    /// Switches to <paramref name="profileId"/> and resolves to the open workspace,
    /// or null when the switch was refused/failed. Waiters see the real outcome
    /// instead of polling <see cref="IsProfileReady"/>.
    /// </summary>
    public Task<ProfileWorkspace?> SwitchProfileAsync(ProfileDescriptor profile) =>
        EnqueueSwitch(profile);

    public void SelectTab(string? tab)
    {
        if (tab is null || !TabKeys.Contains(tab, StringComparer.Ordinal))
        {
            return;
        }

        ActiveTab = tab;
        _ = RefreshActiveTabAsync();
    }

    public async Task RefreshActiveTabAsync()
    {
        if (!IsProfileReady || _isShuttingDown)
        {
            return;
        }

        try
        {
            switch (ActiveTab)
            {
                case "current":
                    await Current.RefreshAsync().ConfigureAwait(true);
                    break;
                case "records":
                    await Records.RefreshAsync().ConfigureAwait(true);
                    break;
                case "settings":
                    Settings.RefreshEnvironment();
                    break;
            }
        }
        catch (Exception ex)
        {
            Status.Report(ex, "刷新页面失败", _log);
        }
    }

    public void RequestShutdown()
    {
        if (_isShuttingDown)
        {
            return;
        }

        _isShuttingDown = true;
        ShutdownRequested?.Invoke(this, EventArgs.Empty);
    }

    public void ShowError(Exception exception, string context) => Status.Report(exception, context, _log);

    // ---------------- Workspace access ----------------

    /// <summary>
    /// Takes the workspace gate for a notebook operation. The returned lease also
    /// carries the exact workspace it was taken on, so callers must use
    /// <see cref="WorkspaceLease.Workspace"/> rather than re-reading this object.
    /// Throws when no space is open.
    /// </summary>
    public async Task<WorkspaceLease> AcquireWorkspaceAsync()
    {
        ProfileWorkspace workspace = _workspace
            ?? throw new InvalidOperationException("尚未打开任何笔记空间。");
        int generation = _commandGeneration;
        IDisposable gate = await workspace.EnterAsync(CancellationToken.None).ConfigureAwait(true);
        return new WorkspaceLease(workspace, gate, generation);
    }

    /// <summary>Throws if the profile changed since the caller captured its data.</summary>
    public void EnsureSameProfile(int generation)
    {
        if (generation != _commandGeneration)
        {
            throw new InvalidOperationException(
                "笔记空间已在操作过程中切换；本次结果未生效，请在新空间中重试。");
        }
    }

    /// <summary>
    /// Runs an irreversible maintenance action (backup/restore/clear/reset) with
    /// profile switching excluded for its whole duration. The action gets a lease
    /// whose workspace is fixed at the start.
    /// </summary>
    public async Task RunMaintenanceAsync(string busyText, Func<WorkspaceLease, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await AwaitSwitchIdleAsync().ConfigureAwait(true);
            ProfileWorkspace workspace = _workspace
                ?? throw new InvalidOperationException("尚未打开任何笔记空间。");
            int generation = _commandGeneration;
            SetMaintenanceDepth(_maintenanceDepth + 1);
            try
            {
                using IDisposable gate = await workspace.EnterAsync(CancellationToken.None).ConfigureAwait(true);
                EnsureSameProfile(generation);
                await action(new WorkspaceLease(workspace, gate, generation)).ConfigureAwait(true);
            }
            finally
            {
                SetMaintenanceDepth(_maintenanceDepth - 1);
                RaiseCommands();
            }

            // Apply any selection the user asked for while maintenance was running.
            FlushQueuedSwitch();
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
            RaiseCommands();
        }
    }

    /// <summary>Keep the profile unavailable for the entire close/restore/reopen operation.</summary>
    public async Task RestoreBackupAsync(string source, string expectedProfileId)
    {
        await AwaitSwitchIdleAsync().ConfigureAwait(true);
        if (IsMaintenanceBusy || _isShuttingDown || CurrentProfileId != expectedProfileId)
            throw new InvalidOperationException("笔记空间已变化或正在维护，恢复未执行。");

        ProfileWorkspace outgoing = Workspace;
        ProfileDescriptor descriptor = Profiles.Single(p => p.Id == expectedProfileId);
        string databasePath = outgoing.DatabasePath;
        SetMaintenanceDepth(_maintenanceDepth + 1);
        IsBusy = true;
        BusyText = "正在恢复备份…";
        try
        {
            Current.PrepareForProfileSwitch();
            Records.PrepareForProfileSwitch();
            WorkspaceClosing?.Invoke(this, EventArgs.Empty);
            _workspace = null;
            _sessionStore = null;
            _autosave = null;
            _commandGeneration++;
            OnPropertyChanged(nameof(IsProfileReady));
            await outgoing.DisposeAsync().ConfigureAwait(true);
            try
            {
                await LoLKarmaBook.Storage.SqliteProfileMaintenance.RestoreAsync(
                    source, databasePath, expectedProfileId, _protector).ConfigureAwait(true);
            }
            finally
            {
                // Reopen even after validation rejects a backup, retaining the original data.
                if (await OpenProfileCoreAsync(descriptor, CancellationToken.None).ConfigureAwait(true) is null)
                    throw new InvalidOperationException("恢复操作结束，但空间重新打开失败：" + ProfileSwitchError);
            }
        }
        finally
        {
            SetMaintenanceDepth(_maintenanceDepth - 1);
            IsBusy = false;
            BusyText = string.Empty;
            ReconcileSelection();
            RaiseCommands();
        }
    }

    private async Task AwaitSwitchIdleAsync()
    {
        for (int attempt = 0; attempt < 3 && IsProfileSwitching; attempt++)
        {
            Task chain;
            lock (this)
            {
                chain = _switchChain;
            }

            Task finished = await Task.WhenAny(chain, Task.Delay(SwitchIdleTimeout)).ConfigureAwait(true);
            if (!ReferenceEquals(finished, chain))
            {
                throw new TimeoutException("另一个空间切换尚未结束；请稍后重试，本次操作未执行。");
            }

            await chain.ConfigureAwait(true);
        }

        if (IsProfileSwitching)
        {
            throw new InvalidOperationException("空间正在切换；请稍后重试，本次操作未执行。");
        }
    }

    // ---------------- Profile switching ----------------

    private Task<ProfileWorkspace?> EnqueueSwitch(ProfileDescriptor profile)
    {
        var completion = new TaskCompletionSource<ProfileWorkspace?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this)
        {
            if (_isShuttingDown)
            {
                completion.TrySetResult(null);
                return completion.Task;
            }

            if (IsMaintenanceBusy)
            {
                // Safety net: the selector is disabled during maintenance, but a
                // programmatic change must not slip through either.
                _log.Warn("profile", "Selection during maintenance was refused.");
                completion.TrySetResult(null);
                return completion.Task;
            }

            if (IsProfileSwitching)
            {
                // Serialised: the chain runs this switch after the in-flight one.
                _switchQueued = true;
            }

            _switchCts ??= new CancellationTokenSource();
            CancellationToken token = _switchCts.Token;
            Task previousChain = _switchChain;
            _switchChain = RunSwitchAsync(profile, token, completion, previousChain);
        }

        return completion.Task;
    }

    private void FlushQueuedSwitch()
    {
        ProfileDescriptor? desired;
        bool queued;
        lock (this)
        {
            queued = _switchQueued;
            _switchQueued = false;
            desired = _selectedProfile;
        }

        if (queued && desired is not null && !_isShuttingDown && !IsMaintenanceBusy
            && CurrentProfileId != desired.Id)
        {
            SwitchProfileAsync(desired);
        }
    }

    // Id of the last successfully opened profile, used to reconcile the selector.
    private string? _pinnedProfileId;

    private Task<ProfileWorkspace?> EnqueueClose(string busyText)
    {
        var completion = new TaskCompletionSource<ProfileWorkspace?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (this)
        {
            if (_isShuttingDown || IsMaintenanceBusy)
            {
                completion.TrySetResult(null);
                return completion.Task;
            }

            if (IsProfileSwitching)
            {
                _switchQueued = true;
            }

            _switchCts ??= new CancellationTokenSource();
            CancellationToken token = _switchCts.Token;
            _switchChain = RunCloseAsync(busyText, token, completion, _switchChain);
        }

        return completion.Task;
    }

    private async Task RunCloseAsync(
        string busyText,
        CancellationToken token,
        TaskCompletionSource<ProfileWorkspace?> completion,
        Task predecessor)
    {
        try
        {
            await predecessor.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn("profile", "Previous switch task failed", ex);
        }

        if (token.IsCancellationRequested || _isShuttingDown)
        {
            completion.TrySetResult(null);
            return;
        }

        IsProfileSwitching = true;
        IsBusy = true;
        BusyText = busyText;
        try
        {
            Current.PrepareForProfileSwitch();
            Records.PrepareForProfileSwitch();
            if (_workspace is not null)
            {
                WorkspaceClosing?.Invoke(this, EventArgs.Empty);
                ProfileWorkspace outgoing = _workspace;
                _workspace = null;
                _sessionStore = null;
                _autosave = null;
                _commandGeneration++;
                OnPropertyChanged(nameof(IsProfileReady));
                await outgoing.DisposeAsync().ConfigureAwait(true);
            }

            completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            completion.TrySetResult(null);
            FailOpen(null, ex);
        }
        finally
        {
            IsProfileSwitching = false;
            IsBusy = false;
            BusyText = string.Empty;
            RaiseCommands();
        }
    }

    private async Task RunSwitchAsync(
        ProfileDescriptor profile,
        CancellationToken token,
        TaskCompletionSource<ProfileWorkspace?> completion,
        Task predecessor)
    {
        try
        {
            await predecessor.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _log.Warn("profile", "Previous switch task failed", ex);
        }

        if (token.IsCancellationRequested || _isShuttingDown)
        {
            completion.TrySetResult(null);
            ReconcileSelection();
            return;
        }

        if (CurrentProfileId == profile.Id)
        {
            if (!ReferenceEquals(_selectedProfile, profile))
            {
                SetProperty(ref _selectedProfile, profile, nameof(SelectedProfile));
            }

            completion.TrySetResult(_workspace);
            return;
        }

        IsProfileSwitching = true;
        IsBusy = true;
        BusyText = "正在打开笔记空间…";
        ProfileSwitchError = string.Empty;
        RaiseCommands();
        try
        {
            ProfileWorkspace? opened = await OpenProfileCoreAsync(profile, token).ConfigureAwait(true);
            completion.TrySetResult(opened);
        }
        catch (Exception ex)
        {
            completion.TrySetResult(null);
            FailOpen(null, ex);
        }
        finally
        {
            IsProfileSwitching = false;
            IsBusy = false;
            BusyText = string.Empty;
            ReconcileSelection();
            RaiseCommands();
        }
    }

    private async Task<ProfileWorkspace?> OpenProfileCoreAsync(ProfileDescriptor profile, CancellationToken token)
    {
        string? previousId = _workspace?.ProfileId;
        ProfileWorkspace? opened = null;
        try
        {
            // 1. Stop editors and flush the DPAPI draft while the store is still open.
            Current.PrepareForProfileSwitch();
            Records.PrepareForProfileSwitch();

            // 2. Close the outgoing database. Profile changes are not applied while
            //    a switch is running, so a queued store operation cannot land here.
            if (_workspace is not null)
            {
                WorkspaceClosing?.Invoke(this, EventArgs.Empty);
                ProfileWorkspace outgoing = _workspace;
                _workspace = null;
                _sessionStore = null;
                _autosave = null;
                OnPropertyChanged(nameof(IsProfileReady));
                await outgoing.DisposeAsync().ConfigureAwait(true);
            }

            token.ThrowIfCancellationRequested();

            // 3. Open the new database. If any later step fails, it is disposed.
            opened = await ProfileWorkspace
                .OpenAsync(_paths.DataRoot, profile.Id, _protector, CancellationToken.None, _paths.GetDatabasePath(profile.Id))
                .ConfigureAwait(true);

            var sessionStore = new SessionStore(_paths.DataRoot, profile.Id, _log);
            var autosave = new AutosaveJournal(
                ProfileCatalog.GetProfileDirectory(_paths.DataRoot, profile.Id),
                _protector,
                _log);

            _workspace = opened;
            _sessionStore = sessionStore;
            _autosave = autosave;
            _commandGeneration++;
            _pinnedProfileId = profile.Id;

            _preferences.LastProfileId = profile.Id;
            SavePreferencesQuietly("profile");

            _log.Info("profile", $"Opened profile '{profile.Id}'.");
            OnPropertyChanged(nameof(IsProfileReady));
            OnPropertyChanged(nameof(CurrentProfileId));
            OnPropertyChanged(nameof(CurrentProfileTitle));
            OnPropertyChanged(nameof(IsDemoProfile));

            await Current.LoadAsync().ConfigureAwait(true);
            await Records.LoadAsync().ConfigureAwait(true);
            Settings.RefreshEnvironment();
            ProfileSwitched?.Invoke(this, opened);

            if (token.IsCancellationRequested)
            {
                // A newer switch is queued; it will close this workspace itself.
                return opened;
            }

            if (previousId is not null && previousId != profile.Id)
            {
                Status.Show($"已切换到「{profile.Title}」。", StatusSeverity.Info);
            }
            else
            {
                Status.Success($"已打开「{profile.Title}」。");
            }

            return opened;
        }
        catch (Exception ex)
        {
            // Either the new workspace opened and later initialization failed, or
            // opening itself failed: never leave a half-initialized workspace open.
            FailOpen(opened, ex);
            return null;
        }
    }

    private void FailOpen(ProfileWorkspace? opened, Exception ex)
    {
        if (opened is not null)
        {
            _workspace = null;
            _sessionStore = null;
            _autosave = null;
            _commandGeneration++;
            try
            {
                opened.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception disposeEx) when (disposeEx is InvalidOperationException or ObjectDisposedException)
            {
                _log.Warn("profile", "Dispose after failed open failed", disposeEx);
            }
        }

        _workspace = null;
        _sessionStore = null;
        _autosave = null;
        OnPropertyChanged(nameof(IsProfileReady));
        ProfileSwitchError = "打开笔记空间失败：" + StatusMessageViewModel.Describe(ex)
                             + "。请检查磁盘空间与文件权限；不会以明文方式继续保存。";
        _log.Error("profile", "Open failed", ex);
        Status.Error(ProfileSwitchError);
    }

    /// <summary>Make the selector reflect the database that is actually open.</summary>
    private void ReconcileSelection()
    {
        ProfileDescriptor? actual = Profiles.FirstOrDefault(p => p.Id == CurrentProfileId);
        if (actual is not null && !ReferenceEquals(_selectedProfile, actual))
        {
            SetProperty(ref _selectedProfile, actual, nameof(SelectedProfile));
        }
        else if (actual is null && _selectedProfile is not null && !IsProfileReady)
        {
            // Failed open: keep whatever the user picked visible, but the banner and
            // error text already state that no space is open.
        }

        OnPropertyChanged(nameof(CurrentProfileTitle));
        OnPropertyChanged(nameof(IsDemoProfile));
    }

    private void RevertSelection() => ReconcileSelection();

    public void RaiseCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        Current.RaiseSaveState();
        Records.RaiseRefresh();
    }

    /// <summary>
    /// Persists preferences. Returns false and reports exactly once on failure;
    /// callers must not announce success when this returns false.
    /// </summary>
    public bool TrySaveSettings()
    {
        try
        {
            _preferences.Save(_paths.SettingsFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Status.Warn("设置保存失败：" + StatusMessageViewModel.Describe(ex) + "。本次修改只在本次运行中有效。");
            _log.Warn("settings", "Save failed", ex);
            return false;
        }
    }

    /// <summary>Background preference writes (e.g. last profile) never steal the status strip.</summary>
    private void SavePreferencesQuietly(string area)
    {
        try
        {
            _preferences.Save(_paths.SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _log.Warn(area, "Preference save failed", ex);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (this) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        CancellationTokenSource? cts;
        Task switching;
        lock (this)
        {
            _isShuttingDown = true;
            cts = _switchCts;
            _switchCts = null;
            switching = _switchChain;
        }

        Performance.StopRiotLookup();
        cts?.Cancel();
        try { await switching.ConfigureAwait(true); }
        catch (OperationCanceledException) { }
        StopRealtime();
        Current.FlushDraftOnClose();
        bool settingsSaved = TrySaveSettings();
        Current.CancelPending();
        ProfileWorkspace? workspace = _workspace;
        _workspace = null;
        _sessionStore = null;
        _autosave = null;
        OnPropertyChanged(nameof(IsProfileReady));
        if (workspace is not null)
        {
            await workspace.DisposeAsync().ConfigureAwait(false);
        }
        if (Settings.HasPendingPathChanges && !settingsSaved)
            throw new IOException("设置尚未保存，位置修改未执行。");
        await Settings.ApplyPendingPathChangesAsync().ConfigureAwait(false);
    }

    /// <summary>Gate lease that pins the workspace it was acquired on.</summary>
    public sealed class WorkspaceLease : IDisposable
    {
        private readonly IDisposable _gate;
        private bool _disposed;

        internal WorkspaceLease(ProfileWorkspace workspace, IDisposable gate, int generation)
        {
            Workspace = workspace;
            Generation = generation;
            _gate = gate;
        }

        public ProfileWorkspace Workspace { get; }

        public NotebookService Notebook => Workspace.Notebook;

        public int Generation { get; }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _gate.Dispose();
            }
        }
    }
}
