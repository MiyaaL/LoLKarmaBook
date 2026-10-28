namespace LoLKarmaBook.Riot;

/// <summary>Why a Riot API call did not produce data. Every case maps to a user-visible message.</summary>
public enum RiotApiFailure
{
    /// <summary>The key was rejected (HTTP 401/403): expired, malformed, or not for this product.</summary>
    KeyRejected,

    /// <summary>No such Riot ID on that platform.</summary>
    PlayerNotFound,

    /// <summary>The key's rate limit was hit (HTTP 429).</summary>
    RateLimited,

    /// <summary>Riot answered with an unexpected status.</summary>
    UnexpectedStatus,

    /// <summary>The network call failed or timed out.</summary>
    Network,

    /// <summary>The response could not be read as the expected shape.</summary>
    MalformedResponse
}

/// <summary>A Riot API call that failed. Messages never contain the key.</summary>
public sealed class RiotApiException : Exception
{
    public RiotApiException(RiotApiFailure failure, string message, int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    public RiotApiFailure Failure { get; }

    public int? StatusCode { get; }

    /// <summary>The user-facing hint for this failure.</summary>
    public string UserHint => Failure switch
    {
        RiotApiFailure.KeyRejected => "API Key 无效、已过期或不属于本产品，请在开发者门户确认后重新填写。",
        RiotApiFailure.PlayerNotFound => "该平台上找不到这个 Riot ID，请检查区服和大区以及 # 后的标签。",
        RiotApiFailure.RateLimited => "已达到 Riot API 的调用频率上限，请稍后再试（开发密钥通常每分钟只有少量请求）。",
        RiotApiFailure.UnexpectedStatus => "Riot API 返回了未预期的状态码" + (StatusCode is { } s ? $" {s}" : "") + "。",
        RiotApiFailure.Network => "无法连接 Riot API，请检查网络或代理设置。",
        _ => "Riot API 返回的内容无法解析。"
    };

    /// <summary>True when the same request is worth retrying later unchanged.</summary>
    public bool IsRetryable => Failure is RiotApiFailure.RateLimited or RiotApiFailure.Network or RiotApiFailure.UnexpectedStatus;
}
