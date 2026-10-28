using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace LoLKarmaBook.Riot;

/// <summary>
/// One read-only lookup: Riot ID → PUUID → recent match IDs → match details.
/// Nothing is written anywhere; the caller decides what to keep. The key is
/// held by the caller and passed per call, never stored by this type.
/// </summary>
public sealed class RiotApiClient
{
    private const string ApiKeyHeader = "X-Riot-Token";

    private readonly HttpClient _http;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTimeOffset> _clock;

    public RiotApiClient(
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? requestTimeout = null)
    {
        // HttpClient.Timeout covers the whole BodyRead, so the per-request
        // options.RequestTimeout only takes effect when this ceiling is looser.
        // Keeping the default at the typical value means neither silently wins.
        _http = handler is null
            // X-Riot-Token is a custom header: automatic redirects would retain
            // it even when Location points at another HTTPS host.
            ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = requestTimeout ?? TimeSpan.FromSeconds(20) }
            : new HttpClient(handler, disposeHandler: false);
        _delay = delay ?? ((span, token) => Task.Delay(span, token));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Resolves <paramref name="riotId"/> to an account on the options' platform.</summary>
    public async Task<RiotAccount> GetAccountAsync(
        string apiKey, RiotId riotId, RiotApiOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(riotId);
        ArgumentNullException.ThrowIfNull(options);
        string region = options.Region
            ?? throw new RiotApiException(RiotApiFailure.PlayerNotFound,
                "该区服不受 Riot 官方 API 支持（例如国服），无法查询。");

        // Riot ID lookup is account-v1, which lives on the regional cluster.
        string path = $"/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(riotId.GameName)}/"
                      + Uri.EscapeDataString(riotId.TagLine);
        using JsonDocument doc = await SendAsync(apiKey, region, path, options, cancellationToken).ConfigureAwait(false);
        JsonElement root = doc.RootElement;

        string puuid = Text(root, "puuid");
        if (puuid.Length == 0)
        {
            throw new RiotApiException(RiotApiFailure.MalformedResponse, "account-v1 响应缺少 puuid。");
        }

        return new RiotAccount(
            puuid,
            Text(root, "gameName") is { Length: > 0 } name ? name : riotId.GameName,
            Text(root, "tagLine") is { Length: > 0 } tag ? tag : riotId.TagLine,
            options.PlatformId);
    }

    /// <summary>Recent match IDs, newest first, filtered by the options' queue and type.</summary>
    public async Task<IReadOnlyList<string>> GetMatchIdsAsync(
        string apiKey, RiotAccount account, RiotApiOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(options);
        string region = options.Region
            ?? throw new RiotApiException(RiotApiFailure.PlayerNotFound, "未选择有效的区服。");

        int count = Math.Clamp(options.MatchCount, 1, 100);
        string path = $"/lol/match/v5/matches/by-puuid/{Uri.EscapeDataString(account.Puuid)}/ids"
                      + $"?start=0&count={count.ToString(CultureInfo.InvariantCulture)}";
        if (options.QueueId is { } queue)
        {
            path += "&queue=" + queue.ToString(CultureInfo.InvariantCulture);
        }

        using JsonDocument doc = await SendAsync(apiKey, region, path, options, cancellationToken).ConfigureAwait(false);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new RiotApiException(RiotApiFailure.MalformedResponse, "match-v5 ids 响应不是数组。");
        }

        var ids = new List<string>(count);
        foreach (JsonElement id in doc.RootElement.EnumerateArray())
        {
            if (id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } value)
            {
                ids.Add(value);
            }
        }

        return ids;
    }

    /// <summary>One match's full detail document.</summary>
    public async Task<JsonDocument> GetMatchAsync(
        string apiKey, string matchId, RiotApiOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(matchId);
        ArgumentNullException.ThrowIfNull(options);
        string region = options.Region
            ?? throw new RiotApiException(RiotApiFailure.PlayerNotFound, "未选择有效的区服。");

        // Match IDs are trusted only after this check: they become part of a URL.
        if (!IsMatchId(matchId))
        {
            throw new RiotApiException(RiotApiFailure.MalformedResponse, "Riot 返回了无法识别的对局编号。");
        }

        return await SendAsync(apiKey, region,
            "/lol/match/v5/matches/" + Uri.EscapeDataString(matchId), options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Pulls the account's recent matches and turns each one into the two halves
    /// of an estimate: the roster row (owner's four teammates and five opponents)
    /// plus the owner's stat row, including ranked and ARAM matches.
    /// </summary>
    public async Task<IReadOnlyList<RiotCompletedMatch>> GetCompletedMatchesAsync(
        string apiKey, RiotAccount account, RiotApiOptions options,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> ids = await GetMatchIdsAsync(apiKey, account, options, cancellationToken).ConfigureAwait(false);
        DateTimeOffset deadline = _clock() + options.TotalTimeout;
        var results = new List<RiotCompletedMatch>(ids.Count);

        for (var i = 0; i < ids.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (i > 0 && options.RequestSpacing > TimeSpan.Zero)
            {
                await _delay(options.RequestSpacing, cancellationToken).ConfigureAwait(false);
            }

            // The deadline only stops new work; a detail already in flight is
            // allowed to finish so the partial result stays usable.
            if (_clock() > deadline)
            {
                break;
            }

            using JsonDocument doc = await GetMatchAsync(apiKey, ids[i], options, cancellationToken).ConfigureAwait(false);
            if (RiotMatchParser.TryParse(doc.RootElement, account.Puuid, out RiotCompletedMatch? match, out _))
            {
                results.Add(match!);
                progress?.Report(results.Count);
            }
        }

        return results;
    }

    private async Task<JsonDocument> SendAsync(
        string apiKey, string region, string path, RiotApiOptions options, CancellationToken cancellationToken, int rateLimitRetries = 0)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new RiotApiException(RiotApiFailure.KeyRejected, "API Key 为空。");
        }

        Uri uri = new("https://" + region + ".api.riotgames.com" + path);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation(ApiKeyHeader, apiKey.Trim());
        request.Headers.Accept.ParseAdd("application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RiotApiException(RiotApiFailure.Network, "请求 Riot API 超时。", null, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RiotApiException(RiotApiFailure.Network, "无法连接 Riot API。", null, ex);
        }

        using (response)
        {
            int status = (int)response.StatusCode;

            if (status is 429)
            {
                // Honour Riot's own back-off hint when it is short enough, once.
                TimeSpan wait = RetryAfter(response) is { } after && after <= options.MaxRateLimitWait
                    ? after
                    : TimeSpan.Zero;
                if (wait > TimeSpan.Zero && rateLimitRetries == 0)
                {
                    await _delay(wait, cancellationToken).ConfigureAwait(false);
                    return await SendAsync(apiKey, region, path, options, cancellationToken, rateLimitRetries + 1).ConfigureAwait(false);
                }

                throw new RiotApiException(RiotApiFailure.RateLimited, "触发 Riot API 频率限制。", status);
            }

            if (status is 401 or 403)
            {
                throw new RiotApiException(RiotApiFailure.KeyRejected, "Riot API 拒绝了该 Key。", status);
            }

            if (status is 404)
            {
                throw new RiotApiException(RiotApiFailure.PlayerNotFound, "Riot API 找不到对应资源。", status);
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new RiotApiException(RiotApiFailure.UnexpectedStatus, "Riot API 返回错误。", status);
            }

            try
            {
                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new RiotApiException(RiotApiFailure.MalformedResponse, "Riot API 响应不是有效 JSON。", status, ex);
            }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter?.Date is { } date) return date - DateTimeOffset.UtcNow;
        if (response.Headers.TryGetValues("X-Rate-Limit-Reset", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return TimeSpan.FromSeconds(Math.Max(0, seconds));
        }

        return null;
    }

    /// <summary>Match IDs look like <c>EUW1_1234567890</c>: a platform prefix, underscore, digits.</summary>
    public static bool IsMatchId(string matchId)
    {
        int underscore = matchId.IndexOf('_');
        string digits = matchId[(underscore + 1)..];
        return underscore is > 0 and < 8
               && matchId.Length is > 3 and < 64
               && digits.Length > 0
               && digits.All(char.IsAsciiDigit)
               && matchId[..underscore].All(c => char.IsAsciiLetterOrDigit(c));
    }

    private static string Text(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out JsonElement value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

/// <summary>A resolved Riot account. No key material is retained.</summary>
public sealed record RiotAccount(string Puuid, string GameName, string TagLine, string PlatformId)
{
    /// <summary>True when no game name came back, so only the PUUID identifies it.</summary>
    public bool HasDisplayName => GameName.Length > 0;

    public string DisplayLabel => GameName.Length > 0 ? GameName + "#" + TagLine : Puuid[..Math.Min(12, Puuid.Length)] + "…";

    public override string ToString() => "Riot account " + DisplayLabel;
}
