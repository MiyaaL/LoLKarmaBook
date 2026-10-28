using LoLKarmaBook.Core;

namespace LoLKarmaBook.Core.Tests;

internal static class TagTests
{
    private static readonly TagEntry Ahri = new(TagKind.Champion, 103, "九尾妖狐", new[] { "狐狸", "阿狸" });
    private static readonly TagEntry Sett = new(TagKind.Champion, 875, "腕豪", new[] { "瑟提" });
    private static readonly TagEntry InfinityEdge = new(TagKind.Item, 3031, "无尽之刃", new[] { "无尽", "IE" });
    private static readonly TagEntry Zhonya = new(TagKind.Item, 3157, "中娅沙漏", new[] { "金身" });

    private static readonly TagEntry[] Entries = { Ahri, Sett, InfinityEdge, Zhonya };

    public static void OfficialNameAndFolkNamesBothFindTheSameEntry()
    {
        Check.Equal(Ahri, TagSearch.Search(Entries, "九尾").Single());
        Check.Equal(Ahri, TagSearch.Search(Entries, "狐狸").Single());
        Check.Equal(Ahri, TagSearch.Search(Entries, "阿狸").Single());
        Check.Equal(InfinityEdge, TagSearch.Search(Entries, "无尽").Single());
        Check.Equal(Zhonya, TagSearch.Search(Entries, "金身").Single());
    }

    public static void RankingPrefersStrongerMatches()
    {
        IReadOnlyList<TagEntry> results = TagSearch.Search(Entries, "无");
        Check.Equal(InfinityEdge, results[0]);
        Check.Equal(1, results.Count);
    }

    public static void OnlyOfficialEntriesCanBeReturned()
    {
        // "狐" only exists as a folk name; the result is still the official entry.
        TagEntry match = TagSearch.Search(Entries, "狐").Single();
        Check.Equal("九尾妖狐", match.OfficialName);
        Check.Equal(TagKind.Champion, match.Kind);
    }

    public static void EmptyQueryListsCandidatesAndNoMatchReturnsNothing()
    {
        Check.Equal(Entries.Length, TagSearch.Search(Entries, "").Count);
        Check.Equal(0, TagSearch.Search(Entries, "不存在的名字").Count);
    }

    public static void NormalizeIgnoresSeparatorsAndCase()
    {
        Check.Equal("无尽之刃", TagSearch.Normalize(" 无尽 之刃 "));
        Check.Equal("ie", TagSearch.Normalize("I-E"));
        Check.Equal(InfinityEdge, TagSearch.Search(Entries, "I E").Single());
    }
}
