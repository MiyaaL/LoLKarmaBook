using LoLKarmaBook.Client;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private ILcuClientService? _clientService;
    private ILcuTagSource? _tagSource;
    private bool _ownsTagSource;
    private readonly SemaphoreSlim _clientPollGate = new(1, 1);
    private readonly CancellationTokenSource _clientLifetime = new();
    private bool _enableRealtime;
    private Task? _clientLoop;
    private Task? _activeClientPoll;
    private ClientAccount? _latestAccount;
    private string _clientStatusText = "等待检测 LOL 客户端";
    private string _clientAccountText = "账号：等待客户端登录";
    private string _clientPhaseText = "阶段：等待检测";
    private bool _isClientPolling;

    public string ClientStatusText { get => _clientStatusText; private set => SetProperty(ref _clientStatusText, value); }
    public string ClientAccountText { get => _clientAccountText; private set => SetProperty(ref _clientAccountText, value); }
    public string ClientPhaseText { get => _clientPhaseText; private set => SetProperty(ref _clientPhaseText, value); }
    public string? LatestAccountLabel => _latestAccount?.DisplayLabel;

    public bool IsClientPolling
    {
        get => _isClientPolling;
        private set
        {
            if (SetProperty(ref _isClientPolling, value)) ReconnectClientCommand.RaiseCanExecuteChanged();
        }
    }

    public IAsyncCommand ReconnectClientCommand { get; private set; } = null!;
    public IAsyncCommand ChooseClientDirectoryCommand { get; private set; } = null!;
    public IAsyncCommand RealtimeModeCommand { get; private set; } = null!;
    public Func<string?, string?>? PickClientDirectory { get; set; }

    private void InitializeRealtime(bool enabled, ILcuClientService? clientService)
    {
        _enableRealtime = enabled;
        _clientService = clientService;
        ReconnectClientCommand = new AsyncRelayCommand(RefreshClientAsync,
            () => !_isShuttingDown && !IsClientPolling, ex => ShowError(ex, "重新检测客户端失败"));
        ChooseClientDirectoryCommand = new AsyncRelayCommand(ChooseClientDirectoryAsync,
            () => !_isShuttingDown, ex => ShowError(ex, "设置客户端目录失败"));
        RealtimeModeCommand = new AsyncRelayCommand(ReturnToRealtimeAsync,
            () => !_isShuttingDown, ex => ShowError(ex, "切换实时模式失败"));
    }

    private async Task StartAndConnectAsync(ProfileDescriptor initial)
    {
        await SwitchProfileAsync(initial).ConfigureAwait(true);
        StartTagCatalogRefresh();
        if (_enableRealtime && !_isShuttingDown)
        {
            _clientLoop = PollClientLoopAsync(_clientLifetime.Token);
        }
    }

    /// <summary>
    /// Best-effort read of the live champion/item catalog. It shares the realtime
    /// client when one exists and only creates its own read-only client when
    /// realtime is actually enabled, so tests and smoke runs never touch the
    /// network; the cached/bundled catalog stays in use until (or unless) it works.
    /// </summary>
    private void StartTagCatalogRefresh()
    {
        if (_isShuttingDown)
        {
            return;
        }

        if (_tagSource is null)
        {
            if (_clientService is ILcuTagSource existing)
            {
                _tagSource = existing;
            }
            else if (_clientService is null && _enableRealtime)
            {
                var source = new LcuClientService();
                _tagSource = source;
                _ownsTagSource = true;
            }
        }

        if (_tagSource is not null)
        {
            _ = RefreshTagCatalogAsync(_tagSource);
        }
    }

    private async Task RefreshTagCatalogAsync(ILcuTagSource source)
    {
        try
        {
            await Tags.RefreshAsync(source, _clientLifetime.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_clientLifetime.IsCancellationRequested)
        {
            // Shutting down: the cached/bundled catalog stays in place.
        }
    }

    private async Task PollClientLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await RefreshClientAsync().ConfigureAwait(true);
                await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _log.Warn("client", "Realtime loop stopped", ex);
            if (!_isShuttingDown) ClientStatusText = "实时检测已停止，请点击重新检测。";
        }
    }

    /// <summary>One non-overlapping observation. The loop and user refresh share this gate.</summary>
    public async Task RefreshClientAsync()
    {
        if (_isShuttingDown || _clientLifetime.IsCancellationRequested
            || !await _clientPollGate.WaitAsync(0).ConfigureAwait(true)) return;
        try
        {
            IsClientPolling = true;
            _clientService ??= new LcuClientService();
            Task<ClientSnapshot> poll = _clientService.PollAsync(_preferences.ClientDirectory, _clientLifetime.Token);
            _activeClientPoll = poll;
            ClientSnapshot snapshot = await poll.ConfigureAwait(true);
            if (_isShuttingDown || _clientLifetime.IsCancellationRequested) return;

            ClientStatusText = snapshot.ConnectionStatus == ClientConnectionStatus.CredentialsUnavailable
                ? "已发现 LOL 客户端，但无法读取连接参数。请让 LoLKarmaBook与客户端以相同权限运行，或选择客户端目录。"
                : snapshot.Message;
            ClientPhaseText = "阶段：" + NotePresenter.GamePhaseLabel(snapshot.Phase);
            _latestAccount = snapshot.IsConnected ? snapshot.Account : null;
            ClientAccountText = _latestAccount is null
                ? "账号：尚未连接已登录账号"
                : "账号：" + _latestAccount.DisplayLabel + " · " + _latestAccount.PlatformId;

            // A deliberately selected demonstration space never receives live rosters or writes.
            if (IsDemoProfile || SelectedProfile?.IsDemo == true) return;
            if (!snapshot.IsConnected || _latestAccount is null)
            {
                Current.MarkLiveUnavailable(ClientStatusText);
                await ObserveMatchLifecycleAsync(null).ConfigureAwait(true);
                return;
            }
            if (IsMaintenanceBusy || IsProfileSwitching || Current.IsSaving) return;

            ProfileDescriptor accountProfile = EnsureAccountProfile(_latestAccount);
            if (CurrentProfileId != accountProfile.Id)
            {
                if (await SwitchProfileAsync(accountProfile).ConfigureAwait(true) is null) return;
            }
            if (_isShuttingDown || IsDemoProfile || CurrentProfileId != accountProfile.Id) return;
            await Current.ApplyLiveSnapshotAsync(snapshot.Session).ConfigureAwait(true);
            await ObserveMatchLifecycleAsync(snapshot.Session).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_clientLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_isShuttingDown) return;
            _log.Warn("client", "Client observation failed", ex);
            ClientStatusText = "连接暂时失败，将自动重试：" + StatusMessageViewModel.Describe(ex);
            ClientPhaseText = "阶段：连接中断";
            Current.MarkLiveUnavailable(ClientStatusText);
            await ObserveMatchLifecycleAsync(null).ConfigureAwait(true);
        }
        finally
        {
            _activeClientPoll = null;
            IsClientPolling = false;
            _clientPollGate.Release();
        }
    }

    private ProfileDescriptor EnsureAccountProfile(ClientAccount account)
    {
        ProfileDescriptor? existing = Profiles.FirstOrDefault(p => p.Id == account.ProfileKey);
        string title = "账号 " + account.DisplayLabel;
        if (existing?.Title == title) return existing;
        var profile = new ProfileDescriptor(account.ProfileKey, title, "该 LOL 账号的对局记录，与其他账号分别保存。", false);
        if (existing is null) Profiles.Add(profile);
        else Profiles[Profiles.IndexOf(existing)] = profile;
        if (CurrentProfileId == profile.Id) ReconcileSelection();
        return profile;
    }

    private async Task ChooseClientDirectoryAsync()
    {
        string? chosen = PickClientDirectory?.Invoke(_preferences.ClientDirectory);
        if (string.IsNullOrWhiteSpace(chosen)) return;
        _preferences.ClientDirectory = chosen;
        SavePreferencesQuietly("client-directory");
        await RefreshClientAsync().ConfigureAwait(true);
    }

    private async Task ReturnToRealtimeAsync()
    {
        ProfileDescriptor target = _latestAccount is null ? Profiles[0] : EnsureAccountProfile(_latestAccount);
        if (CurrentProfileId != target.Id) await SwitchProfileAsync(target).ConfigureAwait(true);
        SelectTab("current");
        await RefreshClientAsync().ConfigureAwait(true);
    }

    /// <summary>Cancellation does not wait for a UI continuation during synchronous application exit.</summary>
    public void StopRealtime()
    {
        if (!_clientLifetime.IsCancellationRequested) _clientLifetime.Cancel();
        if (_ownsTagSource && _tagSource is IDisposable owned)
        {
            owned.Dispose();
        }

        _tagSource = null;
        _ownsTagSource = false;
        ILcuClientService? client = _clientService;
        _clientService = null;
        if (client is null) return;
        Task? pending = _activeClientPoll;
        if (pending is null || pending.IsCompleted) client.Dispose();
        else _ = pending.ContinueWith(_ => client.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
