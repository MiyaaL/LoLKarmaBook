using System.Net;
using System.Text;
using System.Text.Json;
using LoLKarmaBook.Riot;
using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Riot.Tests;

internal static class RiotTests
{
    private const string Key = "RGAPI-test-key-never-printed";
    private const string Puuid = "OWNER-PUUID";

    private static readonly RiotId Id = new("Test Player", "EUW");

    public static void Register()
    {
        Runner.Add("platform: known ids map to regions", PlatformRegions);
        Runner.Add("platform: unknown and CN ids are rejected", PlatformRejects);
        Runner.Add("riot id: parses name#tag with spaces", RiotIdParsing);
        Runner.Add("match id: accepts EUW1_123 and rejects anything else", MatchIdValidation);

        Runner.Add("account: by-riot-id resolves PUUID with the key header", AccountLookup);
        Runner.Add("account: 401 rejects the key without echoing it", KeyRejected);
        Runner.Add("account: unsupported platform never calls the network", UnsupportedPlatform);
        Runner.Add("account: 429 retries once after Retry-After", RateLimitRetry);
        Runner.Add("account: 429 without a short hint fails as rate limited", RateLimitGivesUp);
        Runner.Add("account: repeated short 429 stops after one retry", RepeatedRateLimit);
        Runner.Add("account: cancellation interrupts the rate-limit wait", RateLimitCancellation);
        Runner.Add("account: a redirect is rejected without a second request", RedirectRejected);

        Runner.Add("matches: parser builds team totals from the owner's side", ParseMatch);
        Runner.Add("matches: ARAM and Mayhem keep complete rosters and personal statistics", ParseAram);
        Runner.Add("matches: remake, wrong map and 4v5 are skipped", ParseSkips);
        Runner.Add("matches: fetching pulls details and reports progress", FetchMatches);

        Runner.Add("estimate: parsed matches feed the estimator end to end", EndToEnd);
    }

    private static void PlatformRegions()
    {
        Assert.Equal("europe", RiotPlatforms.RegionFor("EUW1"));
        Assert.Equal("europe", RiotPlatforms.RegionFor("eun1"));
        Assert.Equal("americas", RiotPlatforms.RegionFor("NA1"));
        Assert.Equal("asia", RiotPlatforms.RegionFor("KR"));
        Assert.Equal("sea", RiotPlatforms.RegionFor("SG2"));
    }

    private static void PlatformRejects()
    {
        // The CN client's platform ids (HN1, TJ1, …) are not served by Riot's public API.
        Assert.True(RiotPlatforms.RegionFor("HN1") is null, "HN1 不应有区域。");
        Assert.True(RiotPlatforms.RegionFor("TJ1") is null, "TJ1 不应有区域。");
        Assert.True(RiotPlatforms.RegionFor("../evil") is null, "非法平台 ID 未被拒绝。");
        Assert.True(RiotPlatforms.RegionFor("") is null, "空平台 ID 未被拒绝。");
    }

    private static void RiotIdParsing()
    {
        Assert.True(RiotId.TryParse("Faker#KR1", out RiotId? a), "标准 Riot ID 未被接受。");
        Assert.Equal("Faker", a!.GameName);
        Assert.Equal("KR1", a.TagLine);

        Assert.True(RiotId.TryParse("with space#EUW", out RiotId? b), "含空格的游戏名未被接受。");
        Assert.Equal("with space", b!.GameName);

        Assert.True(!RiotId.TryParse("no-hash", out _), "缺少 # 的输入被接受了。");
        Assert.True(!RiotId.TryParse("#EUW", out _), "缺少游戏名被接受了。");
        Assert.True(!RiotId.TryParse("name#", out _), "缺少标签被接受了。");
        Assert.True(!RiotId.TryParse("name#ab", out _), "过短标签被接受了。");
        Assert.True(!RiotId.TryParse("name#a b", out _), "非法标签字符被接受了。");
    }

    private static void MatchIdValidation()
    {
        Assert.True(RiotApiClient.IsMatchId("EUW1_1234567890"), "合法对局编号被拒绝。");
        Assert.True(!RiotApiClient.IsMatchId("../../admin"), "路径穿越被接受。");
        Assert.True(!RiotApiClient.IsMatchId("EUW1_"), "缺少数字的对局编号被接受。");
        Assert.True(!RiotApiClient.IsMatchId("EUW1_12/../x"), "含斜杠的对局编号被接受。");
    }

    private static void AccountLookup()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("europe.api.riotgames.com", request.RequestUri!.Host);
            Assert.Equal("/riot/account/v1/accounts/by-riot-id/Test%20Player/EUW", request.RequestUri.AbsolutePath);
            Assert.Equal(Key, request.Headers.GetValues("X-Riot-Token").Single());
            return Json("""{"puuid":"OWNER-PUUID","gameName":"Test Player","tagLine":"EUW"}""");
        });

        var client = new RiotApiClient(handler);
        RiotAccount account = client.GetAccountAsync(Key, Id, Options()).GetAwaiter().GetResult();
        Assert.Equal(Puuid, account.Puuid);
        Assert.Equal("Test Player#EUW", account.DisplayLabel);
    }

    private static void KeyRejected()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var client = new RiotApiClient(handler);

        RiotApiException? caught = null;
        try
        {
            client.GetAccountAsync(Key, Id, Options()).GetAwaiter().GetResult();
        }
        catch (RiotApiException ex)
        {
            caught = ex;
        }

        Assert.True(caught is { Failure: RiotApiFailure.KeyRejected }, "401 未被识别为 Key 无效。");
        Assert.True(!caught!.Message.Contains("RGAPI", StringComparison.Ordinal), "异常消息泄露了 Key。");
        Assert.True(!caught.ToString().Contains("RGAPI", StringComparison.Ordinal), "异常文本泄露了 Key。");
    }

    private static void UnsupportedPlatform()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("不应发出请求。"));
        var client = new RiotApiClient(handler);
        Assert.ThrowsAsync(RiotApiFailure.PlayerNotFound,
            () => client.GetAccountAsync(Key, Id, Options("HN1"))).GetAwaiter().GetResult();
    }

    private static void RateLimitRetry()
    {
        var calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new StubHandler(request =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = new HttpResponseMessage((HttpStatusCode)429);
                limited.Headers.TryAddWithoutValidation("Retry-After", "2");
                return limited;
            }

            return Json("""{"puuid":"OWNER-PUUID","gameName":"Test Player","tagLine":"EUW"}""");
        });
        var client = new RiotApiClient(handler, (span, _) =>
        {
            delays.Add(span);
            return Task.CompletedTask;
        });
        client.GetAccountAsync(Key, Id, Options()).GetAwaiter().GetResult();
        Assert.Equal(2, calls);
        Assert.Equal(TimeSpan.FromSeconds(2), delays.Single());
    }

    private static void RateLimitGivesUp()
    {
        var calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.TryAddWithoutValidation("Retry-After", "600");
            return limited;
        });
        var client = new RiotApiClient(handler, (_, _) => Task.CompletedTask);
        Assert.ThrowsAsync(RiotApiFailure.RateLimited,
            () => client.GetAccountAsync(Key, Id, Options())).GetAwaiter().GetResult();
        Assert.Equal(1, calls);
    }

    private static void RepeatedRateLimit()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            // A third request would succeed, so the old unlimited recursion
            // fails this assertion instead of leaving the test hung forever.
            if (calls > 2) return Json("""{"puuid":"OWNER-PUUID"}""");
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.TryAddWithoutValidation("Retry-After", "1");
            return limited;
        });
        var client = new RiotApiClient(handler, (_, _) => Task.CompletedTask);
        Assert.ThrowsAsync(RiotApiFailure.RateLimited,
            () => client.GetAccountAsync(Key, Id, Options())).GetAwaiter().GetResult();
        Assert.Equal(2, calls);
    }

    private static async Task RateLimitCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.TryAddWithoutValidation("Retry-After", "1");
            return limited;
        });
        var client = new RiotApiClient(handler, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        bool cancelled = false;
        try { await client.GetAccountAsync(Key, Id, Options(), cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
        Assert.True(cancelled, "取消没有打断限流等待。");
        Assert.Equal(1, calls);
    }

    private static void RedirectRejected()
    {
        int calls = 0;
        var handler = new StubHandler(_ =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://example.invalid/collect");
            return redirect;
        });
        var client = new RiotApiClient(handler);
        Assert.ThrowsAsync(RiotApiFailure.UnexpectedStatus,
            () => client.GetAccountAsync(Key, Id, Options())).GetAwaiter().GetResult();
        Assert.Equal(1, calls);
    }

    private static void ParseMatch()
    {
        JsonDocument doc = JsonDocument.Parse(MatchJson(Puuid, win: true));
        Assert.True(RiotMatchParser.TryParse(doc.RootElement, Puuid, out RiotCompletedMatch? match, out string? why),
            "有效对局解析失败：" + why);
        Assert.Equal("EUW1_1234567890", match!.MatchId);
        Assert.Equal(4, match.Roster.Teammates.Count);
        Assert.Equal(5, match.Roster.Opponents.Count);
        Assert.True(!match.Roster.Teammates.Contains(Puuid), "队友里不应再有本人。");

        MatchStatRow row = match.Stats;
        Assert.Equal(4, row.Kills);
        Assert.Equal(2, row.Deaths);
        Assert.Equal(1, row.Assists);
        Assert.Equal(8, row.TeamTotalKills);
        Assert.Equal(5000.0 + 1000 + 1000 + 1000 + 1000, row.TeamTotalDamageDealt);
        Assert.Equal(4000.0 + 1000 + 1000 + 1000 + 1000, row.TeamTotalDamageTaken);
        Assert.Equal(10000.0 + 4000, row.TeamTotalGoldEarned);
        Assert.Equal(5000.0, row.TotalDamageDealt);
        Assert.Equal(4000.0, row.TotalDamageTaken);
        Assert.Equal(10000.0, row.GoldEarned);
        PerformanceScore score = PerformanceCalculator.Calculate([row]);
        Assert.Equal(5000.0 / 9000.0, score.Components.DamageShare);
        Assert.Equal(4000.0 / 8000.0, score.Components.TakenShare);
        Assert.Equal(10000.0 / 14000.0, score.Components.GoldShare);
        Assert.True(row.Win, "胜场标记丢失。");
    }

    private static void ParseAram()
    {
        foreach (var (map, queue, mode) in new[] { (12, 2400, "KIWI"), (12, 450, "ARAM"), (14, 450, "ARAM") })
        {
            using JsonDocument doc = JsonDocument.Parse(MatchJson(Puuid, true, mapId: map, queueId: queue, gameMode: mode));
            Assert.True(RiotMatchParser.TryParse(doc.RootElement, Puuid, out RiotCompletedMatch? match, out _), "大乱斗被错误过滤。");
            Assert.Equal(4, match!.Roster.Teammates.Count);
            Assert.Equal(5, match.Roster.Opponents.Count);
            PerformanceScore score = PerformanceCalculator.Calculate([match.Stats]);
            Assert.True(score.IsAvailable, "大乱斗无法计算表现分。");
            Assert.Equal(5000.0 / 9000.0, score.Components.DamageShare);
            Assert.Equal(4000.0 / 8000.0, score.Components.TakenShare);
        }
    }

    private static void ParseSkips()
    {
        JsonDocument remake = JsonDocument.Parse(MatchJson(Puuid, win: true, duration: 180, gameEndId: 0));
        Assert.True(!RiotMatchParser.TryParse(remake.RootElement, Puuid, out _, out string? r1), "重开局未被跳过。");
        Assert.True(r1!.Contains("重开"), "重开局的跳过原因不明确：" + r1);

        JsonDocument otherMap = JsonDocument.Parse(MatchJson(Puuid, win: true, mapId: 30));
        Assert.True(!RiotMatchParser.TryParse(otherMap.RootElement, Puuid, out _, out _), "不支持的地图未被跳过。");

        JsonDocument fourVsFive = JsonDocument.Parse(MatchJson(Puuid, win: true, ownPlayers: 4));
        Assert.True(!RiotMatchParser.TryParse(fourVsFive.RootElement, Puuid, out _, out _), "4v5 未被跳过。");

        JsonDocument wrongOwner = JsonDocument.Parse(MatchJson("SOMEONE-ELSE", win: true));
        Assert.True(!RiotMatchParser.TryParse(wrongOwner.RootElement, Puuid, out _, out _), "本人不在名单却解析成功。");
    }

    private static void FetchMatches()
    {
        var calls = new List<string>();
        var handler = new StubHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            calls.Add(path + (request.RequestUri.Query ?? string.Empty));
            if (path.EndsWith("/ids", StringComparison.Ordinal))
            {
                return Json("""["EUW1_1","EUW1_2"]""");
            }

            string id = path[(path.LastIndexOf('/') + 1)..];
            // EUW1_1 is a seven-minute remake and must be dropped.
            return Json(MatchJson(Puuid, win: true, matchId: id,
                duration: id == "EUW1_1" ? 240 : 1800, gameEndId: id == "EUW1_1" ? 0 : 2));
        });

        var client = new RiotApiClient(handler, (_, _) => Task.CompletedTask);
        var progress = new SyncProgress();
        var account = new RiotAccount(Puuid, "Test Player", "EUW", "EUW1");
        IReadOnlyList<RiotCompletedMatch> matches = client
            .GetCompletedMatchesAsync(Key, account, Options(), progress)
            .GetAwaiter().GetResult();

        Assert.Equal(1, matches.Count);
        Assert.Equal("EUW1_2", matches[0].MatchId);
        Assert.True(progress.Values.Contains(1), "没有上报进度。");
        Assert.True(calls[0].Contains("queue=420", StringComparison.Ordinal), "队列筛选没有传达到请求里。");
    }

    private static void EndToEnd()
    {
        JsonDocument doc = JsonDocument.Parse(MatchJson(Puuid, win: true));
        RiotMatchParser.TryParse(doc.RootElement, Puuid, out RiotCompletedMatch? match, out _);

        var input = new LoLKarmaBook.Scoring.Rating.RatingMatchInput(
            match!.Roster.MatchId,
            match.Roster.Timestamp,
            [.. match.Roster.Teammates, Puuid],
            match.Roster.Opponents,
            LoLKarmaBook.Scoring.Rating.TeamSide.A);

        LoLKarmaBook.Scoring.Rating.EstimatedElo estimate = LoLKarmaBook.Scoring.Rating.EstimatedEloCalculator
            .Calculate(Puuid, [input], [match.Stats], "riot-api");
        Assert.True(estimate.Games == 1, "对局数不正确。");
        Assert.True(estimate.Rating is > 1000 and < 2000, "估算分数不在合理区间：" + estimate.Rating);
        Assert.True(estimate.IsProvisional, "单局应标记为暂定。");
    }

    private static RiotApiOptions Options(string platform = "EUW1") => new() { PlatformId = platform, MatchCount = 2 };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    /// <summary>
    /// One match document with ten participants. The owner (or whichever PUUID is
    /// asked for) is on team 100 with 4 kills; team 100 totals 8 kills, 9000
    /// damage, 8000 taken, 14000 gold.
    /// </summary>
    private static string MatchJson(
        string owner, bool win, int mapId = 11, long duration = 1800,
        string matchId = "EUW1_1234567890", int ownPlayers = 5, int gameEndId = 2,
        int queueId = 420, string gameMode = "CLASSIC")
    {
        var participants = new List<string>();

        // Slot 0 is the owner. When a test asks for a stranger as owner, that
        // stranger takes the slot and the real PUUID plays as a plain teammate.
        for (var i = 0; i < ownPlayers; i++)
        {
            string puuid = i == 0 ? owner : "ALLY-" + i;
            bool isTracked = puuid == Puuid;
            participants.Add(Participant(puuid, 100, win, isTracked ? 4 : 1, isTracked ? 2 : 3, isTracked ? 1 : 4,
                isTracked ? 5000 : 1000, isTracked ? 4000 : 1000, isTracked ? 10000 : 1000));
        }

        for (var i = 0; i < 5; i++)
        {
            participants.Add(Participant("ENEMY-" + (i + 1), 200, !win, 3, 4, 5, 6000, 5000, 9000));
        }

        return $$"""
        {
          "metadata": { "matchId": "{{matchId}}" },
          "info": {
            "mapId": {{mapId}},
            "queueId": {{queueId}},
            "gameMode": "{{gameMode}}",
            "gameEndId": {{gameEndId}},
            "gameDuration": {{duration}},
            "gameStartTimestamp": 1767225600000,
            "gameEndTimestamp": {{1767225600000L + duration * 1000}},
            "gameCreation": 1767225600000,
            "participants": [ {{string.Join(",", participants)}} ]
          }
        }
        """;
    }

    private static string Participant(
        string puuid, int teamId, bool win, long kills, long deaths, long assists,
        long damage, long taken, long gold) => $$"""
        {
          "puuid": "{{puuid}}",
          "teamId": {{teamId}},
          "win": {{(win ? "true" : "false")}},
          "kills": {{kills}},
          "deaths": {{deaths}},
          "assists": {{assists}},
          "totalDamageDealtToChampions": {{damage}},
          "totalDamageTaken": {{taken}},
          "goldEarned": {{gold}}
        }
        """;

    /// <summary>
    /// <see cref="Progress{T}"/> posts callbacks to the pool, so a test that
    /// reads the list right after the await often sees nothing. This one reports
    /// inline, which is what the UI thread effectively does.
    /// </summary>
    private sealed class SyncProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];

        public void Report(int value) => Values.Add(value);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
