namespace LoLKarmaBook.Core;

public enum TagKind
{
    Champion = 1,
    Item = 2
}

/// <summary>One selectable official champion or item, plus the folk names people search by.</summary>
public sealed record TagEntry(TagKind Kind, int Id, string OfficialName, IReadOnlyList<string> Aliases)
{
    public TagEntry(TagKind kind, int id, string officialName)
        : this(kind, id, officialName, Array.Empty<string>())
    {
    }
}

/// <summary>
/// Matches a search box against official champion/item names. Only the entries
/// themselves are ever selected; folk names exist purely to find them.
/// </summary>
public static class TagSearch
{
    public static IReadOnlyList<TagEntry> Search(IReadOnlyList<TagEntry> entries, string? query, int limit = 8)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (limit <= 0)
        {
            return Array.Empty<TagEntry>();
        }

        string needle = Normalize(query);
        if (needle.Length == 0)
        {
            return entries.Take(limit).ToArray();
        }

        var scored = new List<(TagEntry Entry, int Score, int Order)>();
        for (int index = 0; index < entries.Count; index++)
        {
            TagEntry entry = entries[index];
            int score = Score(entry, needle);
            if (score > 0)
            {
                scored.Add((entry, score, index));
            }
        }

        return scored
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Order)
            .Take(limit)
            .Select(item => item.Entry)
            .ToArray();
    }

    /// <summary>Higher is a better hit; 0 means no match.</summary>
    public static int Score(TagEntry entry, string query)
    {
        string needle = Normalize(query);
        if (needle.Length == 0)
        {
            return 1;
        }

        string name = Normalize(entry.OfficialName);
        if (name == needle)
        {
            return 100;
        }

        foreach (string alias in entry.Aliases)
        {
            string normalizedAlias = Normalize(alias);
            if (normalizedAlias.Length == 0)
            {
                continue;
            }

            if (normalizedAlias == needle)
            {
                return 95;
            }

            if (normalizedAlias.StartsWith(needle, StringComparison.Ordinal))
            {
                return 85;
            }

            if (normalizedAlias.Contains(needle, StringComparison.Ordinal))
            {
                return 70;
            }
        }

        if (name.StartsWith(needle, StringComparison.Ordinal))
        {
            return 90;
        }

        // Initials ("jyhy" for 九尾妖狐) are matched through the pinyin field when present.
        return name.Contains(needle, StringComparison.Ordinal) ? 60 : 0;
    }

    /// <summary>Trims, lowercases and drops the separators people type by habit.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        Span<char> buffer = stackalloc char[value.Length];
        int length = 0;
        foreach (char character in value)
        {
            if (char.IsWhiteSpace(character) || character is '#' or '·' or '-' or '_')
            {
                continue;
            }

            buffer[length++] = char.ToLowerInvariant(character);
        }

        return new string(buffer[..length]);
    }
}
