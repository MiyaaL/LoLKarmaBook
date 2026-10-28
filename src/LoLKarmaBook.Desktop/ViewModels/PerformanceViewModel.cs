using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Input;
using LoLKarmaBook.Scoring;
using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// 估算 ELO 分. The score is computed by <see cref="PerformanceCalculator"/>
/// from completed match rows (in-app example or a local JSON import).
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private PerformanceScore? _score;
    private EstimatedElo? _estimate;
    private string _datasetSource = "尚未加载数据";
    private string _datasetMeta = string.Empty;
    private int _matchCount;
    private bool _isBusy;
    private string _errorText = string.Empty;
    private string _lastImportPath = string.Empty;

    /// <summary>Hexdata 海斗分: sign-in, query and the local report backups.</summary>
    public HexdataViewModel Hexdata => _main.Hexdata;

    public PerformanceViewModel(MainViewModel main)
    {
        _main = main;

        LoadDemoCommand = new AsyncRelayCommand(LoadDemoAsync, () => !_isBusy);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => !_isBusy);
        ExportTemplateCommand = new AsyncRelayCommand(ExportTemplateAsync, () => !_isBusy);
        ClearCommand = new RelayCommand(ClearResult, () => !_isBusy);
        InitializeRiot();
    }

    public ObservableCollection<ComponentRowViewModel> Components { get; } = new();

    public ObservableCollection<MatchRowPreview> MatchPreview { get; } = new();

    public PerformanceScore? Score
    {
        get => _score;
        private set
        {
            if (SetProperty(ref _score, value))
            {
                OnPropertyChanged(nameof(HasResult));
                OnPropertyChanged(nameof(ScoreText));
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(AvailabilityText));
                OnPropertyChanged(nameof(WinRateText));
                OnPropertyChanged(nameof(KdaText));
                OnPropertyChanged(nameof(NormalizedText));
                OnPropertyChanged(nameof(RawText));
                OnPropertyChanged(nameof(SampleText));
                OnPropertyChanged(nameof(MissingText));
                OnPropertyChanged(nameof(HasMissing));
                OnPropertyChanged(nameof(AlgorithmText));
                OnPropertyChanged(nameof(ScoreBarValue));
                OnPropertyChanged(nameof(WeightsText));
                OnPropertyChanged(nameof(HasEstimate));
                OnPropertyChanged(nameof(EstimateText));
                OnPropertyChanged(nameof(EstimateDetailText));
                OnPropertyChanged(nameof(EstimateTagText));
                OnPropertyChanged(nameof(EstimateQ10));
                OnPropertyChanged(nameof(EstimateQ50Ticks));
                OnPropertyChanged(nameof(EstimateQ90));
            }
        }
    }

    public EstimatedElo? Estimate
    {
        get => _estimate;
        private set
        {
            if (!SetProperty(ref _estimate, value)) return;
            OnPropertyChanged(nameof(HasEstimate));
            OnPropertyChanged(nameof(EstimateText));
            OnPropertyChanged(nameof(EstimateDetailText));
            OnPropertyChanged(nameof(EstimateTagText));
            OnPropertyChanged(nameof(EstimateQ10));
            OnPropertyChanged(nameof(EstimateQ50Ticks));
            OnPropertyChanged(nameof(EstimateQ90));
        }
    }

    public bool HasResult => _score is not null;

    public bool HasEstimate => _estimate is not null;

    /// <summary>
    /// A rough five-tier label for the estimate. The bands are this tool's own
    /// convention over its 1500-centred scale; they are not Riot tiers.
    /// </summary>
    public string EstimateTier => _estimate?.Rating switch
    {
        null => string.Empty,
        < 1400 => "偏下",
        < 1500 => "中下",
        < 1600 => "中段",
        < 1700 => "中上",
        _ => "偏上"
    };

    public string EstimateText => _estimate?.Rating is { } rating
        ? rating.ToString("0", CultureInfo.CurrentCulture)
        : "—";

    public string EstimateTagText => _estimate is null
        ? string.Empty
        : _estimate.IsProvisional
            ? $"估算 · {EstimateTier} · {_estimate.Games} 局样本偏少"
            : $"估算 · {EstimateTier} · {_estimate.Games} 局";

    public string EstimateDetailText => _estimate switch
    {
        null => "获取近期对局后即可计算。",
        var e when e.TeamElo is null || e.PerformanceScore is null =>
            $"当前团队 Elo 为 {Format(e.TeamElo)}。",
        var e =>
            $"团队 Elo {Format(e.TeamElo)}，表现分 {Format(e.PerformanceScore)}。"
    };

    // Scale the gauge to 1200..1800 so ordinary ratings sit in the middle band.
    public double EstimateQ10 => 1200;
    public double EstimateQ90 => 1800;
    public double EstimateQ50Ticks => _estimate?.Rating is { } rating
        ? Math.Clamp((rating - 1200) / 600 * 100.0, 0, 100)
        : 50;

    private static string Format(double? value) =>
        value is { } v ? v.ToString("0.0", CultureInfo.CurrentCulture) : "—";

    public bool IsAvailable => _score?.IsAvailable == true;

    public double ScoreBarValue => _score?.FinalScore ?? 0;

    public string ScoreText => _score?.FinalScore is { } value
        ? value.ToString("0.0", CultureInfo.CurrentCulture)
        : "—";

    public string AvailabilityText => _score switch
    {
        null => "未计算",
        { IsAvailable: false } => "数据不足，暂不计算。",
        _ => "可用"
    };

    public string WinRateText => _score is null
        ? "—"
        : $"{_score.WinCount} / {_score.GameCount}（{(_score.WinRate * 100).ToString("0.0", CultureInfo.CurrentCulture)}%）";

    public string KdaText => _score?.AggregateKda is { } kda
        ? kda.ToString("0.00", CultureInfo.CurrentCulture)
        : "不可用";

    public string NormalizedText => _score?.NormalizedScore is { } value
        ? value.ToString("0.0", CultureInfo.CurrentCulture)
        : "—";

    public string RawText => _score?.RawWeightedSum is { } value
        ? value.ToString("0.00", CultureInfo.CurrentCulture)
        : "—";

    public string SampleText => _score is null ? "—" : $"{_score.GameCount} 局已完成对局";

    public string MissingText => _score is null || _score.MissingMetrics.Count == 0
        ? "无（所有分项数据齐全）"
        : string.Join("、", _score.MissingMetrics.Select(MetricLabel));

    public bool HasMissing => _score is { MissingMetrics.Count: > 0 };

    public string AlgorithmText => _score is null
        ? PerformanceCalculator.AlgorithmVersion
        : _score.AlgorithmVersion;

    public string WeightsText => _score is null
        ? string.Empty
        : $"按可用分项计算，权重共 {_score.Weights.Total.ToString("0", CultureInfo.CurrentCulture)}%。";

    public string DatasetSource
    {
        get => _datasetSource;
        private set => SetProperty(ref _datasetSource, value);
    }

    public string DatasetMeta
    {
        get => _datasetMeta;
        private set => SetProperty(ref _datasetMeta, value);
    }

    public int MatchCount
    {
        get => _matchCount;
        private set => SetProperty(ref _matchCount, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommands();
            }
        }
    }

    public string ErrorText
    {
        get => _errorText;
        private set
        {
            if (SetProperty(ref _errorText, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    public string LastImportPath
    {
        get => _lastImportPath;
        private set
        {
            if (SetProperty(ref _lastImportPath, value))
            {
                OnPropertyChanged(nameof(HasLastImport));
            }
        }
    }

    public bool HasLastImport => !string.IsNullOrEmpty(LastImportPath);

    /// <summary>Injected by the window: opens an OS file picker. Null disables import.</summary>
    public Func<string, (string path, string content)?>? PickJsonFile { get; set; }

    /// <summary>Injected by the window: saves exported text. Null disables export.</summary>
    public Func<string, string, string?>? SaveTextFile { get; set; }

    public IAsyncCommand LoadDemoCommand { get; }

    public IAsyncCommand ImportCommand { get; }

    public IAsyncCommand ExportTemplateCommand { get; }

    public ICommand ClearCommand { get; }

    public bool CanImport => PickJsonFile is not null;

    public bool CanExport => SaveTextFile is not null;

    public void ClearResult()
    {
        Score = null;
        Estimate = null;
        Components.Clear();
        MatchPreview.Clear();
        MatchCount = 0;
        DatasetSource = "尚未加载数据";
        DatasetMeta = string.Empty;
        ErrorText = string.Empty;
        LastImportPath = string.Empty;
        RiotSourceText = string.Empty;
        _requestedMatches = 0;
        _receivedMatches = 0;
        FetchProgressText = string.Empty;
    }

    private Task LoadDemoAsync()
    {
        try
        {
            PerformanceDataset dataset = SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo().Dataset;
            ApplyDataset(dataset, "演示数据", null);
            _main.Status.Success("已载入内置合成估算 ELO 分示例。");
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "载入示例失败");
        }

        return Task.CompletedTask;
    }

    private async Task ImportAsync()
    {
        if (PickJsonFile is null)
        {
            ErrorText = "当前宿主未提供文件选择能力，无法导入。";
            return;
        }

        IsBusy = true;
        ErrorText = string.Empty;
        try
        {
            // The picker owns a WPF window and must run on its UI/STA thread.
            var picked = PickJsonFile("选择本地已完成对局数据（JSON，最多 5 MiB）");
            if (picked is not { } file)
            {
                return;
            }

            // Validate with the scoring module's strict parser; any failure is a
            // user-visible message and never an unhandled exception.
            PerformanceDataset dataset = await Task.Run(() => PerformanceJson.Parse(file.content))
                .ConfigureAwait(true);

            ApplyDataset(dataset, "本地导入文件", file.path);
            _main.Status.Success($"已导入并计算 {dataset.Matches.Count} 局本地数据。");
        }
        catch (ScoringValidationException ex)
        {
            ErrorText = $"导入被拒绝（{ex.Code}）：{DescribeScoringMessage(ex)}";
            _main.Status.Error(ErrorText);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            ErrorText = "读取文件失败：" + StatusMessageViewModel.Describe(ex);
            _main.Status.Error(ErrorText);
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "导入失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportTemplateAsync()
    {
        if (SaveTextFile is null)
        {
            ErrorText = "当前宿主未提供保存文件能力，无法导出模板。";
            return;
        }

        IsBusy = true;
        try
        {
            string json = await Task.Run(() =>
                    PerformanceJson.Export(SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo(3).Dataset))
                .ConfigureAwait(true);
            string? saved = SaveTextFile("performance-template.json", json);
            if (saved is not null)
            {
                _main.Status.Success("已保存导入模板：" + saved);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorText = "保存模板失败：" + StatusMessageViewModel.Describe(ex);
        }
        catch (Exception ex)
        {
            ReportFailure(ex, "导出模板失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyDataset(PerformanceDataset dataset, string source, string? path)
    {
        PerformanceScore score = PerformanceCalculator.Calculate(dataset);

        // The estimate needs rosters, which plain stat rows do not carry. When the
        // file has them (the exported template and the built-in demo do) the card
        // appears; when it does not, the page keeps showing the performance score
        // alone rather than inventing teammates.
        EstimatedElo? estimate = TryEstimate(dataset);

        ErrorText = string.Empty;
        if (!source.StartsWith("Riot API", StringComparison.Ordinal))
        {
            RiotSourceText = string.Empty;
        }

        Score = score;
        Estimate = estimate;
        MatchCount = dataset.Matches.Count;
        DatasetSource = source;
        DatasetMeta = dataset.Source.StartsWith("riot-api:", StringComparison.Ordinal)
            || dataset.Source.StartsWith("lcu-history:", StringComparison.Ordinal)
            ? $"共 {dataset.Matches.Count} 局已完成对局。"
            : $"共 {dataset.Matches.Count} 局 · {dataset.Source}";
        LastImportPath = path ?? string.Empty;

        Components.Clear();
        Components.Add(new ComponentRowViewModel(
            "胜率",
            NullablePercent(score.WinRate),
            score.Contributions.WinRate,
            score.Weights.WinRate,
            $"{score.WinCount}/{score.GameCount} 胜"));
        Components.Add(new ComponentRowViewModel(
            "KDA 项",
            score.AggregateKda is { } kda ? kda.ToString("0.00", CultureInfo.CurrentCulture) : "不可用",
            score.Contributions.Kda,
            score.Weights.Kda,
            $"聚合 KDA {KdaText}，除以 8 后归一"));
        Components.Add(new ComponentRowViewModel(
            "参团率",
            NullablePercent(score.Components.KillParticipation),
            score.Contributions.KillParticipation,
            score.Weights.KillParticipation,
            "（击杀+助攻）/ 队伍总击杀，逐局平均"));
        Components.Add(new ComponentRowViewModel(
            "伤害转化",
            score.Components.Conversion is { } conv ? conv.ToString("0.000", CultureInfo.CurrentCulture) : "不可用",
            score.Contributions.Conversion,
            score.Weights.Conversion,
            "伤害占比 ÷ 经济占比 ÷ 1.6"));
        Components.Add(new ComponentRowViewModel(
            "伤害占比",
            NullablePercent(score.Components.DamageShare),
            score.Contributions.Damage,
            score.Weights.Damage,
            "个人对英雄伤害 / 队伍伤害，逐局平均"));
        Components.Add(new ComponentRowViewModel(
            "承伤占比",
            NullablePercent(score.Components.TakenShare),
            score.Contributions.Taken,
            score.Weights.Taken,
            "个人承受伤害 / 队伍承受伤害，逐局平均"));

        MatchPreview.Clear();
        foreach (MatchStatRow row in dataset.Matches
                     .OrderByDescending(m => m.MatchId, StringComparer.Ordinal)
                     .Take(200))
        {
            MatchPreview.Add(new MatchRowPreview(
                row.MatchId,
                row.Win ? "胜" : "负",
                $"{row.Kills}/{row.Deaths}/{row.Assists}",
                row.TotalDamageDealt.ToString("0", CultureInfo.CurrentCulture),
                row.GoldEarned.ToString("0", CultureInfo.CurrentCulture)));
        }
    }

    private EstimatedElo? TryEstimate(PerformanceDataset dataset)
    {
        try
        {
            return EstimateBuilder.FromPerformanceDataset(dataset);
        }
        catch (Exception ex) when (ex is ScoringValidationException or ArgumentException or FormatException)
        {
            _main.Log.Warn("performance", "估算 ELO 需要队伍名单，本数据集不可估算：" + ex.Message);
            return null;
        }
    }

    private static string NullablePercent(double? value) =>
        value is { } v ? (v * 100).ToString("0.0", CultureInfo.CurrentCulture) + "%" : "不可用";

    public static string MetricLabel(string code) => code switch
    {
        "aggregate-kda" => "聚合 KDA",
        "kill-participation" => "参团率",
        "damage-share" => "伤害占比",
        "gold-share" => "经济占比",
        "conversion" => "伤害转化",
        "taken-share" => "承伤占比",
        _ => code
    };

    internal static string DescribeScoringMessage(ScoringValidationException ex) => ex.Code switch
    {
        "performance.source" => "source 不能为空，最多 120 字符，不能含首尾空白或控制字符。",
        "performance.schema-version" => "schemaVersion 必须为 1。",
        "performance.empty" => "数据中没有任何对局。",
        "performance.duplicate-match-id" => "存在重复的 matchId。",
        "performance.json" => "JSON 格式无效或包含未知字段。",
        "performance.structure" => "缺少必填字段，或字段类型不正确。",
        "performance.file-too-large" or "scoring.file-too-large" => "文件超过 5 MiB 限制。",
        "performance.invalid-number" or "performance.invalid-count" => "存在负数、非数值或无限大的字段。",
        "performance.match-id" => "matchId 为空、含首尾空格或超过 200 字符。",
        "performance.roster" => "队伍名单（roster）与对局不匹配：每局需要 4 名队友、5 名对手，matchId 与顺序须与 matches 一致。",
        "performance.playerId" => "playerId 为空、含首尾空格或超过 120 字符。",
        "estimated-elo.player-missing" => "playerId 必须出现在每局名单中，且只在一方。",
        "estimated-elo.mismatch" => "队伍名单与统计行数量不一致。",
        "estimated-elo.result-mismatch" => "名单的胜负与统计行的 win 不一致。",
        _ => ex.Message
    };

    private void ReportFailure(Exception ex, string context)
    {
        ErrorText = context + "：" + StatusMessageViewModel.Describe(ex);
        _main.Log.Error("performance", context + ": " + ex.GetType().Name, ex);
    }

    private void RaiseCommands()
    {
        LoadDemoCommand.RaiseCanExecuteChanged();
        ImportCommand.RaiseCanExecuteChanged();
        ExportTemplateCommand.RaiseCanExecuteChanged();
        FetchRiotCommand.RaiseCanExecuteChanged();
    }
}

public sealed class ComponentRowViewModel
{
    public ComponentRowViewModel(string label, string valueText, double points, double weight, string note, bool hidden = false)
    {
        Label = label;
        ValueText = valueText;
        Points = points;
        Weight = weight;
        Note = note;
        Hidden = hidden;
    }

    public string Label { get; }

    public string ValueText { get; }

    public double Points { get; }

    public double Weight { get; }

    public string Note { get; }

    public bool Hidden { get; }

    public string PointsText => Weight > 0
        ? $"{Points.ToString("0.0", CultureInfo.CurrentCulture)} / {Weight.ToString("0", CultureInfo.CurrentCulture)} 分"
        : "不计入（缺数据）";

    public double BarValue => Weight > 0 ? Math.Clamp(Points / Weight * 100.0, 0, 100) : 0;
}

public sealed class MatchRowPreview
{
    public MatchRowPreview(string matchId, string result, string kda, string damage, string gold)
    {
        MatchId = matchId;
        Result = result;
        Kda = kda;
        Damage = damage;
        Gold = gold;
    }

    public string MatchId { get; }

    public string Result { get; }

    public string Kda { get; }

    public string Damage { get; }

    public string Gold { get; }
}
