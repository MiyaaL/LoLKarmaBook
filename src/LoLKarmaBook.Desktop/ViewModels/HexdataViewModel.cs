using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Hexdata;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// Hexdata 海斗分: sign in with an emailed code, then read the public match report
/// for the signed-in account. Every report is copied into the local backup folder
/// so past scores and both teams stay readable offline. The page shows those
/// backups as a collapsible history list plus a score trend chart.
/// </summary>
public sealed class HexdataViewModel : ObservableObject
{
    private readonly HexdataClient _client;
    private readonly HexdataSessionStore _session;
    private readonly HexdataReportCache _cache;
    private readonly Func<DateTimeOffset> _now;
    private string _token = string.Empty;
    private string _tokenInput = string.Empty;

    private string _email = string.Empty;
    private string _code = string.Empty;
    private string _gameIdInput = string.Empty;
    private string _statusText = string.Empty;
    private string _statusSeverity = "info";
    private bool _isBusy;
    private bool _isSignedIn;
    private bool _chartShowsAll;
    private readonly TagCatalogService? _tags;

    public HexdataViewModel(string dataRoot, ITextProtector protector, HexdataClient? client = null,
        Func<DateTimeOffset>? now = null, TagCatalogService? tags = null)
    {
        _client = client ?? new HexdataClient();
        _tags = tags;
        _session = new HexdataSessionStore(dataRoot, protector);
        _cache = new HexdataReportCache(dataRoot);
        _now = now ?? (() => DateTimeOffset.UtcNow);

        SendCodeCommand = new AsyncRelayCommand(() => Task.CompletedTask, () => false, Report);
        SignInCommand = new AsyncRelayCommand(SignInWithBrowserAsync, () => !IsBusy, Report);
        UseTokenCommand = new AsyncRelayCommand(UseTokenAsync, () => !IsBusy && TokenInput.Trim().Length > 8, Report);
        CompleteLoginCommand = new AsyncRelayCommand(CompleteLoginAsync, () => !IsBusy && IsAwaitingBrowserLogin, Report);
        SignOutCommand = new RelayCommand(SignOut, () => !IsBusy);
        QuerySelfCommand = new AsyncRelayCommand(() => QueryAsync(null, true), () => !IsBusy && IsSignedIn, Report);
        QueryGameCommand = new AsyncRelayCommand(
            () => QueryAsync(GameIdInput, false),
            () => !IsBusy && IsSignedIn && GameIdInput.Trim().Length > 1,
            Report);
        ToggleChartScopeCommand = new RelayCommand(ToggleChartScope);

        HexdataSession? saved = _session.Load();
        if (saved is not null)
        {
            _token = saved.Token;
            _isSignedIn = true;
            _email = saved.Email ?? string.Empty;
            _statusText = string.Empty;
        }

        ReloadSavedReports();
    }

    public IAsyncCommand SendCodeCommand { get; }
    public IAsyncCommand SignInCommand { get; }
    public RelayCommand SignOutCommand { get; }
    public IAsyncCommand QuerySelfCommand { get; }
    public IAsyncCommand QueryGameCommand { get; }

    public string Email
    {
        get => _email;
        set
        {
            if (SetProperty(ref _email, value ?? string.Empty))
            {
                SignInCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Code
    {
        get => _code;
        set => SetProperty(ref _code, value ?? string.Empty);
    }

    public string GameIdInput
    {
        get => _gameIdInput;
        set
        {
            if (SetProperty(ref _gameIdInput, value ?? string.Empty))
            {
                QueryGameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string StatusSeverity
    {
        get => _statusSeverity;
        private set
        {
            if (SetProperty(ref _statusSeverity, value))
            {
                OnPropertyChanged(nameof(IsStatusError));
            }
        }
    }

    public bool IsStatusError => StatusSeverity is "error" or "warning";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                SendCodeCommand.RaiseCanExecuteChanged();
                SignInCommand.RaiseCanExecuteChanged();
                QuerySelfCommand.RaiseCanExecuteChanged();
                QueryGameCommand.RaiseCanExecuteChanged();
                SignOutCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsSignedOut));
            }
        }
    }

    public bool IsSignedIn
    {
        get => _isSignedIn;
        private set
        {
            if (SetProperty(ref _isSignedIn, value))
            {
                OnPropertyChanged(nameof(IsSignedOut));
                QuerySelfCommand.RaiseCanExecuteChanged();
                QueryGameCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSignedOut => !IsSignedIn;

    public ObservableCollection<HexdataHistoryRowViewModel> History { get; } = new();

    public HexdataChartViewModel Chart { get; } = new();

    public bool HasHistory => History.Count > 0;

    public string ChartScopeText => _chartShowsAll ? "范围：全部记录" : "范围：当前账号";

    public RelayCommand ToggleChartScopeCommand { get; }

    public string BackupDirectory => _cache.Root;

    /// <summary>Host-provided: reads session values from the browser window we opened.</summary>
    public Func<Task<IReadOnlyList<string>?>>? ReadSessionAsync { get; set; }

    public AsyncRelayCommand CompleteLoginCommand { get; private set; } = null!;

    private bool _awaitingBrowserLogin;

    public bool IsAwaitingBrowserLogin
    {
        get => _awaitingBrowserLogin;
        private set
        {
            if (SetProperty(ref _awaitingBrowserLogin, value))
            {
                CompleteLoginCommand.RaiseCanExecuteChanged();
            }
        }
    }

    /// <summary>Host-provided: opens the Hexdata page in a dedicated browser window.</summary>
    public Action? OpenLoginPage { get; set; }

    private async Task SignInWithBrowserAsync()
    {
        if (OpenLoginPage is null)
        {
            ShowStatus("当前环境无法打开浏览器。", "error");
            return;
        }

        OpenLoginPage();
        IsAwaitingBrowserLogin = true;
        ShowStatus("已在浏览器窗口打开 Hexdata：完成登录后回来点「我已完成登录」。", "info");
        await Task.CompletedTask;
    }

    /// <summary>Pasted session token from the browser.</summary>
    public string TokenInput
    {
        get => _tokenInput;
        set
        {
            if (SetProperty(ref _tokenInput, value ?? string.Empty))
            {
                UseTokenCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand UseTokenCommand { get; private set; } = null!;

    private async Task UseTokenAsync()
    {
        IsBusy = true;
        bool signedIn = false;
        try
        {
            ShowStatus("正在验证令牌…", "info");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            HexdataAccount? account = await _client.GetAccountAsync(TokenInput.Trim(), timeout.Token);
            if (account is null)
            {
                ShowStatus("令牌无效或已过期，请在浏览器里重新登录后再复制。", "error");
                return;
            }

            _token = TokenInput.Trim();
            _session.Save(_token, account.Email);
            _email = account.Email ?? string.Empty;
            OnPropertyChanged(nameof(Email));
            IsSignedIn = true;
            signedIn = true;
            TokenInput = string.Empty;
            ShowStatus("登录成功，正在查询你的海斗分…", "success");
        }
        catch (HexdataException ex)
        {
            ShowStatus(ex.Message, "error");
        }
        catch (OperationCanceledException)
        {
            ShowStatus("验证令牌超时，请检查网络后重试。", "error");
        }
        finally
        {
            IsBusy = false;
        }

        if (signedIn)
        {
            await QueryAsync(null, true);
        }
    }

    private async Task CompleteLoginAsync()
    {
        if (ReadSessionAsync is null)
        {
            ShowStatus("当前环境无法读取登录状态。", "error");
            return;
        }

        IsBusy = true;
        try
        {
            IReadOnlyList<string>? candidates = await ReadSessionAsync();
            if (candidates is not { Count: > 0 })
            {
                ShowStatus("还没有读到登录状态，请先在浏览器窗口里完成登录。", "warning");
                return;
            }

            foreach (string candidate in candidates.Take(20))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                HexdataAccount? account;
                try { account = await _client.GetAccountAsync(candidate, timeout.Token); }
                catch (Exception ex) when (ex is HexdataException or OperationCanceledException) { continue; }
                if (account is null) continue;

                _token = candidate;
                _session.Save(candidate, account.Email);
                _email = account.Email ?? string.Empty;
                OnPropertyChanged(nameof(Email));
                IsSignedIn = true;
                IsAwaitingBrowserLogin = false;
                ShowStatus("登录成功，正在查询你的海斗分…", "success");
                IsBusy = false;
                await QueryAsync(null, true);
                return;
            }

            ShowStatus("没有读到有效的登录状态，请确认浏览器里已经登录。", "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SignOut()
    {
        _token = string.Empty;
        _session.Clear();
        IsSignedIn = false;
        ShowStatus("已退出 Hexdata，本地查询记录仍然保留。", "info");
    }

    private async Task QueryAsync(string? gameId, bool useBoundIdentity)
    {
        if (!IsSignedIn)
        {
            ShowStatus("请先登录 Hexdata。", "warning");
            return;
        }

        await RunAsync(async () =>
        {
            HexdataReport report = await QueryWithFallbackAsync(gameId, useBoundIdentity);
            HexdataSavedReport saved = _cache.Save(report, useBoundIdentity ? "我的账号" : gameId ?? string.Empty, _now());
            ReloadSavedReports();
            HexdataHistoryRowViewModel? row = History.FirstOrDefault(
                candidate => string.Equals(candidate.FileName, saved.FileName, StringComparison.Ordinal));
            if (row is not null)
            {
                await ExpandAsync(row);
            }

            ShowStatus(report.IsCollecting
                ? "Hexdata 还在收集这名玩家近期的有效对局，稍后再查会更新。"
                : "已更新海斗分，并备份到本机：" + Path.GetFileName(saved.FileName), "success");
        });
    }

    /// <summary>Host-provided: the signed-in LOL account's game id, used when Hexdata has no bound identity.</summary>
    public Func<string?>? CurrentGameId { get; set; }

    private async Task<HexdataReport> QueryWithFallbackAsync(string? gameId, bool useBoundIdentity)
    {
        string? fallback = CurrentGameId?.Invoke();
        try
        {
            return await _client.QueryReportAsync(_token, gameId, useBoundIdentity);
        }
        catch (HexdataException) when (useBoundIdentity && !string.IsNullOrWhiteSpace(fallback))
        {
            ShowStatus("Hexdata 还没绑定游戏账号，改用本机账号 " + fallback + " 查询。", "info");
            return await _client.QueryReportAsync(_token, fallback, false);
        }
    }

    /// <summary>
    /// Hexdata returns champion ids/names from Riot's data; the local client
    /// catalog knows the official Chinese name, so prefer it and fall back to the
    /// report text when the id/name is unknown.
    /// </summary>
    public string ResolveChampionName(int? championId, string? fallback)
    {
        if (_tags is { Entries.Count: > 0 } tags)
        {
            if (championId is > 0)
            {
                TagEntry? byId = tags.Entries.FirstOrDefault(
                    entry => entry.Kind == TagKind.Champion && entry.Id == championId);
                if (byId is not null)
                {
                    return byId.OfficialName;
                }
            }

            if (!string.IsNullOrWhiteSpace(fallback))
            {
                string needle = TagSearch.Normalize(fallback);
                TagEntry? byName = tags.Entries.FirstOrDefault(entry =>
                    entry.Kind == TagKind.Champion
                    && (TagSearch.Normalize(entry.OfficialName) == needle
                        || entry.Aliases.Any(alias => TagSearch.Normalize(alias) == needle)));
                if (byName is not null)
                {
                    return byName.OfficialName;
                }
            }
        }

        return string.IsNullOrWhiteSpace(fallback) ? "—" : fallback;
    }

    private void ReloadSavedReports()
    {
        History.Clear();
        foreach (HexdataSavedReport saved in _cache.List().Take(20))
        {
            History.Add(new HexdataHistoryRowViewModel(
                saved,
                _cache.Load(saved.FileName),
                ToggleRowAsync,
                ResolveChampionName));
        }

        OnPropertyChanged(nameof(HasHistory));
        RebuildChart();
    }

    private async Task ToggleRowAsync(HexdataHistoryRowViewModel row)
    {
        if (row.IsExpanded)
        {
            row.SetExpanded(false);
            return;
        }

        await ExpandAsync(row);
    }

    private Task ExpandAsync(HexdataHistoryRowViewModel row)
    {
        foreach (HexdataHistoryRowViewModel other in History)
        {
            if (!ReferenceEquals(other, row))
            {
                other.SetExpanded(false);
            }
        }

        row.SetExpanded(true);
        return Task.CompletedTask;
    }

    private void ToggleChartScope()
    {
        _chartShowsAll = !_chartShowsAll;
        OnPropertyChanged(nameof(ChartScopeText));
        RebuildChart();
    }

    /// <summary>
    /// By default the chart follows the newest scored account, so scores fetched
    /// for other players do not distort your own trend. The toggle shows every
    /// saved score when the user explicitly asks for it.
    /// </summary>
    private void RebuildChart()
    {
        HexdataHistoryRowViewModel[] scored = History
            .Where(row => row.Score is not null)
            .ToArray();
        if (scored.Length == 0)
        {
            Chart.Update(Array.Empty<ScoreSample>(), "还没有可绘制的分数。");
            return;
        }

        string account = scored[0].ResolvedGameId;
        HexdataHistoryRowViewModel[] selected = _chartShowsAll
            ? scored
            : scored
                .Where(row => string.Equals(
                    row.ResolvedGameId,
                    account,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (selected.Length == 0)
        {
            selected = scored;
        }

        var samples = selected
            .Select(row => new ScoreSample(row.SavedAt, row.Score!.Value))
            .ToArray();
        // The signed-in account is already shown at the top of the page, so the
        // chart only says how many points it covers (the scope button says which).
        string label = _chartShowsAll ? "全部记录" : string.Empty;
        Chart.Update(samples, label);
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (HexdataException ex)
        {
            if (ex.IsUnauthorized)
            {
                _token = string.Empty;
                _session.Clear();
                IsSignedIn = false;
            }

            ShowStatus(ex.Message, "error");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowStatus(StatusMessageViewModel.Describe(ex), "error");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Report(Exception exception)
    {
        ShowStatus(exception is HexdataException hex ? hex.Message : StatusMessageViewModel.Describe(exception), "error");
    }

    private void ShowStatus(string text, string severity)
    {
        StatusText = text;
        StatusSeverity = severity;
    }
}

/// <summary>
/// One saved query in the history list. Expanding it reveals the full report —
/// score, metadata, recent matches and both teams — loaded from the local backup.
/// </summary>
public sealed class HexdataHistoryRowViewModel : ObservableObject
{
    private bool _isExpanded;

    public HexdataHistoryRowViewModel(
        HexdataSavedReport saved,
        HexdataReport? report,
        Func<HexdataHistoryRowViewModel, Task> toggle,
        Func<int?, string?, string>? championName = null)
    {
        Func<int?, string?, string> resolve = championName
            ?? ((_, name) => string.IsNullOrWhiteSpace(name) ? "—" : name);
        Saved = saved;
        Report = report;
        TimeText = saved.SavedAt.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        ResolvedGameId = string.IsNullOrWhiteSpace(saved.GameId) ? saved.Query : saved.GameId;
        GameText = string.IsNullOrWhiteSpace(ResolvedGameId) ? "（未返回游戏 ID）" : ResolvedGameId;
        Score = saved.Score;
        ScoreText = saved.Score is { } score
            ? score.ToString("0", CultureInfo.InvariantCulture)
            : (saved.IsCollecting ? "收集中" : "—");
        RegionText = string.IsNullOrWhiteSpace(saved.RegionKey)
            ? string.Empty
            : saved.RegionKey.ToUpperInvariant();
        ToggleCommand = new AsyncRelayCommand(() => toggle(this));

        if (report is not null)
        {
            EstimateMetaText = BuildEstimateMeta(report);
            CollectingText = report.IsCollecting
                ? (string.IsNullOrWhiteSpace(report.CollectingReason)
                    ? "Hexdata 还在收集这名玩家的有效对局。"
                    : "Hexdata 提示：" + report.CollectingReason)
                : string.Empty;
            Matches = report.RecentMatches.Take(10).Select(match => new HexdataMatchRowViewModel(match, resolve)).ToArray();
        }
        else
        {
            EstimateMetaText = "这份备份暂时读不出来，文件可能已被移动。";
            Matches = Array.Empty<HexdataMatchRowViewModel>();
        }
    }

    public HexdataSavedReport Saved { get; }

    public HexdataReport? Report { get; }

    public string FileName => Saved.FileName;

    public DateTimeOffset SavedAt => Saved.SavedAt;

    public string ResolvedGameId { get; }

    public string TimeText { get; }

    public string GameText { get; }

    public double? Score { get; }

    public string ScoreText { get; }

    public string RegionText { get; }

    public string EstimateMetaText { get; }

    public string CollectingText { get; } = string.Empty;

    public bool HasCollectingText => CollectingText.Length > 0;

    public IReadOnlyList<HexdataMatchRowViewModel> Matches { get; }

    public bool IsExpanded => _isExpanded;

    public string ChevronText => _isExpanded ? "▲" : "▼";

    public IAsyncCommand ToggleCommand { get; }

    public void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded)
        {
            return;
        }

        _isExpanded = expanded;
        OnPropertyChanged(nameof(IsExpanded));
        OnPropertyChanged(nameof(ChevronText));
    }

    private static string BuildEstimateMeta(HexdataReport report)
    {
        var parts = new List<string>();
        HexdataEstimate? estimate = report.Estimate;
        if (estimate?.Percentile is { } percentile)
        {
            parts.Add($"百分位 {percentile.ToString("0.#", CultureInfo.InvariantCulture)}%");
        }

        if (estimate?.Confidence is { Length: > 0 } confidence)
        {
            parts.Add("置信度 " + Describe(confidence));
        }

        if (estimate?.IntervalHalfWidth is { } halfWidth)
        {
            parts.Add($"±{halfWidth.ToString("0.#", CultureInfo.InvariantCulture)}");
        }

        if (report.GeneratedAt is { } generated)
        {
            parts.Add("更新于 " + generated.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        if (!string.IsNullOrWhiteSpace(report.RegionKey))
        {
            parts.Add(report.RegionKey.ToUpperInvariant());
        }

        return parts.Count == 0 ? "Hexdata 未返回更多说明。" : string.Join(" · ", parts);
    }

    private static string Describe(string confidence) => confidence switch
    {
        "high" => "高",
        "medium" => "中",
        "low" => "低",
        _ => confidence
    };
}

/// <summary>
/// Lays a <see cref="ScoreTrend"/> out on a fixed canvas: record time on the X
/// axis, Hexdata score on the Y axis, one circle per saved query, and a line when
/// at least two points exist. Hovering a circle shows its exact time and score.
/// </summary>
public sealed class HexdataChartViewModel : ObservableObject
{
    public const double CanvasWidth = 760;
    public const double CanvasHeight = 210;

    private const double PlotLeft = 64;
    private const double PlotRight = 16;
    private const double PlotTop = 14;
    private const double PlotBottom = 30;
    private const double MarkerSize = 20;

    private IReadOnlyList<ChartPlotPoint> _linePoints = Array.Empty<ChartPlotPoint>();
    private IReadOnlyList<HexdataChartDot> _dots = Array.Empty<HexdataChartDot>();
    private IReadOnlyList<HexdataChartLabel> _yLabels = Array.Empty<HexdataChartLabel>();
    private IReadOnlyList<HexdataChartLabel> _xLabels = Array.Empty<HexdataChartLabel>();
    private bool _hasPoints;
    private bool _hasLine;
    private string _minimumText = string.Empty;
    private string _maximumText = string.Empty;
    private string _latestText = string.Empty;
    private string _accountText = string.Empty;

    /// <summary>Polyline points in canvas coordinates (only drawn with 2+ points).</summary>
    public IReadOnlyList<ChartPlotPoint> LinePoints
    {
        get => _linePoints;
        private set => SetProperty(ref _linePoints, value);
    }

    /// <summary>One hoverable circle per saved score.</summary>
    public IReadOnlyList<HexdataChartDot> Dots
    {
        get => _dots;
        private set => SetProperty(ref _dots, value);
    }

    public IReadOnlyList<HexdataChartLabel> YLabels
    {
        get => _yLabels;
        private set => SetProperty(ref _yLabels, value);
    }

    public IReadOnlyList<HexdataChartLabel> XLabels
    {
        get => _xLabels;
        private set => SetProperty(ref _xLabels, value);
    }

    public bool HasPoints
    {
        get => _hasPoints;
        private set => SetProperty(ref _hasPoints, value);
    }

    public bool HasLine
    {
        get => _hasLine;
        private set => SetProperty(ref _hasLine, value);
    }

    public string MinimumText
    {
        get => _minimumText;
        private set => SetProperty(ref _minimumText, value);
    }

    public string MaximumText
    {
        get => _maximumText;
        private set => SetProperty(ref _maximumText, value);
    }

    public string LatestText
    {
        get => _latestText;
        private set => SetProperty(ref _latestText, value);
    }

    public string AccountText
    {
        get => _accountText;
        private set => SetProperty(ref _accountText, value);
    }

    public void Update(IEnumerable<ScoreSample> samples, string accountLabel)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ScoreTrend trend = ScoreTrend.Build(samples);
        if (trend.IsEmpty)
        {
            LinePoints = Array.Empty<ChartPlotPoint>();
            Dots = Array.Empty<HexdataChartDot>();
            YLabels = Array.Empty<HexdataChartLabel>();
            XLabels = Array.Empty<HexdataChartLabel>();
            HasPoints = false;
            HasLine = false;
            MinimumText = string.Empty;
            MaximumText = string.Empty;
            LatestText = string.Empty;
            AccountText = accountLabel;
            return;
        }

        double plotWidth = CanvasWidth - PlotLeft - PlotRight;
        double plotHeight = CanvasHeight - PlotTop - PlotBottom;
        var linePoints = new List<ChartPlotPoint>(trend.Points.Count);
        var dots = new List<HexdataChartDot>(trend.Points.Count);
        foreach (ScoreTrendPoint point in trend.Points)
        {
            double x = PlotLeft + point.X * plotWidth;
            double y = PlotTop + (1 - point.Y) * plotHeight;
            linePoints.Add(new ChartPlotPoint(x, y));
            dots.Add(new HexdataChartDot(
                x - MarkerSize / 2,
                y - MarkerSize / 2,
                TooltipTime(point.At) + Environment.NewLine + "海斗分 " + point.Score.ToString("0", CultureInfo.InvariantCulture)));
        }

        LinePoints = linePoints;
        Dots = dots;
        HasLine = trend.Points.Count >= 2;

        var yLabels = new List<HexdataChartLabel>(2);
        if (trend.Maximum - trend.Minimum < 0.5)
        {
            double middle = PlotTop + plotHeight / 2;
            yLabels.Add(new HexdataChartLabel(
                trend.Maximum.ToString("0", CultureInfo.InvariantCulture),
                0,
                middle - 9,
                PlotLeft - 8));
        }
        else
        {
            yLabels.Add(new HexdataChartLabel(
                trend.Maximum.ToString("0", CultureInfo.InvariantCulture),
                0,
                PlotTop - 9,
                PlotLeft - 8));
            yLabels.Add(new HexdataChartLabel(
                trend.Minimum.ToString("0", CultureInfo.InvariantCulture),
                0,
                PlotTop + plotHeight - 9,
                PlotLeft - 8));
        }

        YLabels = yLabels;

        var xLabels = new List<HexdataChartLabel>(3);
        int[] indices = trend.Points.Count switch
        {
            1 => new[] { 0 },
            2 => new[] { 0, 1 },
            _ => new[] { 0, trend.Points.Count / 2, trend.Points.Count - 1 }
        };
        foreach (int index in indices)
        {
            double x = linePoints[index].X;
            xLabels.Add(new HexdataChartLabel(
                AxisTime(trend.Points[index].At),
                Math.Clamp(x - 60, 0, CanvasWidth - 120),
                CanvasHeight - PlotBottom + 6,
                120));
        }

        XLabels = xLabels;
        HasPoints = true;
        MinimumText = "最低 " + trend.Minimum.ToString("0", CultureInfo.InvariantCulture);
        MaximumText = "最高 " + trend.Maximum.ToString("0", CultureInfo.InvariantCulture);
        LatestText = "最新 " + (trend.Latest ?? 0).ToString("0", CultureInfo.InvariantCulture);
        AccountText = string.IsNullOrWhiteSpace(accountLabel)
            ? $"{trend.Points.Count} 个分数点"
            : $"{accountLabel} · {trend.Points.Count} 个分数点";
    }

    private static string AxisTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    private static string TooltipTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>A hoverable score marker; Left/Top are canvas coordinates.</summary>
public sealed record HexdataChartDot(double Left, double Top, string Tooltip);

/// <summary>One axis label with its canvas position and width.</summary>
public sealed record HexdataChartLabel(string Text, double Left, double Top, double Width);

/// <summary>One recent match from the report.</summary>
public sealed class HexdataMatchRowViewModel : ObservableObject
{
    private bool _isExpanded;

    public HexdataMatchRowViewModel(HexdataMatch match, Func<int?, string?, string>? championName = null)
    {
        Func<int?, string?, string> resolve = championName
            ?? ((_, name) => string.IsNullOrWhiteSpace(name) ? "—" : name);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        WhenText = match.StartedAt is { } started
            ? started.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)
            : "—";
        ResultText = match.TargetWin switch
        {
            true => "胜",
            false => "负",
            null => "—"
        };
        IsWin = match.TargetWin == true;
        IsLoss = match.TargetWin == false;
        ChampionText = resolve(match.Summary.ChampionId, match.Summary.ChampionName);
        KdaText = match.Summary.Kills is { } k && match.Summary.Deaths is { } d && match.Summary.Assists is { } a
            ? $"{k}/{d}/{a}"
            : "—";
        DurationText = match.DurationSeconds is { } seconds ? $"{seconds / 60}:{seconds % 60:00}" : "—";
        ScoreText = match.Summary.MatchHexScore is { } score ? score.ToString("0", CultureInfo.InvariantCulture) : "—";
        HasTeams = match.Teams.Count > 0 && match.Teams.Any(team => team.Participants.Count > 0);
        Teams = match.Teams.Select(team => new HexdataTeamRowViewModel(team, resolve)).ToArray();
    }

    public string WhenText { get; }
    public string ResultText { get; }
    public bool IsWin { get; }
    public bool IsLoss { get; }
    public string ChampionText { get; }
    public string KdaText { get; }
    public string DurationText { get; }
    public string ScoreText { get; }
    public bool HasTeams { get; }
    public IReadOnlyList<HexdataTeamRowViewModel> Teams { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        private set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(ChevronText));
            }
        }
    }

    public string ChevronText => _isExpanded ? "▼" : "▶";

    public ICommand ToggleCommand { get; }
}

public sealed class HexdataTeamRowViewModel
{
    public HexdataTeamRowViewModel(HexdataTeam team, Func<int?, string?, string>? championName = null)
    {
        Func<int?, string?, string> resolve = championName
            ?? ((_, name) => string.IsNullOrWhiteSpace(name) ? "—" : name);
        Heading = (team.Side ?? string.Empty) switch
        {
            "blue" or "Blue" => $"蓝方 · {team.Scored}/{team.Total} 人有分",
            "red" or "Red" => $"红方 · {team.Scored}/{team.Total} 人有分",
            _ => $"{team.Side} · {team.Scored}/{team.Total} 人有分"
        };
        Participants = team.Participants.Select(p => new HexdataPlayerRowViewModel(p, resolve)).ToArray();
    }

    public string Heading { get; }

    public IReadOnlyList<HexdataPlayerRowViewModel> Participants { get; }
}

public sealed class HexdataPlayerRowViewModel
{
    public HexdataPlayerRowViewModel(HexdataParticipant participant, Func<int?, string?, string>? championName = null)
    {
        Func<int?, string?, string> resolve = championName
            ?? ((_, name) => string.IsNullOrWhiteSpace(name) ? "—" : name);
        Name = participant.Hidden ? "（隐藏）" : participant.PublicGameId ?? "—";
        Champion = resolve(participant.ChampionId, participant.ChampionName);
        Kda = participant.Kills is { } k && participant.Deaths is { } d && participant.Assists is { } a
            ? $"{k}/{d}/{a}"
            : "—";
        Score = participant.CurrentEstimate?.Score is { } score
            ? score.ToString("0", CultureInfo.InvariantCulture)
            : "—";
        IsTarget = participant.IsTarget;
    }

    public string Name { get; }
    public string Champion { get; }
    public string Kda { get; }
    public string Score { get; }
    public bool IsTarget { get; }
}
