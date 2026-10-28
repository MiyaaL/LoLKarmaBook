using System.Security.Cryptography;
using System.Text.Json;
using LoLKarmaBook.Core;
using LoLKarmaBook.Riot;
using LoLKarmaBook.Storage;
using LoLKarmaBook.Scoring;
using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

try
{
    switch (args.FirstOrDefault() ?? "demo")
    {
        case "demo":
            await RunNotebookDemo();
            PrintPerformance(PerformanceCalculator.Calculate(SyntheticScenarioGenerator.CreatePerformanceDemo()));
            PrintRating();
            PrintEstimate();
            break;
        case "performance" when args.Length == 2:
            PrintPerformance(PerformanceCalculator.Calculate(PerformanceJson.LoadFile(args[1])));
            break;
        case "export-demo" when args.Length == 2:
            WriteDemo(args[1]);
            break;
        case "rating-demo":
            PrintRating();
            break;
        case "estimate-demo":
            PrintEstimate();
            break;
        case "estimate-riot" when args.Length >= 3:
            // Prefer the environment: an argument lands in the shell history.
            await PrintRiotEstimate(args[1], args[2], args.Length >= 4 ? args[3] : Environment.GetEnvironmentVariable("RIOT_API_KEY"));
            break;
        case "verify-performance-reference" when args.Length == 2:
            VerifyReference(args[1]);
            break;
        default:
            Console.Error.WriteLine("用法：LoLKarmaBook.Cli demo | performance <赛后统计.json> | rating-demo | estimate-demo | export-demo <输出.json>"
                + " | estimate-riot <区服如 EUW1> <Riot ID 如 Faker#KR1> <API Key>（或设置环境变量 RIOT_API_KEY）");
            return 2;
    }
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"操作失败：{ex.Message}");
    return 1;
}

static async Task RunNotebookDemo()
{
    var directory = Path.Combine(Path.GetTempPath(), "lolkarmabook-demo-" + Guid.NewGuid().ToString("N"));
    using var protector = SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
    try
    {
        var provider = new SyntheticProvider();
        await using (var store = await SqliteProfileStore.OpenAsync(directory, "synthetic-demo", protector))
        {
            var notebook = new NotebookService(store);
            await notebook.SaveNoteAsync(provider.GetMatchA(), SyntheticScenario.MatchAXParticipantKey,
                new NoteDraft("demo-note", [NoteCategory.AfkLeave, NoteCategory.VerbalAbuse],
                    "合成数据：上一局手动记录，默认提醒 365 天。"));
            var alerts = await notebook.EvaluateRemindersAsync(provider.GetMatchB());
            if (alerts.Reminders.Count != 1 || alerts.Reminders[0].Identity != SyntheticScenario.PlayerXIdentity
                && !alerts.Reminders[0].Identity.Equals(SyntheticScenario.PlayerXIdentity))
                throw new InvalidOperationException("再遇演示未能精确匹配 X。");
            var alert = alerts.Reminders[0];
            Console.WriteLine($"再遇提醒：{alert.CurrentDisplayId}，{string.Join("、", alert.ActiveCategories.Select(NoteCategories.GetDisplayName))}，涉及 {alert.DistinctSessionCount} 局。");
        }
        await using (var reopened = await SqliteProfileStore.OpenAsync(directory, "synthetic-demo", protector))
        {
            var notebook = new NotebookService(reopened);
            var repeat = await notebook.EvaluateRemindersAsync(provider.GetMatchB());
            if (repeat.Reminders.Count != 0) throw new InvalidOperationException("重启后出现重复提醒。");
            Console.WriteLine("SQLite 重开验证：笔记保留，同局不重复提醒，同名 Y 未误报。");
        }
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

static void PrintPerformance(PerformanceScore result)
{
    var score = result.FinalScore?.ToString("F2") ?? "不可计算";
    Console.WriteLine($"近期表现分：{score}，{result.GameCount} 场，算法 {result.AlgorithmVersion}。");
    if (result.MissingMetrics.Count > 0) Console.WriteLine("缺失分项：" + string.Join("、", result.MissingMetrics));
}

static void PrintRating()
{
    var result = SyntheticRatingCalculator.Calculate(SyntheticScenarioGenerator.CreateRatingDemo(),
        [SyntheticScenarioGenerator.DefaultTargetPlayerId]);
    var target = result.Players.Single();
    Console.WriteLine($"团队 Elo 示例：{target.Rating:F2}，{target.Games} 场，算法 {result.AlgorithmVersion}。");
}

/// <summary>Regenerates the committed fixture used by the scoring tests.</summary>
static void WriteDemo(string path)
{
    PerformanceDataset dataset = SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo().Dataset;
    File.WriteAllText(path, PerformanceJson.Export(dataset));
    Console.WriteLine($"已写出 {dataset.Matches.Count} 局估算示例：" + path);
}

static async Task PrintRiotEstimate(string platform, string riotIdText, string? apiKey)
{
    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new ArgumentException("缺少 Riot API Key：传入第四个参数或设置环境变量 RIOT_API_KEY。");
    }

    if (!RiotId.TryParse(riotIdText, out RiotId? riotId) || riotId is null)
    {
        throw new ArgumentException("Riot ID 格式应为 名称#标签。");
    }

    var options = new RiotApiOptions { PlatformId = platform };
    if (options.Region is null)
    {
        throw new ArgumentException($"区服 {platform} 不受 Riot 官方 API 支持。");
    }

    // The key never enters a message: RiotApiException messages are key-free by design.
    var client = new RiotApiClient(requestTimeout: options.RequestTimeout + TimeSpan.FromSeconds(5));
    RiotAccount account = await client.GetAccountAsync(apiKey, riotId, options);
    IReadOnlyList<RiotCompletedMatch> matches = await client.GetCompletedMatchesAsync(apiKey, account, options, null);
    if (matches.Count == 0)
    {
        throw new InvalidOperationException("筛选条件下没有可用对局。");
    }

    var rows = matches.Select(m => m.Stats).ToArray();
    var roster = new PerformanceRoster(account.Puuid,
        matches.Select(m => new PerformanceRosterMatch(m.Roster.MatchId, m.Roster.Timestamp, m.Roster.Teammates, m.Roster.Opponents))
            .ToArray());
    EstimatedElo estimate = EstimatedEloCalculator.Calculate(account.Puuid, roster.ToRatingMatches(rows), rows,
        "riot-api:" + options.PlatformId);
    PrintEstimateLine(estimate, account.DisplayLabel + " @ " + options.PlatformId + $"，{rows.Length} 局");
}

static void PrintEstimate() =>
    PrintEstimateLine(SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo().Estimate, "示例");

static void PrintEstimateLine(EstimatedElo result, string subject)
{
    Console.WriteLine($"估算 ELO（{subject}）：{(result.IsProvisional ? "暂定 " : "")}{result.Rating:F2}，{result.Wins} 胜 / {result.Games} 场，"
        + $"团队 Elo {result.TeamElo:F2}，表现分 {(result.PerformanceScore?.ToString("F2") ?? "不可用")}，算法 {result.AlgorithmVersion}。"
        + " 这是本工具的估算，不是 Riot 官方分数。");
}

static void VerifyReference(string path)
{
    var info = new FileInfo(path);
    if (info.Length > 5 * 1024 * 1024) throw new ArgumentException("参考样例超过大小限制。");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    int passed = 0;
    foreach (var item in document.RootElement.EnumerateArray())
    {
        var rows = item.GetProperty("rows").EnumerateArray().Select((row, index) => new MatchStatRow(
            index.ToString("D4"), row.GetProperty("win").GetBoolean(),
            (int)Number(row, "kills"), (int)Number(row, "deaths"), (int)Number(row, "assists"),
            (int)Number(row, "team_total_kills"), Number(row, "total_damage_dealt"), Number(row, "team_total_damage_dealt"),
            Number(row, "total_damage_taken"), Number(row, "team_total_damage_taken"),
            Number(row, "gold_earned"), Number(row, "team_total_gold_earned"))).ToArray();
        double? expected = item.GetProperty("expected").ValueKind == JsonValueKind.Null ? null : item.GetProperty("expected").GetDouble();
        double? actual;
        try { actual = PerformanceCalculator.Calculate(rows).FinalScore; }
        catch (ScoringValidationException) when (rows.Length == 0 && expected is null) { actual = null; }
        if (expected.HasValue != actual.HasValue || expected.HasValue && Math.Abs(expected.Value - actual!.Value) > 1e-10)
            throw new InvalidOperationException($"参考比对失败：{item.GetProperty("name").GetString()}，expected={expected}，actual={actual}");
        passed++;
    }
    Console.WriteLine($"参考实现比对通过：{passed} 组（容差 1e-10）。");
}

static double Number(JsonElement row, string key) => row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
