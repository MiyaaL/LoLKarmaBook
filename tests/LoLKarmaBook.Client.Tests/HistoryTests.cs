using System.Net;
using System.Text;
using System.Text.Json;
using LoLKarmaBook.Client;
using LoLKarmaBook.Scoring.Performance;
using LoLKarmaBook.Scoring.Rating;

static class HistoryTests
{
    private static ClientAccount Account => new("owner-cn-puuid", "Private name", "CN", "HN1", ClientAccount.CreateProfileKey("puuid:owner-cn-puuid"), 1);
    public static IEnumerable<(string Name, Func<Task> Run)> All()
    {
        yield return ("CN history: KIWI map 12 preserves own counters and hashed full roster", () =>
        {
            Check(LcuHistoryParser.TryParse(Json(Game(1)), Account, out var match), "CN KIWI match rejected");
            Check(match!.QueueId == 2400 && match.MapId == 12 && match.Roster.Teammates.Count == 4 && match.Roster.Opponents.Count == 5, "metadata/roster wrong");
            var score = PerformanceCalculator.Calculate([match.Stats]);
            Check(match.Stats.TotalDamageDealt == 5000 && score.Components.DamageShare == 5000.0 / 9000
                && score.Components.TakenShare == 4000.0 / 8000 && score.Components.GoldShare == 10000.0 / 14000, "owner counters became team totals");
            string encoded = JsonSerializer.Serialize(match);
            Check(!encoded.Contains("cn-puuid", StringComparison.Ordinal) && !encoded.Contains("Private name", StringComparison.Ordinal), "private source identifiers reached dataset");
            var estimate = EstimatedEloCalculator.Calculate(match.OwnerId,
                [new RatingMatchInput(match.Roster.MatchId, match.Roster.Timestamp, [..match.Roster.Teammates, match.OwnerId], match.Roster.Opponents, TeamSide.A)], [match.Stats], "lcu-history:HN1:2400");
            Check(double.IsFinite(estimate.Rating), "CN history cannot feed estimator");
            return Task.CompletedTask;
        });
        yield return ("CN history: supported ARAM and classic maps stay distinct from other maps", () =>
        {
            Check(LcuHistoryParser.TryParse(Json(Game(1, 450, 14, "ARAM")), Account, out _), "alternate ARAM map rejected");
            Check(LcuHistoryParser.TryParse(Json(Game(1, 420, 11, "CLASSIC")), Account, out _), "classic rejected");
            Check(!LcuHistoryParser.TryParse(Json(Game(1, 2400, 30, "KIWI")), Account, out _), "unknown map accepted");
            Check(!LcuHistoryParser.TryParse(Json(Game(1, 3140, 12, "PRACTICETOOL")), Account, out _), "practice mode treated as ARAM");
            Check(!LcuHistoryParser.TryParse(Json(Game(1).Replace("owner-cn-puuid", "unrelated-cn-puuid", StringComparison.Ordinal).Replace("\"summonerId\":1,", "\"summonerId\":999,", StringComparison.Ordinal)), Account, out _), "another owner's game accepted");
            Check(!LcuHistoryParser.TryParse(Json(Game(1).Replace("owner-cn-puuid", "unrelated-cn-puuid", StringComparison.Ordinal)), Account, out _), "conflicting PUUID was overridden by summoner ID");
            return Task.CompletedTask;
        });
        yield return ("CN history: paginates past other queues and fetches partial game details", async () =>
        {
            var scenario = new HistoryScenario { Page = begin => begin == 0 ? Enumerable.Range(1, 20).Select(id => Summary(id, 450)).ToArray() : [Summary(21, 2400), Summary(22, 2400)] };
            using var service = scenario.Service();
            var result = await service.GetHistoryAsync(null, 2, 2400);
            Check(result.Status == LcuHistoryStatus.Success && result.Dataset!.Matches.Count == 2 && result.ScannedMatches == 22 && result.SkippedMatches == 20, "filtered pagination lost matching games");
            Check(scenario.Paths.Any(path => path.Contains("begIndex=20", StringComparison.Ordinal)) && scenario.Paths.Count(path => path.StartsWith("/lol-match-history/v1/games/", StringComparison.Ordinal)) == 2, "pagination/details not requested");
            Check(result.Dataset!.Source == "lcu-history:HN1:2400", "CN queue scope missing");
        });
        yield return ("CN history: all queues remains explicit mixed selection and complete summaries need no detail call", async () =>
        {
            var scenario = new HistoryScenario { Page = _ => [Game(1), Game(2, 420, 11, "CLASSIC")] };
            using var service = scenario.Service();
            var result = await service.GetHistoryAsync(null, 20);
            Check(result.Status == LcuHistoryStatus.Success && result.Dataset!.Matches.Count == 2 && result.Dataset.Source.EndsWith(":all", StringComparison.Ordinal), "all queue selection was narrowed");
            Check(!scenario.Paths.Any(path => path.StartsWith("/lol-match-history/v1/games/", StringComparison.Ordinal)), "full summaries fetched twice");
        });
        yield return ("CN history: account switching drops the entire in-flight result", async () =>
        {
            var scenario = new HistoryScenario { SwitchAfterDetail = true, Page = _ => [Summary(1, 2400)] };
            using var service = scenario.Service();
            var result = await service.GetHistoryAsync(null, 1, 2400);
            Check(result.Status == LcuHistoryStatus.AccountChanged && result.Dataset is null && result.Account is null, "switched account received old history");
        });
        yield return ("CN history: caller cancellation interrupts detail body reading", async () =>
        {
            var scenario = new HistoryScenario { HangDetail = true, Page = _ => [Summary(1, 2400)] };
            using var service = scenario.Service();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            bool cancelled = false;
            try { await service.GetHistoryAsync(null, 1, 2400, cancellationToken: cancel.Token).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
            Check(cancelled, "history cancellation converted to a success/error result");
        });
        yield return ("CN history: repeated pages terminate and malformed identities do not become stable players", async () =>
        {
            var scenario = new HistoryScenario { Page = _ => Enumerable.Range(0, 20).Select(_ => Game(1)).ToArray() };
            using var service = scenario.Service();
            var result = await service.GetHistoryAsync(null, 20, 2400);
            Check(result.Status == LcuHistoryStatus.Success && result.Dataset!.Matches.Count == 1 && scenario.Paths.Count(path => path.Contains("/products/lol/", StringComparison.Ordinal)) == 2, "repeated page did not terminate");
            Check(!LcuHistoryParser.TryParse(Json(Game(1).Replace("peer-cn-puuid-2", "owner-cn-puuid", StringComparison.Ordinal)), Account, out _), "duplicate identity accepted");
        });
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static JsonElement Json(string json) { using var doc = JsonDocument.Parse(json); return doc.RootElement.Clone(); }
    private static string Summary(int id, int queue) => JsonSerializer.Serialize(new { gameId = id, queueId = queue });
    private static string Game(int id, int queue = 2400, int map = 12, string mode = "KIWI") => JsonSerializer.Serialize(new
    {
        gameId = id, queueId = queue, mapId = map, gameMode = mode, gameCreation = 1767225600000L + id * 60000, gameDuration = 1800,
        participantIdentities = Enumerable.Range(1, 10).Select(index => new { participantId = index, player = new {
            puuid = index == 1 ? "owner-cn-puuid" : "peer-cn-puuid-" + index, summonerId = index, summonerName = "Private name" } }),
        participants = Enumerable.Range(1, 10).Select(index => new { participantId = index, teamId = index <= 5 ? 100 : 200,
            stats = new { win = index <= 5, kills = index == 1 ? 4 : 1, deaths = 2, assists = 1,
                totalDamageDealtToChampions = index == 1 ? 5000 : 1000, totalDamageTaken = index == 1 ? 4000 : 1000,
                goldEarned = index == 1 ? 10000 : 1000 } })
    });

    private sealed class HistoryScenario
    {
        public Func<int, string[]> Page { get; init; } = _ => [Game(1)];
        public bool SwitchAfterDetail { get; init; }
        public bool HangDetail { get; init; }
        private bool _switched;
        public List<string> Paths { get; } = [];
        public LcuHistoryService Service() => new(new FixedDiscovery(), () => new Handler(this));
        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri!.IsLoopback && request.RequestUri.Scheme == "https", "nonlocal/write request");
            string path = request.RequestUri!.PathAndQuery;
            Paths.Add(path);
            string body;
            if (path == "/lol-summoner/v1/current-summoner") body = _switched ? """{"puuid":"changed-account","summonerId":2}""" : """{"puuid":"owner-cn-puuid","summonerId":1,"gameName":"Private name"}""";
            else if (path == "/lol-login/v1/session") body = """{"platformId":"HN1"}""";
            else if (path.StartsWith("/lol-match-history/v1/products/lol/", StringComparison.Ordinal))
            {
                Check(path.Contains("/owner-cn-puuid/", StringComparison.Ordinal), "requested another user's history");
                string beginText = path.Split("begIndex=")[1].Split('&')[0];
                body = "{\"games\":{\"games\":[" + string.Join(',', Page(int.Parse(beginText))) + "]}}";
            }
            else if (path.StartsWith("/lol-match-history/v1/games/", StringComparison.Ordinal))
            {
                if (HangDetail) return new(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) };
                body = Game(int.Parse(path.Split('/')[^1]));
                if (SwitchAfterDetail) _switched = true;
            }
            else return new(HttpStatusCode.NotFound);
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Handler(HistoryScenario scenario) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(scenario.Respond(request));
    }
}
