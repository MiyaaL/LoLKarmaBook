using System.Text.RegularExpressions;

namespace LoLKarmaBook.Riot;

/// <summary>
/// Platform (summoner) IDs and the regional cluster that serves account and
/// match data for them. The desktop app speaks the CN client's platform IDs
/// (HN1, TJ1, …), which Riot's public API does not serve; both the official
/// and the CN values are listed so a mismatched choice is reported clearly
/// instead of silently returning nothing.
/// </summary>
public static class RiotPlatforms
{
    public const string RegionAmericas = "americas";
    public const string RegionAsia = "asia";
    public const string RegionEurope = "europe";
    public const string RegionSea = "sea";

    /// <summary>Region used for match-v5, league-v4 and match timelines.</summary>
    public static string? RegionFor(string? platformId)
    {
        if (string.IsNullOrWhiteSpace(platformId)) return null;
        return Normalize(platformId) switch
        {
            "NA1" or "BR1" or "LA1" or "LA2" or "OC1" => RegionAmericas,
            "KR" or "JP1" or "TW2" or "VN2" => RegionAsia,
            "EUW1" or "EUN1" or "TR1" or "RU" or "ME1" => RegionEurope,
            "SG2" or "PH2" or "TH2" => RegionSea,
            _ => null
        };
    }

    /// <summary>Human labels for the console picker, in the order they are shown.</summary>
    public static IReadOnlyList<(string Platform, string Label)> Catalog { get; } =
    [
        ("NA1", "北美 NA1"),
        ("EUW1", "欧西 EUW1"),
        ("EUN1", "欧北 EUN1"),
        ("KR", "韩服 KR"),
        ("JP1", "日服 JP1"),
        ("BR1", "巴西 BR1"),
        ("LA1", "拉美北 LA1"),
        ("LA2", "拉美南 LA2"),
        ("OC1", "大洋洲 OC1"),
        ("TR1", "土耳其 TR1"),
        ("RU", "俄服 RU"),
        ("SG2", "新加坡 SG2"),
        ("PH2", "菲律宾 PH2"),
        ("TH2", "泰国 TH2"),
        ("TW2", "台服 TW2"),
        ("VN2", "越南 VN2")
    ];

    /// <summary>Riot's platform IDs are upper-case ASCII letters and digits.</summary>
    public static string? Normalize(string? platformId)
    {
        if (string.IsNullOrWhiteSpace(platformId)) return null;
        string trimmed = platformId.Trim().ToUpperInvariant();
        return Regex.IsMatch(trimmed, "^[A-Z0-9]{2,4}$") ? trimmed : null;
    }
}

/// <summary>A Riot ID: game name plus tag line, as shown as <c>Name#TAG</c>.</summary>
public sealed record RiotId(string GameName, string TagLine)
{
    public override string ToString() => GameName + "#" + TagLine;

    /// <summary>
    /// Splits on the last <c>#</c>. Game names may contain spaces and other
    /// characters; the tag line is 3–5 alphanumerics.
    /// </summary>
    public static bool TryParse(string? text, out RiotId? id)
    {
        id = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        int hash = text.LastIndexOf('#');
        if (hash <= 0 || hash == text.Length - 1) return false;

        string name = text[..hash].Trim();
        string tag = text[(hash + 1)..].Trim();
        if (name.Length is < 1 or > 32 || tag.Length is < 3 or > 5) return false;
        if (tag.Any(c => !char.IsLetterOrDigit(c))) return false;

        id = new RiotId(name, tag.ToUpperInvariant());
        return true;
    }
}
