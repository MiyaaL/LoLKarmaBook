using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Client;

/// <summary>One read-only localhost observation per call; scheduling belongs to the desktop host.</summary>
public sealed class LcuClientService : ILcuClientService, ILcuTagSource
{
    private readonly ILcuConnectionDiscovery _discovery;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<long, JsonElement> _summoners = new();
    private readonly Dictionary<int, string> _championNames = new();
    private readonly string _providerId = "lcu-" + Guid.NewGuid().ToString("N");
    private HttpClient? _http;
    private string? _fingerprint;
    private string? _accountKey;
    private string? _activeToken;
    private string? _activeGameId;
    private string _platformId = "";
    private long _generation;
    private long _sequence;
    private int _championNameAttempts;
    private bool _disposed;

    public LcuClientService(ILcuConnectionDiscovery? discovery = null, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _discovery = discovery ?? new LcuConnectionDiscovery();
        _handlerFactory = handlerFactory;
    }

    public async Task<ClientSnapshot> PollAsync(string? clientDirectory = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        LcuConnection? connection = null;
        try
        {
            LcuDiscoveryResult result = await _discovery.DiscoverAsync(clientDirectory, cancellationToken).ConfigureAwait(false);
            connection = result.Connection;
            if (connection is null)
            {
                ResetConnection();
                return Snapshot(result.Status, result.Message, null, null, GamePhase.Disconnected, clientDirectory);
            }
            EnsureConnection(connection);
            JsonElement? current = await GetJsonAsync("/lol-summoner/v1/current-summoner", cancellationToken).ConfigureAwait(false);
            if (current is null || !HasCurrentAccount(current.Value))
            {
                ResetAccount();
                return Snapshot(ClientConnectionStatus.NotLoggedIn, "客户端已启动，等待账号登录完成。", null, null, GamePhase.Unknown, connection.ClientDirectory);
            }

            if (_platformId.Length == 0)
            {
                JsonElement? region = await GetJsonAsync("/riotclient/region-locale", cancellationToken).ConfigureAwait(false);
                _platformId = LcuSnapshotParser.Text(region, "platformId", "region");
                JsonElement? login = await GetJsonAsync("/lol-login/v1/session", cancellationToken).ConfigureAwait(false);
                string platform = LcuSnapshotParser.Text(login, "platformId");
                if (platform.Length > 0) _platformId = platform;
                if (_platformId.Length == 0) _platformId = "unknown";
            }
            ClientAccount account = LcuSnapshotParser.ParseAccount(current.Value, _platformId);
            if (_accountKey != account.ProfileKey)
            {
                _accountKey = account.ProfileKey;
                _generation++;
                _sequence = 0;
                _activeToken = null;
                _activeGameId = null;
                _summoners.Clear();
            }

            JsonElement? phaseJson = await GetJsonAsync("/lol-gameflow/v1/gameflow-phase", cancellationToken).ConfigureAwait(false);
            string phaseName = phaseJson is { ValueKind: JsonValueKind.String } ? phaseJson.Value.GetString() ?? "" : "";
            GamePhase phase = LcuSnapshotParser.ParsePhase(phaseName);
            bool active = phase is GamePhase.ChampionSelect or GamePhase.Loading or GamePhase.InGame or GamePhase.Finished;
            if (!active)
            {
                _activeToken = null;
                _activeGameId = null;
                return Snapshot(ClientConnectionStatus.Connected, "客户端已连接，等待当前对局。", account, null, phase, connection.ClientDirectory);
            }

            JsonElement? flow = await GetJsonAsync("/lol-gameflow/v1/session", cancellationToken).ConfigureAwait(false);
            JsonElement? selection = phase == GamePhase.ChampionSelect
                ? await GetJsonAsync("/lol-champ-select/v1/session", cancellationToken).ConfigureAwait(false) : null;
            string gameId = LcuSnapshotParser.GameId(flow, selection);
            if (_activeToken is null || (_activeGameId is not null && gameId.Length > 0 && gameId != _activeGameId))
            {
                _activeToken = gameId.Length > 0 ? "game-" + gameId : Guid.NewGuid().ToString("N");
                _summoners.Clear();
            }
            if (gameId.Length > 0) _activeGameId = gameId;

            foreach (long summonerId in LcuSnapshotParser.VisibleSummonerIds(flow, selection).Distinct().Take(20))
            {
                if (_summoners.ContainsKey(summonerId)) continue;
                JsonElement? summoner = await GetJsonAsync("/lol-summoner/v1/summoners/" + summonerId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                if (summoner is not null) _summoners[summonerId] = summoner.Value;
            }
            string localId = "live-" + account.ProfileKey + "-" + _activeToken;
            IReadOnlyDictionary<int, string> championNames = await ReadChampionNamesAsync(cancellationToken).ConfigureAwait(false);
            MatchSession? session = LcuSnapshotParser.ParseSession(account, phase, flow, selection, _summoners,
                localId, _providerId, _generation, ++_sequence, DateTimeOffset.UtcNow, championNames);
            if (session is not null && phase == GamePhase.Finished)
            {
                session = await EnrichFinishedRosterAsync(session, account, championNames, cancellationToken).ConfigureAwait(false);
            }
            return Snapshot(ClientConnectionStatus.Connected,
                session is { Participants.Count: > 0 } ? "正在实时检测当前对局。" : "已检测到对局阶段，等待名单。",
                account, session, phase, connection.ClientDirectory);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (LcuAuthenticationException)
        {
            ResetConnection();
            return Snapshot(ClientConnectionStatus.CredentialsUnavailable, "客户端连接参数已变化，正在重新发现。", null, null, GamePhase.Disconnected, connection?.ClientDirectory);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            ResetConnection();
            return Snapshot(ClientConnectionStatus.Disconnected, "客户端连接已断开，正在等待重连。", null, null, GamePhase.Disconnected, connection?.ClientDirectory);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            return Snapshot(ClientConnectionStatus.Error, "客户端返回的数据格式暂不受支持，将继续尝试更新。", null, null, GamePhase.Unknown, connection?.ClientDirectory);
        }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// Fetches the official champion and item lists (localized) for tag search.
    /// Read-only and best-effort: any client/network problem returns null so the
    /// caller can fall back to its cached or bundled catalog.
    /// </summary>
    public async Task<TagSeedCatalog?> ReadTagSeedsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_http is null)
            {
                LcuDiscoveryResult discovery = await _discovery.DiscoverAsync(null, cancellationToken)
                    .ConfigureAwait(false);
                if (discovery.Connection is null)
                {
                    return null;
                }

                EnsureConnection(discovery.Connection);
            }

            JsonElement? champions = await GetJsonAsync(
                    "/lol-game-data/assets/v1/champion-summary.json",
                    cancellationToken)
                .ConfigureAwait(false);
            if (champions is null)
            {
                return null;
            }

            JsonElement? items = await GetJsonAsync("/lol-game-data/assets/v1/items.json", cancellationToken)
                .ConfigureAwait(false);
            if (items is null)
            {
                return null;
            }

            TagSeedCatalog catalog = TagSeedCatalog.ParseLcu(champions.Value, items.Value);
            return catalog.IsEmpty ? null : catalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       or LcuAuthenticationException or OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool HasCurrentAccount(JsonElement current) =>
        !PlayerIdentity.IsPlaceholderValue(LcuSnapshotParser.Text(current, "puuid")) || LcuSnapshotParser.Number(current, "summonerId") > 0;

    private static ClientSnapshot Snapshot(ClientConnectionStatus status, string message, ClientAccount? account, MatchSession? session, GamePhase phase, string? directory) =>
        new(status, message, account, session, phase, DateTimeOffset.UtcNow, directory);

    internal void EnsureConnection(LcuConnection connection)
    {
        if (_fingerprint == connection.Fingerprint && _http is not null) return;
        ResetConnection();
        _fingerprint = connection.Fingerprint;
        var handler = _handlerFactory?.Invoke() ?? CreateHandler(connection.Port);
        _http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri("https://127.0.0.1:" + connection.Port.ToString(CultureInfo.InvariantCulture)),
            Timeout = TimeSpan.FromSeconds(5)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + connection.Password)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static HttpMessageHandler CreateHandler(int port) => new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = (request, certificate, _, _) =>
            request.RequestUri is { Scheme: "https" } uri && uri.Port == port
            && IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address)
            && certificate is not null && ValidateRiotCertificate(certificate)
    };

    /// <summary>Validates solely against Riot's bundled CA from https://static.developer.riotgames.com/docs/lol/riotgames.pem.</summary>
    public static bool ValidateRiotCertificate(X509Certificate2 certificate)
    {
        using Stream stream = typeof(LcuClientService).Assembly.GetManifestResourceStream("LoLKarmaBook.Client.riotgames.pem")
            ?? throw new InvalidOperationException("Bundled client certificate authority is missing.");
        using var reader = new StreamReader(stream);
        using X509Certificate2 authority = X509Certificate2.CreateFromPem(reader.ReadToEnd());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
        return chain.Build(certificate);
    }

    internal async Task<JsonElement?> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken requestToken = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using HttpResponseMessage response = await _http!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new LcuAuthenticationException();
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent or HttpStatusCode.ServiceUnavailable) return null;
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("Local client request failed.");
        await using Stream body = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16384];
        const int limit = 5 * 1024 * 1024;
        while (true)
        {
            int count = (int)Math.Min(buffer.Length, limit - bytes.Length + 1);
            int read = await body.ReadAsync(buffer.AsMemory(0, count), requestToken).ConfigureAwait(false);
            if (read == 0) break;
            bytes.Write(buffer, 0, read);
            if (bytes.Length > limit) throw new InvalidDataException("Local client response exceeds the read limit.");
        }
        if (bytes.Length == 0) return null;
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Localized champion names by numeric id. Cosmetic only: a client that does
    /// not serve the asset list still gets the "#id" fallback, and a failed read
    /// is retried a few times rather than on every two-second poll.
    /// </summary>
    private async Task<IReadOnlyDictionary<int, string>> ReadChampionNamesAsync(CancellationToken cancellationToken)
    {
        if (_championNames.Count > 0 || _championNameAttempts >= 3) return _championNames;
        _championNameAttempts++;
        try
        {
            JsonElement? summary = await GetJsonAsync("/lol-game-data/assets/v1/champion-summary.json", cancellationToken)
                .ConfigureAwait(false);
            if (summary is { ValueKind: JsonValueKind.Array } champions)
            {
                foreach (JsonElement champion in champions.EnumerateArray())
                {
                    long id = LcuSnapshotParser.Number(champion, "id");
                    string name = LcuSnapshotParser.Text(champion, "name");
                    if (name.Length == 0 || id <= 0 || id > int.MaxValue) continue;
                    _championNames[(int)id] = name;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException
                                       or LcuAuthenticationException)
        {
        }

        return _championNames;
    }

    /// <summary>
    /// The client hides some names while the match runs and only reveals them in the
    /// match history afterwards, so a finished roster is completed from that record.
    /// </summary>
    private async Task<MatchSession> EnrichFinishedRosterAsync(MatchSession session, ClientAccount account,
        IReadOnlyDictionary<int, string> championNames, CancellationToken cancellationToken)
    {
        if (!session.Participants.Any(participant => participant.Visibility != RosterVisibility.Visible
                                                     || string.IsNullOrWhiteSpace(participant.CurrentDisplayId)))
        {
            return session;
        }

        long gameId = GameIdFrom(session.ExternalMatchId);
        if (gameId <= 0)
        {
            return session;
        }

        JsonElement? detail;
        try
        {
            detail = await GetJsonAsync("/lol-match-history/v1/games/" + gameId.ToString(CultureInfo.InvariantCulture),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or LcuAuthenticationException)
        {
            return session;
        }

        if (detail is not { ValueKind: JsonValueKind.Object })
        {
            return session;
        }

        List<HistoryRow> rows = ReadHistoryRows(detail.Value);
        if (rows.Count == 0 || rows.Count != session.Participants.Count)
        {
            return session;
        }

        HistoryRow? selfRow = rows.FirstOrDefault(row =>
            !PlayerIdentity.IsPlaceholderValue(account.Puuid) && row.Puuid == account.Puuid);
        if (selfRow is null || selfRow.TeamId is not (100 or 200))
        {
            return session;
        }

        var bySide = rows
            .GroupBy(row => row.TeamId == selfRow.TeamId ? ParticipantSide.Ally : ParticipantSide.Enemy)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Index).ToList());
        var used = new HashSet<int>();
        var participants = new List<Participant>(session.Participants.Count);
        foreach (Participant participant in session.Participants)
        {
            if (participant.Visibility == RosterVisibility.Visible
                && !string.IsNullOrWhiteSpace(participant.CurrentDisplayId))
            {
                participants.Add(participant);
                continue;
            }

            if (!bySide.TryGetValue(participant.Side, out List<HistoryRow>? candidates))
            {
                participants.Add(participant);
                continue;
            }

            HistoryRow? row = candidates.FirstOrDefault(candidate => !used.Contains(candidate.Index));
            if (row is null)
            {
                participants.Add(participant);
                continue;
            }

            used.Add(row.Index);
            participants.Add(BuildFromHistory(participant, row, championNames));
        }

        if (participants.SequenceEqual(session.Participants))
        {
            return session;
        }

        return new MatchSession(session.LocalSessionId, session.SourceKind, session.ProviderInstanceId,
            session.Generation, session.Sequence, session.ObservedAt, session.Phase, session.QueueId, session.MapId,
            session.ExternalMatchId, session.RosterComplete, participants);
    }

    private static Participant BuildFromHistory(Participant original, HistoryRow row,
        IReadOnlyDictionary<int, string> championNames)
    {
        string? champion = original.Champion;
        if (champion is null && row.ChampionId > 0)
        {
            champion = championNames.TryGetValue(row.ChampionId, out string? name)
                ? name
                : "#" + row.ChampionId.ToString(CultureInfo.InvariantCulture);
        }

        PlayerIdentity? identity = null;
        if (!PlayerIdentity.IsPlaceholderValue(row.Puuid))
        {
            PlayerIdentity.TryCreate("riot", "lcu-puuid", "global", "puuid", row.Puuid, out identity, out _);
        }

        string key = "eog-" + ShortHash(identity?.Value ?? row.Name + "|" + row.Index.ToString(CultureInfo.InvariantCulture));
        return identity is null
            ? Participant.VisibleNameOnly(key, original.Side, row.Name, champion, original.IsSelf)
            : Participant.Visible(key, original.Side, row.Name, identity, champion, original.IsSelf);
    }

    private static List<HistoryRow> ReadHistoryRows(JsonElement game)
    {
        JsonElement? identities = LcuSnapshotParser.Child(game, "participantIdentities");
        JsonElement? participants = LcuSnapshotParser.Child(game, "participants");
        if (identities is not { ValueKind: JsonValueKind.Array } identityRows
            || participants is not { ValueKind: JsonValueKind.Array } participantRows)
        {
            return new List<HistoryRow>();
        }

        var players = new Dictionary<long, (string Puuid, string Name)>();
        foreach (JsonElement identity in identityRows.EnumerateArray())
        {
            long id = LcuSnapshotParser.Number(identity, "participantId", -1);
            JsonElement? player = LcuSnapshotParser.Child(identity, "player");
            if (id < 0 || player is null)
            {
                continue;
            }

            string name = LcuSnapshotParser.Text(player, "gameName", "summonerName", "displayName");
            string tag = LcuSnapshotParser.Text(player, "tagLine");
            if (name.Length > 0 && tag.Length > 0 && !name.Contains('#'))
            {
                name += "#" + tag;
            }

            players[id] = (LcuSnapshotParser.Text(player, "puuid"), name);
        }

        var rows = new List<HistoryRow>(10);
        foreach (JsonElement participant in participantRows.EnumerateArray())
        {
            long id = LcuSnapshotParser.Number(participant, "participantId", -1);
            if (!players.TryGetValue(id, out (string Puuid, string Name) player) || player.Name.Length == 0)
            {
                continue;
            }

            rows.Add(new HistoryRow((int)id, LcuSnapshotParser.Number(participant, "teamId"),
                (int)Math.Clamp(LcuSnapshotParser.Number(participant, "championId"), 0, int.MaxValue),
                player.Puuid, player.Name));
        }

        return rows;
    }

    private static long GameIdFrom(string? externalMatchId)
    {
        if (string.IsNullOrWhiteSpace(externalMatchId))
        {
            return 0;
        }

        int cut = externalMatchId.LastIndexOf(':');
        return cut > 0 && long.TryParse(externalMatchId[(cut + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long gameId)
            ? gameId
            : 0;
    }

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private sealed record HistoryRow(int Index, long TeamId, int ChampionId, string Puuid, string Name);
    private void ResetConnection()
    {
        _http?.Dispose();
        _http = null;
        _fingerprint = null;
        _platformId = "";
        _championNameAttempts = 0;
        ResetAccount();
    }

    private void ResetAccount()
    {
        if (_accountKey is not null) _generation++;
        _accountKey = null;
        _activeToken = null;
        _activeGameId = null;
        _summoners.Clear();
        _sequence = 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetConnection();
    }

    internal sealed class LcuAuthenticationException : Exception;
}
