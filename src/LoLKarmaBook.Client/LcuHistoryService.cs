using System.Globalization;
using System.Text.Json;
using LoLKarmaBook.Core;
using LoLKarmaBook.Scoring.Performance;

namespace LoLKarmaBook.Client;

public sealed class LcuHistoryService : ILcuHistoryService
{
    private readonly ILcuConnectionDiscovery _discovery;
    private readonly LcuClientService _transport;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public LcuHistoryService(ILcuConnectionDiscovery? discovery = null, Func<HttpMessageHandler>? handlerFactory = null)
    {
        _discovery = discovery ?? new LcuConnectionDiscovery();
        _transport = new LcuClientService(_discovery, handlerFactory);
    }

    public async Task<LcuHistoryResult> GetHistoryAsync(string? clientDirectory, int count = 20, int? queueId = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count));
        if (queueId is <= 0) throw new ArgumentOutOfRangeException(nameof(queueId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        int scanned = 0, skipped = 0;
        try
        {
            var discovery = await _discovery.DiscoverAsync(clientDirectory, cancellationToken).ConfigureAwait(false);
            if (discovery.Connection is null)
                return Failure(discovery.Status == ClientConnectionStatus.NotRunning ? LcuHistoryStatus.NotRunning : LcuHistoryStatus.CredentialsUnavailable, discovery.Message);
            _transport.EnsureConnection(discovery.Connection);
            JsonElement? current = await Read("/lol-summoner/v1/current-summoner", cancellationToken).ConfigureAwait(false);
            if (current is null || PlayerIdentity.IsPlaceholderValue(LcuSnapshotParser.Text(current, "puuid")))
                return Failure(LcuHistoryStatus.NotLoggedIn, "请先登录 LOL 客户端。" );
            JsonElement? login = await Read("/lol-login/v1/session", cancellationToken).ConfigureAwait(false);
            string platform = LcuSnapshotParser.Text(login, "platformId");
            if (platform.Length == 0)
            {
                var region = await Read("/riotclient/region-locale", cancellationToken).ConfigureAwait(false);
                platform = LcuSnapshotParser.Text(region, "platformId", "region");
            }
            var account = LcuSnapshotParser.ParseAccount(current.Value, platform.Length == 0 ? "unknown" : platform);
            string owner = LcuHistoryParser.PlayerKey(account.Puuid, account.SummonerId, account.PlatformId);
            var matches = new List<LcuHistoryMatch>();
            var seen = new HashSet<long>();
            const int pageSize = 20, maximumScanned = 200;
            int begin = 0;
            while (matches.Count < count && begin < maximumScanned)
            {
                if (!await SameAccount(account, cancellationToken).ConfigureAwait(false)) return Failure(LcuHistoryStatus.AccountChanged, "账号已切换，请重新查询。" );
                string path = "/lol-match-history/v1/products/lol/" + Uri.EscapeDataString(account.Puuid) + "/matches?begIndex="
                    + begin.ToString(CultureInfo.InvariantCulture) + "&endIndex=" + (begin + pageSize - 1).ToString(CultureInfo.InvariantCulture);
                JsonElement? page = await Read(path, cancellationToken).ConfigureAwait(false);
                if (page is null) return Failure(LcuHistoryStatus.Unavailable, "客户端战绩暂不可用，请稍后重试。" );
                JsonElement? container = LcuSnapshotParser.Child(page, "games");
                JsonElement? games = container is { ValueKind: JsonValueKind.Array } ? container : LcuSnapshotParser.Child(container, "games");
                if (games is not { ValueKind: JsonValueKind.Array }) return Failure(LcuHistoryStatus.Error, "客户端战绩格式暂不受支持。" );
                int pageCount = games.Value.GetArrayLength(), newGames = 0;
                if (pageCount == 0) break;
                foreach (JsonElement summary in games.Value.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long gameId = LcuSnapshotParser.Number(summary, "gameId");
                    if (gameId <= 0 || !seen.Add(gameId)) continue;
                    newGames++; scanned++;
                    long summaryQueue = LcuSnapshotParser.Number(summary, "queueId");
                    if (queueId is { } selected && summaryQueue > 0 && summaryQueue != selected) { skipped++; continue; }
                    JsonElement detail;
                    if (LcuHistoryParser.TryParse(summary, account, out var parsed)) detail = summary;
                    else
                    {
                        var fetched = await Read("/lol-match-history/v1/games/" + gameId.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                        if (fetched is null) { skipped++; continue; }
                        detail = fetched.Value;
                    }
                    if (!LcuHistoryParser.TryParse(detail, account, out parsed) || parsed!.GameId != gameId
                        || parsed.OwnerId != owner || queueId is { } filter && parsed.QueueId != filter)
                    { skipped++; continue; }
                    matches.Add(parsed);
                    progress?.Report(matches.Count);
                    if (matches.Count == count) break;
                }
                if (newGames == 0) break;
                // Advance by the returned count, tolerating inclusive or exclusive endIndex implementations.
                begin += pageCount;
                if (pageCount < pageSize) break;
            }
            if (!await SameAccount(account, cancellationToken).ConfigureAwait(false)) return Failure(LcuHistoryStatus.AccountChanged, "账号已切换，请重新查询。" );
            var dataset = new PerformanceDataset
            {
                Source = "lcu-history:" + account.PlatformId + ":" + (queueId?.ToString(CultureInfo.InvariantCulture) ?? "all"),
                PlayerId = owner, Matches = matches.Select(match => match.Stats).ToArray(),
                Roster = new PerformanceRoster(owner, matches.Select(match => match.Roster).ToArray())
            };
            return new(LcuHistoryStatus.Success, matches.Count == 0 ? "没有找到符合条件的完整对局。" : "已获取客户端本人战绩。", account, dataset, scanned, skipped);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (LcuClientService.LcuAuthenticationException) { return Failure(LcuHistoryStatus.CredentialsUnavailable, "客户端连接已变化，请重新查询。" ); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { return Failure(LcuHistoryStatus.Unavailable, "客户端战绩连接暂不可用，请稍后重试。" ); }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or OverflowException)
        { return Failure(LcuHistoryStatus.Error, "客户端战绩格式暂不受支持。" ); }
        finally { _gate.Release(); }

        LcuHistoryResult Failure(LcuHistoryStatus status, string message) => new(status, message, null, null, scanned, skipped);
    }

    private Task<JsonElement?> Read(string path, CancellationToken token) => _transport.GetJsonAsync(path, token);
    private async Task<bool> SameAccount(ClientAccount account, CancellationToken token)
    {
        var current = await Read("/lol-summoner/v1/current-summoner", token).ConfigureAwait(false);
        return current is not null && LcuSnapshotParser.Text(current, "puuid") == account.Puuid;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.Dispose();
    }
}
