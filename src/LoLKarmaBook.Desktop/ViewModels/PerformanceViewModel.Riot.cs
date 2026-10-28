using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using LoLKarmaBook.Client;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Riot;
using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// Riot API half of the estimate page: the user types a key, a platform and a
/// Riot ID; the client pulls recent matches and the same pipeline as a local
/// import computes the estimate. The key lives in memory only for the duration of
/// a request (and as DPAPI ciphertext on disk) and is never written to a log,
/// an error message or the status strip.
/// </summary>
public sealed partial class PerformanceViewModel
{
    private string _apiKey = string.Empty;
    private bool _rememberApiKey;
    private string _riotIdText = string.Empty;
    private bool _isFetching;
    private string _fetchProgressText = string.Empty;
    private string _keyNoticeText = string.Empty;
    private RiotPlatformOption? _selectedPlatform;
    private RiotQueueOption? _selectedQueue;
    private RiotCountOption? _selectedCount;
    private int _requestedMatches;
    private int _receivedMatches;
    private int _lookupGeneration;

    /// <summary>
    /// Supplies the client. The window installs one wrapping the real network;
    /// tests substitute a stubbed handler so no request leaves the machine.
    /// </summary>
    public Func<RiotApiOptions, RiotApiClient>? CreateRiotClient { get; set; }

    public Func<ILcuHistoryService>? CreateLocalHistoryService { get; set; } = () => new LcuHistoryService();

    public ObservableCollection<RiotPlatformOption> Platforms { get; } = new(
        new[] { new RiotPlatformOption("CN", "中国大陆") }
            .Concat(RiotPlatforms.Catalog.Select(p => new RiotPlatformOption(p.Platform, p.Label))));

    public static IReadOnlyList<RiotQueueOption> Queues { get; } =
    [
        new(420, "单双排"),
        new(440, "灵活组排"),
        new(450, "极地大乱斗"),
        new(2400, "海克斯大乱斗"),
        new(null, "全部队列")
    ];

    public static IReadOnlyList<RiotCountOption> Counts { get; } =
    [
        new(10, "最近 10 局"),
        new(20, "最近 20 局"),
        new(30, "最近 30 局"),
        new(50, "最近 50 局")
    ];

    /// <summary>The key as typed. Bound one-way to a PasswordBox; never read back into the UI.</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            // Only the length is ever compared, so a leak of diagnostics cannot
            // reveal key material.
            if (SetProperty(ref _apiKey, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasApiKey));
                OnPropertyChanged(nameof(CanFetchRiot));
                FetchRiotCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasApiKey => _apiKey.Trim().Length > 0;

    /// <summary>
    /// True when the key should be saved (DPAPI-protected). Off by default: a
    /// shared machine keeps the key only until the app closes.
    /// </summary>
    public bool RememberApiKey
    {
        get => _rememberApiKey;
        set
        {
            if (SetProperty(ref _rememberApiKey, value)) UpdatePlatformNotice();
        }
    }

    public string RiotIdText
    {
        get => _riotIdText;
        set
        {
            if (SetProperty(ref _riotIdText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanFetchRiot));
                FetchRiotCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RiotPlatformOption? SelectedPlatform
    {
        get => _selectedPlatform;
        set
        {
            if (SetProperty(ref _selectedPlatform, value))
            {
                OnPropertyChanged(nameof(CanFetchRiot));
                OnPropertyChanged(nameof(IsMainlandPlatform));
                OnPropertyChanged(nameof(IsRiotPlatform));
                OnPropertyChanged(nameof(LookupDescription));
                UpdatePlatformNotice();
                FetchRiotCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public RiotQueueOption? SelectedQueue
    {
        get => _selectedQueue;
        set => SetProperty(ref _selectedQueue, value);
    }

    public RiotCountOption? SelectedCount
    {
        get => _selectedCount;
        set => SetProperty(ref _selectedCount, value);
    }

    public bool IsFetching
    {
        get => _isFetching;
        private set
        {
            if (SetProperty(ref _isFetching, value))
            {
                OnPropertyChanged(nameof(CanFetchRiot));
                OnPropertyChanged(nameof(CanEditLookup));
                FetchRiotCommand.RaiseCanExecuteChanged();
                CancelRiotCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>"正在拉取第 7 / 20 局…" while a fetch runs; empty otherwise.</summary>
    public string FetchProgressText
    {
        get => _fetchProgressText;
        private set
        {
            if (SetProperty(ref _fetchProgressText, value))
            {
                OnPropertyChanged(nameof(HasFetchProgress));
            }
        }
    }

    public bool HasFetchProgress => FetchProgressText.Length > 0;

    public bool IsMainlandPlatform => SelectedPlatform?.IsMainland == true;
    public bool IsRiotPlatform => !IsMainlandPlatform;
    public bool CanEditLookup => !IsFetching;
    public string LookupDescription => IsMainlandPlatform
        ? "读取本机已登录账号的近期对局。" : "填写 Riot ID 和 Key 获取近期对局。";
    public string LocalAccountText => _main.ClientAccountText;

    /// <summary>
    /// Shown under the key field: platform unsupported, key not persisted,
    /// previously saved key unreadable. Empty hides the line.
    /// </summary>
    public string KeyNoticeText
    {
        get => _keyNoticeText;
        private set
        {
            if (SetProperty(ref _keyNoticeText, value))
            {
                OnPropertyChanged(nameof(HasKeyNotice));
            }
        }
    }

    public bool HasKeyNotice => KeyNoticeText.Length > 0;

    private string _riotSourceText = string.Empty;

    /// <summary>Provenance line for the current result when it came from Riot; empty otherwise.</summary>
    public string RiotSourceText
    {
        get => _riotSourceText;
        private set
        {
            if (SetProperty(ref _riotSourceText, value))
            {
                OnPropertyChanged(nameof(HasRiotSource));
            }
        }
    }

    public bool HasRiotSource => RiotSourceText.Length > 0;

    /// <summary>True once a Riot lookup produced the current result.</summary>
    public bool IsRiotResult => LastImportPath.StartsWith("riot:", StringComparison.Ordinal);

    public IAsyncCommand FetchRiotCommand { get; private set; } = null!;

    public RelayCommand CancelRiotCommand { get; private set; } = null!;

    private void InitializeRiot()
    {
        FetchRiotCommand = new AsyncRelayCommand(FetchRiotAsync, () => CanFetchRiot);
        CancelRiotCommand = new RelayCommand(CancelRiot, () => _isFetching);

        SelectedPlatform ??= Platforms.FirstOrDefault(p => p.IsMainland) ?? Platforms.FirstOrDefault();
        SelectedQueue = Queues[0];
        SelectedCount = Counts[1];
        ApplySavedPreferences();
        UpdatePlatformNotice();
        _main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ClientAccountText)) OnPropertyChanged(nameof(LocalAccountText));
        };
    }

    private void ApplySavedPreferences()
    {
        try
        {
            RiotLookupPreferences saved = _main.RiotKeys.Load();
            if (saved.ApiKey is { Length: > 0 } key)
            {
                ApiKey = key;
                RememberApiKey = true;
            }

            if (File.Exists(_main.RiotKeys.FilePath)
                && Platforms.FirstOrDefault(p => p.Platform == saved.PlatformId) is { } platform)
            {
                SelectedPlatform = platform;
            }

            if (saved.RiotId.Trim().Length > 0)
            {
                RiotIdText = saved.RiotId;
            }

            SelectedCount = Counts.FirstOrDefault(c => c.Count == saved.MatchCount) ?? SelectedCount;
            SelectedQueue = Queues.FirstOrDefault(q => q.QueueId == saved.QueueId) ?? SelectedQueue;

            if (_main.RiotKeys.LoadError is { Length: > 0 } error)
            {
                KeyNoticeText = "已保存的 Key 无法读取，请重新填写。";
            }
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            _main.Log.Warn("riot", "Riot 设置数据异常：" + ex.GetType().Name);
            KeyNoticeText = "查询设置无法读取，请重新选择。";
        }
    }

    /// <summary>
    /// The China client's platform IDs (HN1, TJ1, …) are not served by Riot's
    /// public API. Saying so before the user presses the button is kinder than
    /// a 404 after it.
    /// </summary>
    /// <summary>
    /// True when a lookup has everything it needs. The Riot ID must already parse
    /// so a typo disables the button instead of failing after a click.
    /// </summary>
    public bool CanFetchRiot => !_isBusy && !_isFetching
        && (IsMainlandPlatform ? CreateLocalHistoryService is not null
            : HasApiKey && SelectedPlatform?.Region is not null
                && RiotId.TryParse(RiotIdText, out RiotId? parsed) && parsed is not null);

    private void UpdatePlatformNotice()
    {
        if (IsMainlandPlatform)
        {
            KeyNoticeText = "使用已登录的本机账号，无需填写 Key。";
            return;
        }
        if (_main.RiotKeys.LoadError is { Length: > 0 })
        {
            KeyNoticeText = "已保存的 Key 无法读取，请重新填写。";
            return;
        }

        KeyNoticeText = SelectedPlatform?.Region is null
            ? "请选择可用的区服。"
            : RememberApiKey
                ? "下次可继续使用已保存的 Key。"
                : "Key 仅在本次运行期间使用。";
    }

    private void CancelRiot()
    {
        _riotCts?.Cancel();
    }

    /// <summary>Aborts a running lookup; called when the window is closing.</summary>
    public void StopRiotLookup() => CancelRiot();

    private async Task FetchRiotAsync()
    {
        // Capture every request input before the first await; later UI changes are for the next lookup.
        RiotPlatformOption? platform = SelectedPlatform;
        bool local = platform?.IsMainland == true;
        var createLocal = CreateLocalHistoryService;
        var createRiot = CreateRiotClient;
        string apiKey = _apiKey.Trim();
        bool rememberKey = RememberApiKey;
        string? clientDirectory = _main.PersistedSettings.ClientDirectory;
        RiotId.TryParse(RiotIdText, out RiotId? riotId);
        if (platform is null || !platform.IsSupported
            || (local ? createLocal is null : createRiot is null || apiKey.Length == 0 || riotId is null))
        {
            ErrorText = local ? "无法连接本机客户端，请重新打开程序。" : "请填写有效的 Riot ID 和 Key。";
            _main.Status.Error(ErrorText);
            return;
        }

        var options = new RiotApiOptions
        {
            PlatformId = platform.Platform,
            MatchCount = SelectedCount?.Count ?? 20,
            QueueId = SelectedQueue?.QueueId
        };

        _riotCts?.Dispose();
        _riotCts = new CancellationTokenSource();
        CancellationToken token = _riotCts.Token;

        IsFetching = true;
        IsBusy = true;
        int generation = ++_lookupGeneration;
        ErrorText = string.Empty;
        _requestedMatches = options.MatchCount;
        _receivedMatches = 0;
        FetchProgressText = local ? "正在读取本机账号…" : "正在查找账号…";

        try
        {
            var progress = new Progress<int>(count =>
            {
                if (token.IsCancellationRequested || !IsFetching || generation != _lookupGeneration) return;
                _receivedMatches = count;
                FetchProgressText = $"已读取 {count} 局。";
            });
            if (local)
            {
                using ILcuHistoryService client = createLocal!();
                LcuHistoryResult result = await client.GetHistoryAsync(clientDirectory, options.MatchCount,
                    options.QueueId, progress, token).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (result.Status != LcuHistoryStatus.Success || result.Dataset is null || result.Account is null)
                {
                    ErrorText = LocalHistoryError(result.Status);
                    _main.Status.Error(ErrorText);
                    return;
                }
                if (result.Dataset.Matches.Count == 0)
                {
                    ErrorText = "该队列没有可用对局，请更换筛选条件。";
                    _main.Status.Error(ErrorText);
                    return;
                }
                ApplyDataset(result.Dataset, "客户端查询 · " + result.Account.DisplayLabel,
                    "client:" + result.Account.PlatformId + "/" + result.Account.DisplayLabel);
                RiotSourceText = "中国大陆 · " + result.Account.PlatformId + " · " + result.Account.DisplayLabel + " · " + QueueLabel(options.QueueId);
                RememberLocalPreferences(options);
                _main.Status.Success($"已读取 {result.Dataset.Matches.Count} 局{QueueLabel(options.QueueId)}。");
                return;
            }

            RiotApiClient riotClient = createRiot!(options);
            RiotAccount account = await riotClient.GetAccountAsync(apiKey, riotId!, options, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            FetchProgressText = "正在读取近期对局…";
            IReadOnlyList<RiotCompletedMatch> matches = await riotClient
                .GetCompletedMatchesAsync(apiKey, account, options, progress, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (matches.Count == 0)
            {
                ErrorText = "该队列没有可用对局，请更换筛选条件。";
                _main.Status.Error(ErrorText);
                return;
            }
            PerformanceDataset dataset = BuildRiotDataset(account, matches, options);
            ApplyDataset(dataset, $"Riot API 查询 · {account.DisplayLabel} · {platform.Label}",
                "riot:" + platform.Platform + "/" + account.DisplayLabel);
            RiotSourceText = platform.Label + " · " + account.DisplayLabel + " · " + QueueLabel(options.QueueId);
            RememberPreferences(apiKey, riotId!, options, rememberKey);
            _main.Status.Success($"已读取 {matches.Count} 局{QueueLabel(options.QueueId)}。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            FetchProgressText = string.Empty;
            _main.Status.Show("已取消对局查询。");
        }
        catch (RiotApiException ex)
        {
            // The message and hint are written by the Riot module and never carry
            // the key; the log records only the failure kind and status.
            ErrorText = ex.Failure switch
            {
                RiotApiFailure.KeyRejected => "Key 已失效或被拒绝，请重新填写。",
                RiotApiFailure.PlayerNotFound => "没有找到该账号，请检查区服和 Riot ID。",
                RiotApiFailure.RateLimited => "查询过于频繁，请稍后重试。",
                RiotApiFailure.Network => "暂时无法连接，请检查网络后重试。",
                _ => "对局读取失败，请稍后重试。"
            };
            _main.Log.Warn("riot", $"Riot API 查询失败：{ex.Failure}（HTTP {ex.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "无"}）");
            _main.Status.Error(ErrorText);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException)
        {
            ErrorText = "暂时无法读取对局，请稍后重试。";
            _main.Log.Warn("riot", "History request failed", ex);
            _main.Status.Error(ErrorText);
        }
        catch (Exception ex)
        {
            // Deliberately no ex.ToString() into any user-visible field; the log
            // record omits request data for the same reason.
            ReportFailure(ex, "Riot 对局查询失败");
        }
        finally
        {
            FetchProgressText = string.Empty;
            IsFetching = false;
            IsBusy = false;
        }
    }

    private static string LocalHistoryError(LcuHistoryStatus status) => status switch
    {
        LcuHistoryStatus.NotRunning => "请先打开并登录 LOL 客户端。",
        LcuHistoryStatus.NotLoggedIn => "请先在 LOL 客户端登录账号。",
        LcuHistoryStatus.CredentialsUnavailable => "无法读取客户端，请使用相同权限运行。",
        LcuHistoryStatus.AccountChanged => "查询期间账号发生变化，请重新获取。",
        LcuHistoryStatus.Unavailable => "客户端暂未提供历史对局，请稍后重试。",
        _ => "读取客户端对局失败，请稍后重试。"
    };

    private static string QueueLabel(int? queueId) => Queues.FirstOrDefault(q => q.QueueId == queueId)?.Label ?? "全部队列";

    private void RememberLocalPreferences(RiotApiOptions options)
    {
        try
        {
            RiotLookupPreferences saved = _main.RiotKeys.Load();
            if (_main.RiotKeys.LoadError is not null) return;
            _main.RiotKeys.Save(saved.ApiKey, new RiotLookupPreferences(null, "CN", saved.RiotId, options.MatchCount, options.QueueId));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.Cryptography.CryptographicException or PlatformNotSupportedException or NotSupportedException)
        {
            _main.Log.Warn("riot", "Unable to save lookup preferences", ex);
        }
    }

    /// <summary>
    /// Turns parsed matches into the same dataset a local import would produce,
    /// so one scoring path serves both. Only the roster and the owner's stat row
    /// are kept; no other player's identity leaves memory.
    /// </summary>
    private static PerformanceDataset BuildRiotDataset(
        RiotAccount account, IReadOnlyList<RiotCompletedMatch> matches, RiotApiOptions options)
    {
        var rows = new List<MatchStatRow>(matches.Count);
        var rosterMatches = new List<PerformanceRosterMatch>(matches.Count);
        foreach (RiotCompletedMatch match in matches)
        {
            rows.Add(match.Stats);
            rosterMatches.Add(new PerformanceRosterMatch(
                match.Roster.MatchId,
                match.Roster.Timestamp,
                match.Roster.Teammates,
                match.Roster.Opponents));
        }

        // Platform and queue go in the source tag; the Riot ID is shown on the
        // page's provenance line separately so this string stays identifier-only.
        return new PerformanceDataset
        {
            Source = $"riot-api:{options.PlatformId}:{options.QueueId?.ToString(CultureInfo.InvariantCulture) ?? "all"}",
            Matches = rows,
            PlayerId = account.Puuid,
            Roster = new PerformanceRoster(account.Puuid, rosterMatches)
        };
    }

    private void RememberPreferences(string apiKey, RiotId riotId, RiotApiOptions options, bool rememberKey)
    {
        // Key material is passed separately so the record never holds plaintext.
        var preferences = new RiotLookupPreferences(
            null, options.PlatformId ?? string.Empty, riotId.ToString(), options.MatchCount, options.QueueId);
        try
        {
            if (!rememberKey)
            {
                _main.RiotKeys.Save(null, preferences);
                return;
            }

            _main.RiotKeys.Save(apiKey, preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.Cryptography.CryptographicException
                                       or PlatformNotSupportedException or NotSupportedException)
        {
            // The lookup already succeeded; failing to remember it is a warning,
            // not an error, and the key is never echoed.
            _main.Log.Warn("riot", "无法保存 Riot API 设置：" + ex.GetType().Name);
            KeyNoticeText = "设置未保存，下次需重新填写。";
        }
    }

    private CancellationTokenSource? _riotCts;
}

/// <summary>One entry of the platform picker; unsupported regions are still listed.</summary>
public sealed class RiotPlatformOption(string platform, string label)
{
    public string Platform { get; } = platform;

    public string Label { get; } = label;

    public string? Region { get; } = RiotPlatforms.RegionFor(platform);

    public bool IsMainland => Platform == "CN";

    public bool IsSupported => IsMainland || Region is not null;

    public string Display => Label;

    public override string ToString() => Display;
}

public sealed record RiotQueueOption(int? QueueId, string Label)
{
    public override string ToString() => Label;
}

public sealed record RiotCountOption(int Count, string Label)
{
    public override string ToString() => Label;
}
