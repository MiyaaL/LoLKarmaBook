using LoLKarmaBook.Core;

namespace LoLKarmaBook.Core.Tests;

internal static class TagCatalogTests
{
    private static readonly TagSeedCatalog Seeds = new(
        new[]
        {
            new TagSeed(103, "九尾妖狐", "阿狸", "Ahri"),
            new TagSeed(875, "腕豪", "瑟提", "Sett")
        },
        new[]
        {
            new TagSeed(3031, "无尽之刃"),
            new TagSeed(3157, "中娅沙漏")
        });

    private static IReadOnlyList<TagEntry> Catalog => TagCatalog.Build(Seeds);

    public static void PinyinInitialsAndFullPinyinFindChampions()
    {
        Check.Equal(103, TagSearch.Search(Catalog, "jwyh").Single().Id);
        Check.Equal(103, TagSearch.Search(Catalog, "jiuweiyaohu").Single().Id);
        Check.Equal(875, TagSearch.Search(Catalog, "wh").Single().Id);
        Check.Equal(3031, TagSearch.Search(Catalog, "wjzr").Single().Id);
        Check.Equal(3157, TagSearch.Search(Catalog, "zysl").Single().Id);
    }

    public static void FolkNamesRealNamesAndEnglishFindTheOfficialEntry()
    {
        Check.Equal("九尾妖狐", TagSearch.Search(Catalog, "狐狸").Single().OfficialName);
        Check.Equal("九尾妖狐", TagSearch.Search(Catalog, "阿狸").Single().OfficialName);
        Check.Equal("九尾妖狐", TagSearch.Search(Catalog, "ahri").Single().OfficialName);
        Check.Equal("无尽之刃", TagSearch.Search(Catalog, "无尽").Single().OfficialName);
        Check.Equal("中娅沙漏", TagSearch.Search(Catalog, "金身").Single().OfficialName);
        Check.Equal(TagKind.Item, TagSearch.Search(Catalog, "金身").Single().Kind);
    }

    public static void OnlyOfficialEntriesAndNoSelfAliasesAreReturned()
    {
        foreach (TagEntry entry in Catalog)
        {
            Check.True(entry.Id > 0, "every tag must carry an official id");
            Check.True(entry.OfficialName.Length > 0, "every tag must carry an official name");
            Check.False(
                entry.Aliases.Any(alias => TagSearch.Normalize(alias) == TagSearch.Normalize(entry.OfficialName)),
                "an alias must not repeat the official name");
        }

        Check.Equal(0, TagSearch.Search(Catalog, "完全不存在").Count);
        Check.Equal(Catalog.Count, TagSearch.Search(Catalog, "").Count);
    }

    public static void BuiltInSnapshotProvidesChampionsItemsAndAliases()
    {
        TagSeedCatalog builtIn = TagSeedCatalog.LoadBuiltIn();
        Check.True(builtIn.Champions.Count >= 160, "the bundled champion snapshot must be present");
        Check.True(builtIn.Items.Count >= 300, "the bundled item snapshot must be present");
        Check.True(
            builtIn.Champions.Any(seed => seed.Name == "九尾妖狐"),
            "the bundled snapshot must contain 九尾妖狐");

        IReadOnlyList<TagEntry> catalog = TagCatalog.Build(builtIn);
        Check.True(catalog.Count >= 500, "the built catalog must be large enough to search");
        Check.Equal("九尾妖狐", TagSearch.Search(catalog, "狐狸").First().OfficialName);
        Check.Equal("九尾妖狐", TagSearch.Search(catalog, "jwyh").First().OfficialName);
        Check.Equal("无尽之刃", TagSearch.Search(catalog, "无尽").First().OfficialName);
    }

    public static void LcuPayloadsAreParsedIntoOfficialSeeds()
    {
        const string champions = """
            [
              {"id":-1,"name":"无","description":"","alias":"None"},
              {"id":103,"name":"九尾妖狐","description":"阿狸","alias":"Ahri"},
              {"id":60001,"name":"黑暗之女","description":"安妮","alias":"Jade_Annie"}
            ]
            """;
        const string items = """
            [
              {"id":1001,"name":"速度之靴","inStore":true},
              {"id":1002,"name":"速度之靴","inStore":true},
              {"id":9999,"name":"被隐藏的装备","inStore":false}
            ]
            """;
        using var championDocument = System.Text.Json.JsonDocument.Parse(champions);
        using var itemDocument = System.Text.Json.JsonDocument.Parse(items);
        TagSeedCatalog parsed = TagSeedCatalog.ParseLcu(
            championDocument.RootElement,
            itemDocument.RootElement);

        Check.Equal(1, parsed.Champions.Count);
        Check.Equal(103, parsed.Champions[0].Id);
        Check.Equal("阿狸", parsed.Champions[0].Title);
        Check.Equal(1, parsed.Items.Count);
        Check.Equal(1001, parsed.Items[0].Id);

        TagSeedCatalog roundTrip = TagSeedCatalog.Deserialize(parsed.Serialize());
        Check.Equal(1, roundTrip.Champions.Count);
        Check.Equal(1, roundTrip.Items.Count);
    }
}
