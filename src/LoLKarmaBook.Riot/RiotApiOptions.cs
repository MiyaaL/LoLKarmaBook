namespace LoLKarmaBook.Riot;

/// <summary>
/// Everything a lookup needs except the key itself. The key is passed to each
/// call separately so callers decide how long it lives; it is never stored here.
/// </summary>
public sealed record RiotApiOptions
{
    /// <summary>Platform ID the account's matches are played on, e.g. <c>EUW1</c>.</summary>
    public string PlatformId { get; init; } = string.Empty;

    /// <summary>How many recent matches to pull, newest first. 1–100.</summary>
    public int MatchCount { get; init; } = 20;

    /// <summary>Queue filter, or null for every queue. 420 is solo/duo, 440 flex.</summary>
    public int? QueueId { get; init; } = 420;

    /// <summary>Per-request network timeout.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Delay between match-detail calls, to stay under development-key limits.</summary>
    public TimeSpan RequestSpacing { get; init; } = TimeSpan.FromMilliseconds(600);

    /// <summary>Upper bound on how long one full lookup may take.</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>Riot's rate-limit headers are honoured, but never beyond this wait.</summary>
    public TimeSpan MaxRateLimitWait { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The regional cluster derived from <see cref="PlatformId"/>, or null.</summary>
    public string? Region => RiotPlatforms.RegionFor(PlatformId);
}
