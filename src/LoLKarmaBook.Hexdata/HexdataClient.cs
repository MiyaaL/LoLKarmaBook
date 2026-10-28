using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LoLKarmaBook.Hexdata;

/// <summary>
/// The three Hexdata calls this app needs: an email code, the session that code
/// unlocks, and the public match report. Only the app's own account is involved;
/// the session token is sent as a bearer header exactly as the website does.
/// </summary>
public sealed class HexdataClient : IDisposable
{
    /// <summary>The API host. The site is served from hexdata.com.cn, its API from box.</summary>
    public const string BaseAddress = "https://box.hexdata.com.cn/";

    private readonly HttpClient _http;
    private readonly bool _ownsHandler;

    public HexdataClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _ownsHandler = handler is null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: _ownsHandler)
        {
            BaseAddress = new Uri(BaseAddress),
            Timeout = timeout ?? TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LoLKarmaBook/0.1 (+desktop)");
    }

    /// <summary>Sends a sign-in code to the address; new addresses register automatically.</summary>
    public async Task RequestCodeAsync(string email, CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await PostAsync("/v1/auth/request-code",
            new { email = Normalize(email) }, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Exchanges the emailed code for a session token.</summary>
    public async Task<string> VerifyCodeAsync(string email, string code, CancellationToken cancellationToken = default)
    {
        using JsonDocument response = await PostAsync("/v1/auth/verify-code",
            new { email = Normalize(email), code = code.Trim() }, null, cancellationToken).ConfigureAwait(false);
        string? token = ReadString(response.RootElement, "sessionToken");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new HexdataException("登录响应里没有会话令牌。", 200, "invalid_response");
        }

        return token;
    }

    /// <summary>Returns the signed-in account and membership, or null for a rejected token.</summary>
    public async Task<HexdataAccount?> GetAccountAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            using JsonDocument response = await SendAsync(HttpMethod.Get, "/v1/me", null, token, cancellationToken)
                .ConfigureAwait(false);
            return ParseAccount(response.RootElement);
        }
        catch (HexdataException ex) when (ex.IsUnauthorized)
        {
            return null;
        }
    }

    private static HexdataAccount? ParseAccount(JsonElement root)
    {
        if (root.TryGetProperty("authenticated", out JsonElement authenticated)
            && authenticated.ValueKind == JsonValueKind.False)
        {
            return null;
        }

        string? email = null;
        if (root.TryGetProperty("account", out JsonElement account) && account.ValueKind == JsonValueKind.Object)
        {
            email = ReadString(account, "email");
        }

        string? plan = null;
        bool active = false;
        if (root.TryGetProperty("membership", out JsonElement membership) && membership.ValueKind == JsonValueKind.Object)
        {
            plan = ReadString(membership, "plan");
            active = membership.TryGetProperty("isActive", out JsonElement isActive)
                     && isActive.ValueKind == JsonValueKind.True;
        }

        return new HexdataAccount(email, plan, active);
    }

    /// <summary>
    /// Queries the public match report. With <paramref name="useBoundIdentity"/> the
    /// server looks up the game account already bound to the session; otherwise the
    /// supplied game id ("name#tag") is used.
    /// </summary>
    public async Task<HexdataReport> QueryReportAsync(string token, string? gameId, bool useBoundIdentity,
        CancellationToken cancellationToken = default)
    {
        object payload = useBoundIdentity ? new { useBoundIdentity = true } : new { gameId = (gameId ?? string.Empty).Trim() };
        using JsonDocument response = await PostAsync("/v1/public/match-report", payload, token, cancellationToken)
            .ConfigureAwait(false);
        return HexdataReport.Parse(response.RootElement.GetRawText());
    }

    private async Task<JsonDocument> PostAsync(string path, object payload, string? token,
        CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        return await SendAsync(HttpMethod.Post, path, content, token, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, HttpContent? content, string? token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HexdataException("连接 Hexdata 超时，请检查网络后重试。", 0, "timeout", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HexdataException("无法连接 Hexdata：" + ex.Message, 0, "network_error", ex);
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                (string? code, string? message) = ReadError(body);
                string text = message ?? response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "登录已失效，请重新登录 Hexdata。",
                    HttpStatusCode.TooManyRequests => "查询次数已用完，请稍后再试。",
                    _ => "Hexdata 请求失败（" + (int)response.StatusCode + "）。"
                };
                throw new HexdataException(text, (int)response.StatusCode, code);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return JsonDocument.Parse("{}");
            }

            try
            {
                return JsonDocument.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new HexdataException("Hexdata 返回了无法解析的数据。", (int)response.StatusCode, "invalid_response", ex);
            }
        }
    }

    private static (string? Code, string? Message) ReadError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            string? code = null;
            string? message = null;
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object)
            {
                code = ReadString(error, "code");
                message = ReadString(error, "message");
            }

            code ??= ReadString(root, "code");
            message ??= ReadString(root, "message");
            return (code, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string Normalize(string email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public void Dispose() => _http.Dispose();
}

public sealed record HexdataAccount(string? Email, string? Plan, bool IsActive);
