using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LoLKarmaBook.Client;
using LoLKarmaBook.Core;

const string AccountJson = """{"puuid":"puuid-self","summonerId":1,"gameName":"Self","tagLine":"EUW"}""";
const string GameJson = """{"gameData":{"gameId":10001,"queue":{"id":420},"map":{"id":11},"teamOne":[{"puuid":"puuid-self","summonerId":1,"gameName":"Self","championId":1},{"puuid":"puuid-peer","summonerId":2,"gameName":"Peer","championId":2}],"teamTwo":[{"puuid":"puuid-other","summonerId":3,"gameName":"Peer","championId":3},{"puuid":"do-not-expose","summonerId":987654,"gameName":"Do not expose","isObfuscated":true,"championId":4}]}}""";
const string SelectionJson = """{"gameId":10001,"localPlayerCellId":0,"myTeam":[{"cellId":0,"summonerId":0,"gameName":"Self"}],"theirTeam":[{"cellId":5,"summonerId":987654,"isObfuscated":true,"gameName":"Do not expose","puuid":"do-not-expose"}]}""";

if (args.Contains("--probe-history-schema", StringComparer.Ordinal)) return await HistorySchemaProbe.RunAsync();
if (args.Contains("--probe-client-queues", StringComparer.Ordinal)) return await HistorySchemaProbe.RunAsync(queuesOnly: true);
if (args.Contains("--probe-history", StringComparer.Ordinal))
{
    using var history = new LcuHistoryService();
    var result = await history.GetHistoryAsync(null, count: 20, queueId: args.Contains("--hex", StringComparer.Ordinal) ? 2400 : null);
    Console.WriteLine(JsonSerializer.Serialize(new { Status = result.Status.ToString(), AccountDetected = result.Account is not null,
        MatchCount = result.Dataset?.Matches.Count ?? 0, result.ScannedMatches, result.SkippedMatches,
        RosterComplete = result.Dataset?.Roster?.Matches.All(match => match.Teammates.Count == 4 && match.Opponents.Count == 5) }));
    return result.Status == LcuHistoryStatus.Success ? 0 : 1;
}

if (args.Contains("--probe-local", StringComparer.Ordinal))
{
    using var service = new LcuClientService();
    ClientSnapshot first = await service.PollAsync();
    ClientSnapshot second = await service.PollAsync();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        ConnectionStatus = second.ConnectionStatus.ToString(), AccountDetected = second.Account is not null,
        PuuidPresent = !string.IsNullOrWhiteSpace(second.Account?.Puuid), Phase = second.Phase.ToString(),
        ParticipantCount = second.Session?.Participants.Count ?? 0,
        SelfCount = second.Session?.Participants.Count(p => p.IsSelf) ?? 0,
        StableIdentityCount = second.Session?.Participants.Count(p => p.IdentityStrength == IdentityStrength.VerifiedStable) ?? 0,
        SessionStable = first.Session is null || second.Session is null ? (bool?)null : first.Session.LocalSessionId == second.Session.LocalSessionId,
        SequenceAdvanced = first.Session is null || second.Session is null ? (bool?)null : second.Session.Sequence > first.Session.Sequence
    }));
    return second.IsConnected && second.Account is not null ? 0 : 1;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("lockfile and command-line credentials are parsed without exposing them", () =>
    {
        Require(LcuConnectionDiscovery.TryParseLockfile("LeagueClient:123:23456:test-secret:https", null, out var connection), "valid lockfile rejected");
        Require(connection!.Port == 23456 && connection.ProcessId == 123, "wrong connection");
        Require(!connection.ToString().Contains("test-secret", StringComparison.Ordinal), "credential appears in ToString");
        Require(!JsonSerializer.Serialize(connection).Contains("test-secret", StringComparison.Ordinal), "credential is publicly serialized");
        Require(LcuConnectionDiscovery.TryParseCommandLine("LeagueClientUx.exe \"--app-port=23456\" \"--remoting-auth-token=test-secret\"", 123, null, out _), "quoted arguments rejected");
        Require(!LcuConnectionDiscovery.TryParseLockfile("LeagueClient:123:23456:test-secret:http", null, out _), "HTTP allowed");
        Require(!LcuConnectionDiscovery.TryParseLockfile("LeagueClient:123:99999:test-secret:https", null, out _), "bad port allowed");
        Require(!LcuConnectionDiscovery.TryParseCommandLine("--app-port=0 --remoting-auth-token=x", 123, null, out _), "zero port allowed");
        return Task.CompletedTask;
    }),
    ("client phases map to application stages", () =>
    {
        foreach (var pair in new[] { ("None", GamePhase.Lobby), ("ReadyCheck", GamePhase.Matchmaking), ("ChampSelect", GamePhase.ChampionSelect),
            ("GameStart", GamePhase.Loading), ("InProgress", GamePhase.InGame), ("Reconnect", GamePhase.Loading), ("EndOfGame", GamePhase.Finished) })
            Require(LcuSnapshotParser.ParsePhase(pair.Item1) == pair.Item2, "phase mismatch");
        Require(LcuSnapshotParser.ParsePhase("new-unknown-phase") == GamePhase.Unknown, "unknown phase was invented");
        return Task.CompletedTask;
    }),
    ("gameflow roster keeps sides, stable identities and hidden boundaries", () =>
    {
        var account = LcuSnapshotParser.ParseAccount(Json(AccountJson), "EUW1");
        MatchSession session = Parse(account, GamePhase.InGame, GameJson);
        Require(session.SourceKind == SourceKind.Live && session.ExternalMatchId == "lcu:EUW1:10001", "wrong match scope");
        Require(session.Participants.Count == 4 && session.Participants.Count(p => p.IsSelf) == 1, "wrong roster/self");
        Require(session.Participants[0].Side == ParticipantSide.Ally && session.Participants[2].Side == ParticipantSide.Enemy, "sides wrong");
        Require(session.Participants[1].Identity?.Value == "puuid-peer", "stable PUUID missing");
        Participant hidden = session.Participants[3];
        Require(hidden.Visibility == RosterVisibility.Hidden && hidden.Identity is null && hidden.CurrentDisplayId is null && hidden.Champion is null, "hidden data escaped");
        string changed = GameJson.Replace("Peer", "Renamed", StringComparison.Ordinal);
        Require(Parse(account, GamePhase.InGame, changed).Participants[1].Identity!.Equals(session.Participants[1].Identity), "rename changed identity");
        return Task.CompletedTask;
    }),
    ("champion ids resolve to the client's localized names with an id fallback", async () =>
    {
        var scenario = new Scenario
        {
            Phase = "InProgress",
            Flow = """{"gameData":{"gameId":10001,"teamOne":[{"puuid":"puuid-self","summonerId":1,"gameName":"Self","championId":103},{"puuid":"puuid-peer","summonerId":2,"gameName":"Peer","championId":99999}],"teamTwo":[]}}""",
            Champions = """[{"id":-1,"name":"无"},{"id":103,"name":"九尾妖狐"}]"""
        };
        using var service = scenario.Service();
        ClientSnapshot snapshot = await service.PollAsync();
        Require(snapshot.Session!.Participants[0].Champion == "九尾妖狐", "localized champion name was not applied");
        Require(snapshot.Session.Participants[1].Champion == "#99999", "unknown champion id lost its fallback");
        Require(scenario.Requests.Count(r => r.RequestUri!.AbsolutePath == "/lol-game-data/assets/v1/champion-summary.json") == 1,
            "the champion list was fetched more than once");
    }),
    ("champion selection detects self by cell and never resolves anonymous identities", () =>
    {
        var account = LcuSnapshotParser.ParseAccount(Json(AccountJson), "EUW1");
        JsonElement selection = Json(SelectionJson);
        var session = LcuSnapshotParser.ParseSession(account, GamePhase.ChampionSelect, null, selection,
            new Dictionary<long, JsonElement>(), "selection", "fixture", 1, 1, DateTimeOffset.UtcNow)!;
        Require(session.Participants[0].IsSelf && session.Participants[0].Identity?.Value == "puuid-self", "local cell self missing");
        Require(session.Participants[1].Visibility != RosterVisibility.Visible && session.Participants[1].Identity is null, "anonymous name/identity invented");
        Require(!LcuSnapshotParser.VisibleSummonerIds(null, selection).Contains(987654), "hidden ID was scheduled for lookup");
        return Task.CompletedTask;
    }),
    ("missing stable identity stays name-only and same names do not merge", () =>
    {
        var account = LcuSnapshotParser.ParseAccount(Json(AccountJson), "unknown");
        var session = Parse(account, GamePhase.InGame, """{"gameData":{"gameId":10002,"teamOne":[{"gameName":"Same","summonerId":0}],"teamTwo":[{"gameName":"Same","summonerId":0}]}}""");
        Require(session.Participants.All(p => p.Identity is null && p.IdentityStrength == IdentityStrength.NameOnly), "name promoted to identity");
        Require(session.Participants[0].ParticipantKey != session.Participants[1].ParticipantKey, "same names merged");
        return Task.CompletedTask;
    }),
    ("poll is GET-only loopback and preserves a match across repeated observations", async () =>
    {
        var scenario = new Scenario();
        using var service = scenario.Service();
        ClientSnapshot first = await service.PollAsync();
        ClientSnapshot second = await service.PollAsync();
        Require(first.IsConnected && first.Account is not null && first.Session is not null, "poll did not connect");
        Require(first.Session!.LocalSessionId == second.Session!.LocalSessionId && second.Session.Sequence > first.Session.Sequence, "poll resets match identity");
        Require(first.Account!.ProfileKey.StartsWith("account-", StringComparison.Ordinal) && !first.Account.ProfileKey.Contains("puuid-self", StringComparison.Ordinal), "profile ID exposes account");
        Require(scenario.Requests.All(r => r.Method == HttpMethod.Get && r.RequestUri!.IsLoopback && r.RequestUri.Scheme == "https"), "non-local/write request observed");
    }),
    ("stable participant keys survive roster reordering, teams and game stages", () =>
    {
        var account = LcuSnapshotParser.ParseAccount(Json(AccountJson), "EUW1");
        var first = Parse(account, GamePhase.InGame, GameJson);
        var reordered = Parse(account, GamePhase.InGame, """{"gameData":{"gameId":10001,"teamOne":[{"puuid":"puuid-other","gameName":"Renamed"}],"teamTwo":[{"puuid":"puuid-peer","gameName":"Peer"},{"puuid":"puuid-self","gameName":"Self"}]}}""");
        var selection = LcuSnapshotParser.ParseSession(account, GamePhase.ChampionSelect, null,
            Json("""{"gameId":10001,"myTeam":[{"puuid":"puuid-self","gameName":"Self"},{"puuid":"puuid-peer","gameName":"Peer"}],"theirTeam":[{"puuid":"puuid-other","gameName":"Peer"}]}"""),
            new Dictionary<long, JsonElement>(), "fixture-session", "fixture-provider", 1, 2, DateTimeOffset.UtcNow)!;
        foreach (var original in first.Participants.Where(p => p.Identity is not null))
        {
            Require(reordered.Participants.Single(p => p.Identity?.Equals(original.Identity) == true).ParticipantKey == original.ParticipantKey, "reorder moved a stable player's draft key");
            Require(selection.Participants.Single(p => p.Identity?.Equals(original.Identity) == true).ParticipantKey == original.ParticipantKey, "stage moved a stable player's draft key");
            Require(!original.ParticipantKey.Contains(original.Identity!.Value, StringComparison.Ordinal), "participant key exposes identity");
        }
        return Task.CompletedTask;
    }),
    ("name-only roster replacements invalidate old draft keys and unknown slots are observation-scoped", () =>
    {
        var account = LcuSnapshotParser.ParseAccount(Json(AccountJson), "unknown");
        string original = """{"gameData":{"gameId":10001,"teamOne":[{"gameName":"First"},{"gameName":"Second"}],"teamTwo":[{}]}}""";
        var first = Parse(account, GamePhase.InGame, original);
        var unchanged = Parse(account, GamePhase.InGame, original);
        Require(first.Participants[0].ParticipantKey == unchanged.Participants[0].ParticipantKey, "unchanged visible name-only roster churns keys");
        var replaced = Parse(account, GamePhase.InGame, original.Replace("First", "Replacement", StringComparison.Ordinal));
        Require(!replaced.Participants.Any(p => p.ParticipantKey == first.Participants[0].ParticipantKey || p.ParticipantKey == first.Participants[1].ParticipantKey), "name-only slot replacement kept old draft keys");
        var reordered = Parse(account, GamePhase.InGame, original.Replace("First", "TEMP", StringComparison.Ordinal).Replace("Second", "First", StringComparison.Ordinal).Replace("TEMP", "Second", StringComparison.Ordinal));
        Require(reordered.Participants[0].ParticipantKey != first.Participants[0].ParticipantKey, "name-only reorder reused a slot key");
        var championChanged = Parse(account, GamePhase.InGame, original.Replace("\"First\"", "\"First\",\"championId\":99", StringComparison.Ordinal));
        Require(championChanged.Participants[0].ParticipantKey != first.Participants[0].ParticipantKey, "name-only row change reused an editor key");
        var next = LcuSnapshotParser.ParseSession(account, GamePhase.InGame, Json(original), null, new Dictionary<long, JsonElement>(), "fixture-session", "fixture-provider", 1, 2, DateTimeOffset.UtcNow)!;
        Require(first.Participants[2].ParticipantKey != next.Participants[2].ParticipantKey, "unknown row retained an editor key across observations");
        return Task.CompletedTask;
    }),
    ("a stalled response body reaches the request deadline and leaves polling reusable", async () =>
    {
        var scenario = new Scenario { Failure = "body-hang" };
        using var service = scenario.Service();
        var started = System.Diagnostics.Stopwatch.StartNew();
        ClientSnapshot timedOut = await service.PollAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Require(timedOut.ConnectionStatus == ClientConnectionStatus.Disconnected && started.Elapsed < TimeSpan.FromSeconds(9), "response body never timed out");
        scenario.Failure = "";
        Require((await service.PollAsync()).IsConnected, "deadline left the poll gate locked");
    }),
    ("caller cancellation interrupts a stalled body and preserves cancellation semantics", async () =>
    {
        var scenario = new Scenario { Failure = "body-hang" };
        using var service = scenario.Service();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        bool cancelled = false;
        try { await service.PollAsync(cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { cancelled = true; }
        Require(cancelled, "caller cancellation was converted to a connection result");
        scenario.Failure = "";
        Require((await service.PollAsync()).IsConnected, "cancellation left the poll gate locked");
    }),
    ("account switches and reconnects advance provider generation", async () =>
    {
        var scenario = new Scenario();
        using var service = scenario.Service();
        ClientSnapshot first = await service.PollAsync();
        scenario.Account = AccountJson.Replace("puuid-self", "puuid-other", StringComparison.Ordinal);
        ClientSnapshot switched = await service.PollAsync();
        Require(switched.Account!.ProfileKey != first.Account!.ProfileKey && switched.Session!.Generation > first.Session!.Generation, "account switch not isolated");
        scenario.Discovery.Current = new(ClientConnectionStatus.NotRunning, null, "not running");
        ClientSnapshot disconnected = await service.PollAsync();
        Require(disconnected.Session is null && disconnected.Account is null, "disconnection kept stale state");
        scenario.Discovery.Current = Found();
        ClientSnapshot reconnected = await service.PollAsync();
        Require(reconnected.Session!.Generation > switched.Session!.Generation && reconnected.Session.MatchKey == switched.Session.MatchKey, "reconnect loses real-match deduplication key");
    }),
    ("restart keeps known match scope and game-id arrival keeps the active session", async () =>
    {
        var scenario = new Scenario();
        using var firstService = scenario.Service();
        using var restartedService = scenario.Service();
        var first = await firstService.PollAsync();
        var restarted = await restartedService.PollAsync();
        Require(first.Session!.LocalSessionId == restarted.Session!.LocalSessionId, "known game cannot restore its session scope");
        scenario.Phase = "ChampSelect";
        scenario.Flow = """{"gameData":{"gameId":0}}""";
        scenario.Selection = """{"gameId":0,"localPlayerCellId":0,"myTeam":[{"cellId":0,"gameName":"Self"}],"theirTeam":[]}""";
        using var selectionService = scenario.Service();
        var selection = await selectionService.PollAsync();
        scenario.Phase = "InProgress";
        scenario.Flow = """{"gameData":{"gameId":10001,"teamOne":[{"puuid":"puuid-self","gameName":"Self"}],"teamTwo":[]}}""";
        var game = await selectionService.PollAsync();
        Require(selection.Session!.LocalSessionId == game.Session!.LocalSessionId, "game-id arrival resets active session");
        scenario.Flow = scenario.Flow.Replace("10001", "10002", StringComparison.Ordinal);
        var nextGame = await selectionService.PollAsync();
        Require(nextGame.Session!.LocalSessionId != game.Session.LocalSessionId, "new game reuses old session scope");
    }),
    ("logout clears account and increments ordering when the same user logs in", async () =>
    {
        var scenario = new Scenario();
        using var service = scenario.Service();
        var loggedIn = await service.PollAsync();
        string account = scenario.Account;
        scenario.Account = "{}";
        var loggedOut = await service.PollAsync();
        Require(loggedOut.ConnectionStatus == ClientConnectionStatus.NotLoggedIn && loggedOut.Account is null && loggedOut.Session is null, "logout retained private context");
        scenario.Account = account;
        var reloggedIn = await service.PollAsync();
        Require(reloggedIn.Session!.Generation > loggedIn.Session!.Generation, "relogin ordering did not advance");
    }),
    ("credentials-unavailable status is distinct from client-not-running", async () =>
    {
        var discovery = new FixedDiscovery { Current = new(ClientConnectionStatus.CredentialsUnavailable, null, "client present, metadata unavailable") };
        using var service = new LcuClientService(discovery);
        ClientSnapshot result = await service.PollAsync();
        Require(result.ConnectionStatus == ClientConnectionStatus.CredentialsUnavailable && result.Account is null, "permission status confused with no client");
    }),
    ("authentication failure, redirects and malformed data remain visible", async () =>
    {
        foreach (var mode in new[] { "auth", "redirect", "malformed" })
        {
            var scenario = new Scenario { Failure = mode };
            using var service = scenario.Service();
            ClientSnapshot result = await service.PollAsync();
            Require(!result.IsConnected && result.Session is null && result.Account is null, "invalid response appeared connected");
            Require(!result.Message.Contains("test-secret", StringComparison.Ordinal), "error disclosed credentials");
        }
    }),
    ("unrelated self-signed TLS certificate is rejected", () =>
    {
        using RSA rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        Require(!LcuClientService.ValidateRiotCertificate(certificate), "unrelated self-signed certificate trusted");
        return Task.CompletedTask;
    })
};

int failed = 0;
tests = tests.Concat(HistoryTests.All()).ToArray();
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.WriteLine("FAIL " + test.Name + ": " + ex.GetType().Name + " " + ex.Message); }
}
Console.WriteLine($"Client tests: {tests.Length - failed}/{tests.Length} passed.");
return failed == 0 ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
static MatchSession Parse(ClientAccount account, GamePhase phase, string flow) => LcuSnapshotParser.ParseSession(account, phase, Json(flow), null,
    new Dictionary<long, JsonElement>(), "fixture-session", "fixture-provider", 1, 1, DateTimeOffset.UtcNow)!;
static LcuDiscoveryResult Found() => new(ClientConnectionStatus.Connected, new LcuConnection(123, 23456, "test-secret"), "found");

sealed class FixedDiscovery : ILcuConnectionDiscovery
{
    public LcuDiscoveryResult Current { get; set; } = new(ClientConnectionStatus.Connected, new LcuConnection(123, 23456, "test-secret"), "found");
    public Task<LcuDiscoveryResult> DiscoverAsync(string? directory, CancellationToken cancellationToken) => Task.FromResult(Current);
}

sealed class Scenario
{
    public FixedDiscovery Discovery { get; } = new();
    public string Account { get; set; } = """{"puuid":"puuid-self","summonerId":1,"gameName":"Self","tagLine":"EUW"}""";
    public string Failure { get; set; } = "";
    public string Phase { get; set; } = "InProgress";
    public string Flow { get; set; } = """{"gameData":{"gameId":10001,"teamOne":[{"puuid":"puuid-self","summonerId":1,"gameName":"Self"}],"teamTwo":[{"puuid":"puuid-peer","summonerId":2,"gameName":"Peer"}]}}""";
    public string Selection { get; set; } = "{}";
    public string? Champions { get; set; }
    public List<HttpRequestMessage> Requests { get; } = new();
    public LcuClientService Service() => new(Discovery, () => new FakeHandler(this));
    public HttpResponseMessage Respond(HttpRequestMessage request)
    {
        Requests.Add(request);
        if (Failure == "auth") return new(HttpStatusCode.Unauthorized);
        if (Failure == "body-hang") return new(HttpStatusCode.OK) { Content = new StreamContent(new HangingStream()) };
        if (Failure == "redirect") { var redirect = new HttpResponseMessage(HttpStatusCode.Redirect); redirect.Headers.Location = new Uri("https://example.invalid"); return redirect; }
        string? body = request.RequestUri!.AbsolutePath switch
        {
            "/lol-summoner/v1/current-summoner" => Account,
            "/riotclient/region-locale" => """{"region":"EUW1","locale":"en_GB"}""",
            "/lol-login/v1/session" => """{"platformId":"EUW1"}""",
            "/lol-gameflow/v1/gameflow-phase" => JsonSerializer.Serialize(Phase),
            "/lol-gameflow/v1/session" => Flow,
            "/lol-champ-select/v1/session" => Selection,
            "/lol-game-data/assets/v1/champion-summary.json" => Champions,
            _ => null
        };
        return body is null ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent(Failure == "malformed" ? "{invalid-json" : body, Encoding.UTF8, "application/json") };
    }
}

sealed class FakeHandler(Scenario scenario) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(scenario.Respond(request));
}

sealed class HangingStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return 0;
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { }
}
