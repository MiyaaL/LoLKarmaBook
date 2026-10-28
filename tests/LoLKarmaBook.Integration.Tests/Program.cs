using System.Security.Cryptography;
using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;
using LoLKarmaBook.Scoring;
using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

var tests = new (string Name, Func<Task> Run)[]
{
    ("重复请求必须绑定原对局", () => WithStore(async (store, _) =>
    {
        var service = new NotebookService(store);
        var a = new SyntheticProvider().GetMatchA();
        var draft = new NoteDraft("request-same", [NoteCategory.AfkLeave], "同样内容");
        var first = await service.SaveNoteAsync(a, SyntheticScenario.MatchAXParticipantKey, draft);
        var retry = await service.SaveNoteAsync(a, SyntheticScenario.MatchAXParticipantKey, draft);
        Require(first.NoteId == retry.NoteId, "同一请求重试产生了重复记录。");
        var other = Clone(a, GamePhase.Finished, "different-game", "different-external-game");
        await MustReject(() => service.SaveNoteAsync(other, SyntheticScenario.MatchAXParticipantKey, draft));
    })),
    ("活动名单阶段可提醒，其他阶段不消耗提醒", () => WithStore(async (store, _) =>
    {
        var service = new NotebookService(store); var provider = new SyntheticProvider();
        var note = await service.SaveNoteAsync(provider.GetMatchA(), SyntheticScenario.MatchAXParticipantKey,
            new NoteDraft("phase", [NoteCategory.VerbalAbuse]));
        var b = provider.GetMatchB();
        foreach (var phase in new[] { GamePhase.Unknown, GamePhase.Lobby, GamePhase.Matchmaking, GamePhase.Disconnected })
        {
            var invalid = Clone(b, phase, $"inactive-{phase}", $"inactive-{phase}");
            Require((await service.FindRemindersAsync(invalid)).Count == 0, $"阶段 {phase} 没有活动对局名单却匹配历史。");
            Require((await service.EvaluateRemindersAsync(invalid)).Reminders.Count == 0, $"阶段 {phase} 产生通知。");
            Require(await store.GetAlertReceiptStateAsync(invalid.MatchKey, note.PlayerId!.Value) is null,
                $"阶段 {phase} 写入了提醒收据。");
        }
        // Post-game keeps the roster for the end-of-match preview, but never notifies
        // during play and never writes a receipt.
        var finished = Clone(b, GamePhase.Finished, "finished-preview", "finished-preview");
        Require((await service.FindRemindersAsync(finished)).Count == 1, "赛后名单未匹配历史。");
        Require((await service.EvaluateRemindersAsync(finished)).Reminders.Count == 0, "赛后阶段产生了通知。");
        Require(await store.GetAlertReceiptStateAsync(finished.MatchKey, note.PlayerId!.Value) is null,
            "赛后阶段写入了提醒收据。");

        foreach (var phase in new[] { GamePhase.ChampionSelect, GamePhase.Loading, GamePhase.InGame })
        {
            var active = Clone(b, phase, $"active-{phase}", $"active-{phase}");
            Require((await service.FindRemindersAsync(active)).Count == 1, $"阶段 {phase} 未匹配可靠身份的历史。");
            Require(await store.GetAlertReceiptStateAsync(active.MatchKey, note.PlayerId!.Value) is null,
                "历史预览不应消耗提醒收据。");
            Require((await service.EvaluateRemindersAsync(active)).Reminders.Count == 1, $"阶段 {phase} 缺少首次提醒。");
            Require((await service.EvaluateRemindersAsync(active)).Reminders.Count == 0, $"阶段 {phase} 同局重复提醒。");
        }
        Require((await service.EvaluateRemindersAsync(b)).Reminders.Count == 1, "其他对局的收据影响了当前对局。");
    })),
    ("已提示可查看，本局忽略后隐藏", () => WithStore(async (store, _) =>
    {
        var service = new NotebookService(store); var provider = new SyntheticProvider();
        await service.SaveNoteAsync(provider.GetMatchA(), SyntheticScenario.MatchAXParticipantKey,
            new NoteDraft("ignore", [NoteCategory.LowSkill]));
        var b = provider.GetMatchB();
        Require((await service.EvaluateRemindersAsync(b)).Reminders.Count == 1, "缺少首次提醒。");
        Require((await service.FindRemindersAsync(b)).Count == 1, "已提示记录不应从详情消失。");
        await service.IgnoreForSessionAsync(b, SyntheticScenario.PlayerXIdentity);
        Require((await service.FindRemindersAsync(b)).Count == 0, "忽略记录仍在详情出现。");
        Require((await service.EvaluateRemindersAsync(b)).Reminders.Count == 0, "忽略记录重新弹出。");
    })),
    ("默认一年有效期与五类多选", () => WithStore(async (store, _) =>
    {
        var service = new NotebookService(store);
        var note = await service.SaveNoteAsync(new SyntheticProvider().GetMatchA(), SyntheticScenario.MatchAXParticipantKey,
            new NoteDraft("year", NoteCategories.All.ToArray()));
        Require(note.Categories.Count == 5, "五类标签未全部保存。");
        foreach (var category in note.Categories)
            Require(category.ReminderExpiresAt - note.CreatedAt == TimeSpan.FromDays(365), "默认期限不是 365 天。");
    })),
    ("重复 JSON 字段必须拒绝", () =>
    {
        var valid = PerformanceJson.Export(SyntheticScenarioGenerator.CreatePerformanceDemo(), false);
        var duplicate = valid.Replace("\"source\":", "\"source\":\"wrong\",\"source\":", StringComparison.Ordinal);
        MustRejectScoring(() => PerformanceJson.Parse(duplicate));
        var rating = SyntheticRatingJson.Export(SyntheticScenarioGenerator.CreateRatingDemo(), false);
        MustRejectScoring(() => SyntheticRatingJson.Parse(rating.Replace("\"schemaVersion\":", "\"schemaVersion\":999,\"schemaVersion\":", StringComparison.Ordinal)));
        return Task.CompletedTask;
    }),
    ("评分导入接受并保留来源标签", () =>
    {
        var rating = SyntheticRatingJson.Export(SyntheticScenarioGenerator.CreateRatingDemo(), false);
        var performance = PerformanceJson.Export(SyntheticScenarioGenerator.CreatePerformanceDemo(), false);
        foreach (string source in new[] { "live", "manual" })
        {
            var importedRating = SyntheticRatingJson.Parse(rating.Replace("\"source\":\"synthetic\"", $"\"source\":\"{source}\"", StringComparison.Ordinal));
            Require(importedRating.Source == source, "团队评分来源未保留。");
            Require(SyntheticRatingJson.Parse(SyntheticRatingJson.Export(importedRating)).Source == source, "团队评分导出覆盖了来源。");
            var importedPerformance = PerformanceJson.Parse(performance.Replace("\"source\":\"user-owned-completed-matches\"", $"\"source\":\"{source}\"", StringComparison.Ordinal));
            Require(importedPerformance.Source == source, "表现分来源未保留。");
            Require(PerformanceJson.Parse(PerformanceJson.Export(importedPerformance)).Source == source, "表现分导出覆盖了来源。");
        }
        return Task.CompletedTask;
    })
};
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"监督回归测试：{tests.Length - failures}/{tests.Length} 通过。");
return failures == 0 ? 0 : 1;

static async Task WithStore(Func<SqliteProfileStore, string, Task> run)
{
    var root = Path.Combine(Path.GetTempPath(), "lolkarmabook-integration-" + Guid.NewGuid().ToString("N"));
    using var protector = SyntheticTestProtector.CreateForTestsOnly(RandomNumberGenerator.GetBytes(32));
    try
    {
        await using var store = await SqliteProfileStore.OpenAsync(root, "test", protector);
        await run(store, root);
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static MatchSession Clone(MatchSession source, GamePhase phase, string localId, string? externalId) =>
    new(localId, source.SourceKind, source.ProviderInstanceId, source.Generation, source.Sequence + 1,
        DateTimeOffset.UtcNow, phase, source.QueueId, source.MapId, externalId, source.RosterComplete, source.Participants);

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task MustReject(Func<Task> action)
{
    try { await action(); }
    catch (InvalidOperationException) { return; }
    catch (ArgumentException) { return; }
    throw new InvalidOperationException("应拒绝的请求被接受。");
}

static void MustRejectScoring(Action action)
{
    try { action(); }
    catch (ScoringValidationException) { return; }
    throw new InvalidOperationException("应拒绝的评分输入被接受。");
}
