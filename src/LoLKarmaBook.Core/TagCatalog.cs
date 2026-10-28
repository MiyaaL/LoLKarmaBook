using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoLKarmaBook.Core;

/// <summary>
/// One official champion or item as delivered by the game client (or by the
/// bundled fallback). <paramref name="Title"/> is the champion's real name (the
/// client returns it as "description") and <paramref name="English"/> its
/// developer alias; both are searchable but never stored as a tag.
/// </summary>
public sealed record TagSeed(int Id, string Name, string? Title = null, string? English = null);

/// <summary>
/// The raw official catalog. It is built from the running client when possible,
/// cached on disk, and falls back to a bundled snapshot so tags still work with
/// the client closed.
/// </summary>
public sealed class TagSeedCatalog
{
    public static TagSeedCatalog Empty { get; } = new(Array.Empty<TagSeed>(), Array.Empty<TagSeed>());

    public TagSeedCatalog(IEnumerable<TagSeed> champions, IEnumerable<TagSeed> items)
    {
        ArgumentNullException.ThrowIfNull(champions);
        ArgumentNullException.ThrowIfNull(items);
        Champions = champions.ToArray();
        Items = items.ToArray();
    }

    public IReadOnlyList<TagSeed> Champions { get; }

    public IReadOnlyList<TagSeed> Items { get; }

    public bool IsEmpty => Champions.Count == 0 && Items.Count == 0;

    /// <summary>
    /// Parses the two LCU asset endpoints. Champion ids are filtered to the real
    /// roster (the client also lists Jade/event variants at id 60001+), and items
    /// are restricted to purchasable entries.
    /// </summary>
    public static TagSeedCatalog ParseLcu(JsonElement championSummary, JsonElement items)
    {
        var champions = new List<TagSeed>();
        if (championSummary.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement champion in championSummary.EnumerateArray())
            {
                long id = ReadLong(champion, "id");
                string name = ReadString(champion, "name");
                if (id <= 0 || id >= 60000 || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                champions.Add(new TagSeed(
                    (int)id,
                    name.Trim(),
                    NullIfEmpty(ReadString(champion, "description")),
                    NullIfEmpty(ReadString(champion, "alias"))));
            }
        }

        var itemRows = new List<JsonElement>();
        if (items.ValueKind == JsonValueKind.Array)
        {
            itemRows.AddRange(items.EnumerateArray());
        }
        else if (items.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in items.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    itemRows.Add(property.Value);
                }
            }
        }

        var itemsResult = new List<TagSeed>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in itemRows)
        {
            long id = ReadLong(item, "id");
            string name = ReadString(item, "name");
            if (id <= 0 || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            JsonElement inStore = default;
            if (item.TryGetProperty("inStore", out inStore) && inStore.ValueKind == JsonValueKind.False)
            {
                continue;
            }

            string trimmed = name.Trim();
            if (!seenNames.Add(trimmed))
            {
                continue;
            }

            itemsResult.Add(new TagSeed((int)id, trimmed));
        }

        return new TagSeedCatalog(champions, itemsResult);
    }

    /// <summary>Loads the snapshot embedded in this assembly (173 champions, store items).</summary>
    public static TagSeedCatalog LoadBuiltIn()
    {
        Assembly assembly = typeof(TagSeedCatalog).Assembly;
        string? name = assembly.GetManifestResourceNames()
            .FirstOrDefault(candidate => candidate.EndsWith("tag-seeds.json", StringComparison.OrdinalIgnoreCase));
        if (name is null)
        {
            return Empty;
        }

        using Stream stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return Deserialize(reader.ReadToEnd());
    }

    public static TagSeedCatalog Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        SeedDocument? document = JsonSerializer.Deserialize<SeedDocument>(json, JsonOptions);
        if (document is null)
        {
            return Empty;
        }

        return new TagSeedCatalog(
            (document.Champions ?? new List<SeedDocument.Row>())
                .Select(row => new TagSeed(row.Id, row.Name ?? string.Empty, row.Title, row.English)),
            (document.Items ?? new List<SeedDocument.Row>())
                .Select(row => new TagSeed(row.Id, row.Name ?? string.Empty)));
    }

    public string Serialize()
    {
        var document = new SeedDocument
        {
            Champions = Champions
                .Select(seed => new SeedDocument.Row
                {
                    Id = seed.Id,
                    Name = seed.Name,
                    Title = seed.Title,
                    English = seed.English
                })
                .ToList(),
            Items = Items
                .Select(seed => new SeedDocument.Row { Id = seed.Id, Name = seed.Name })
                .ToList()
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    public static TagSeedCatalog LoadFile(string path)
    {
        try
        {
            return File.Exists(path) ? Deserialize(File.ReadAllText(path, Encoding.UTF8)) : Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Empty;
        }
    }

    public bool TrySaveFile(string path)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = path + ".tmp";
            File.WriteAllText(temporary, Serialize(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long parsed)
            ? parsed
            : 0;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed class SeedDocument
    {
        public List<Row>? Champions { get; set; }
        public List<Row>? Items { get; set; }

        public sealed class Row
        {
            public int Id { get; set; }
            public string? Name { get; set; }
            public string? Title { get; set; }
            public string? English { get; set; }
        }
    }
}

/// <summary>
/// Turns official seeds into searchable <see cref="TagEntry"/> items. Only the
/// seed's official name is ever selectable; folk names, the champion's real
/// name, the English alias and pinyin only help the search find that entry.
/// </summary>
public static class TagCatalog
{
    public static IReadOnlyList<TagEntry> Build(
        TagSeedCatalog seeds,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? extraAliases = null)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        var entries = new List<TagEntry>(seeds.Champions.Count + seeds.Items.Count);
        var seen = new HashSet<(TagKind, int)>();
        Append(TagKind.Champion, seeds.Champions);
        Append(TagKind.Item, seeds.Items);
        return entries;

        void Append(TagKind kind, IEnumerable<TagSeed> rows)
        {
            foreach (TagSeed seed in rows)
            {
                string name = (seed.Name ?? string.Empty).Trim();
                if (seed.Id <= 0 || name.Length == 0 || name.Length > 60 || !seen.Add((kind, seed.Id)))
                {
                    continue;
                }

                var aliases = new List<string>();
                Add(seed.Title);
                Add(seed.English);
                if (TagAliases.Default.TryGetValue(name, out string[]? folk))
                {
                    foreach (string alias in folk)
                    {
                        Add(alias);
                    }
                }

                if (extraAliases is not null && extraAliases.TryGetValue(name, out IReadOnlyList<string>? extra))
                {
                    foreach (string alias in extra)
                    {
                        Add(alias);
                    }
                }

                if (PinyinAliases.TryCreate(name) is { } pinyin)
                {
                    Add(pinyin.Initials);
                    if (!string.Equals(pinyin.Full, pinyin.Initials, StringComparison.Ordinal))
                    {
                        Add(pinyin.Full);
                    }
                }

                entries.Add(new TagEntry(kind, seed.Id, name, aliases));

                void Add(string? alias)
                {
                    if (string.IsNullOrWhiteSpace(alias))
                    {
                        return;
                    }

                    string trimmed = alias.Trim();
                    string normalized = TagSearch.Normalize(trimmed);
                    if (normalized.Length == 0 || string.Equals(normalized, TagSearch.Normalize(name), StringComparison.Ordinal))
                    {
                        return;
                    }

                    if (aliases.Any(existing => string.Equals(
                            TagSearch.Normalize(existing),
                            normalized,
                            StringComparison.Ordinal)))
                    {
                        return;
                    }

                    aliases.Add(trimmed);
                }
            }
        }
    }

    /// <summary>Number of champions and items in a catalog, for the UI hint.</summary>
    public static (int Champions, int Items) Counts(IReadOnlyList<TagEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        int champions = 0;
        int items = 0;
        foreach (TagEntry entry in entries)
        {
            if (entry.Kind == TagKind.Champion)
            {
                champions++;
            }
            else
            {
                items++;
            }
        }

        return (champions, items);
    }
}

/// <summary>
/// Common Chinese community names. Keys are the official client names; values
/// are what people actually type. The list is intentionally short and can grow.
/// </summary>
public static class TagAliases
{
    public static IReadOnlyDictionary<string, string[]> Default { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        // ---- champions ----
        ["黑暗之女"] = new[] { "安妮" },
        ["狂战士"] = new[] { "奥拉夫" },
        ["正义巨像"] = new[] { "加里奥" },
        ["卡牌大师"] = new[] { "卡牌", "崔斯特" },
        ["德邦总管"] = new[] { "赵信", "菊花信" },
        ["诡术妖姬"] = new[] { "妖姬", "乐芙兰" },
        ["猩红收割者"] = new[] { "吸血鬼", "弗拉基米尔" },
        ["远古恐惧"] = new[] { "稻草人", "费德提克" },
        ["无极剑圣"] = new[] { "剑圣", "易大师" },
        ["牛头酋长"] = new[] { "牛头", "阿利斯塔" },
        ["符文法师"] = new[] { "瑞兹", "光头" },
        ["战争女神"] = new[] { "希维尔", "轮子妈" },
        ["众星之子"] = new[] { "索拉卡", "奶妈" },
        ["迅捷斥候"] = new[] { "提莫", "提百万" },
        ["麦林炮手"] = new[] { "小炮", "崔丝塔娜" },
        ["祖安怒兽"] = new[] { "狼人", "沃里克" },
        ["雪原双子"] = new[] { "努努" },
        ["赏金猎人"] = new[] { "女枪", "厄运小姐" },
        ["寒冰射手"] = new[] { "寒冰", "艾希" },
        ["蛮族之王"] = new[] { "蛮王", "泰达米尔" },
        ["武器大师"] = new[] { "贾克斯", "武器" },
        ["堕落天使"] = new[] { "莫甘娜" },
        ["时光守护者"] = new[] { "基兰", "时光" },
        ["炼金术士"] = new[] { "辛吉德", "炼金" },
        ["痛苦之拥"] = new[] { "伊芙琳", "寡妇" },
        ["瘟疫之源"] = new[] { "老鼠", "图奇" },
        ["死亡颂唱者"] = new[] { "死歌", "卡尔萨斯" },
        ["虚空恐惧"] = new[] { "大虫子", "科加斯" },
        ["殇之木乃伊"] = new[] { "阿木木", "木乃伊" },
        ["披甲龙龟"] = new[] { "龙龟", "拉莫斯" },
        ["冰晶凤凰"] = new[] { "凤凰", "艾尼维亚" },
        ["恶魔小丑"] = new[] { "小丑", "萨科" },
        ["祖安狂人"] = new[] { "蒙多" },
        ["琴瑟仙女"] = new[] { "琴女", "娑娜" },
        ["虚空行者"] = new[] { "卡萨丁" },
        ["刀锋舞者"] = new[] { "刀妹", "艾瑞莉娅" },
        ["风暴之怒"] = new[] { "风女", "迦娜" },
        ["海洋之灾"] = new[] { "船长", "普朗克" },
        ["英勇投弹手"] = new[] { "飞机", "库奇" },
        ["天启者"] = new[] { "扇子妈", "卡尔玛" },
        ["瓦洛兰之盾"] = new[] { "塔里克", "宝石" },
        ["邪恶小法师"] = new[] { "小法", "维迦" },
        ["巨魔之王"] = new[] { "巨魔", "特朗德尔" },
        ["诺克萨斯统领"] = new[] { "乌鸦", "斯维因" },
        ["皮城女警"] = new[] { "女警", "凯特琳" },
        ["蒸汽机器人"] = new[] { "机器人", "布里茨" },
        ["熔岩巨兽"] = new[] { "石头人", "墨菲特" },
        ["不祥之刃"] = new[] { "卡特", "卡特琳娜" },
        ["永恒梦魇"] = new[] { "梦魇", "魔腾" },
        ["扭曲树精"] = new[] { "大树", "茂凯" },
        ["荒漠屠夫"] = new[] { "鳄鱼", "雷克顿" },
        ["德玛西亚皇子"] = new[] { "皇子", "嘉文四世" },
        ["蜘蛛女皇"] = new[] { "蜘蛛", "伊莉丝" },
        ["发条魔灵"] = new[] { "发条", "奥莉安娜" },
        ["齐天大圣"] = new[] { "猴子", "孙悟空" },
        ["复仇焰魂"] = new[] { "火男", "布兰德" },
        ["盲僧"] = new[] { "盲僧", "李青" },
        ["暗夜猎手"] = new[] { "薇恩", "VN" },
        ["机械公敌"] = new[] { "兰博" },
        ["魔蛇之拥"] = new[] { "蛇女", "卡西奥佩娅" },
        ["上古领主"] = new[] { "蝎子", "斯卡纳" },
        ["大发明家"] = new[] { "大头", "黑默丁格" },
        ["沙漠死神"] = new[] { "狗头", "内瑟斯" },
        ["狂野女猎手"] = new[] { "豹女", "奈德丽" },
        ["兽灵行者"] = new[] { "乌迪尔" },
        ["圣锤之毅"] = new[] { "波比" },
        ["酒桶"] = new[] { "古拉加斯" },
        ["不屈之枪"] = new[] { "潘森" },
        ["探险家"] = new[] { "EZ", "伊泽瑞尔" },
        ["铁铠冥魂"] = new[] { "铁男", "莫德凯撒" },
        ["牧魂人"] = new[] { "掘墓", "约里克" },
        ["离群之刺"] = new[] { "阿卡丽" },
        ["狂暴之心"] = new[] { "凯南" },
        ["德玛西亚之力"] = new[] { "盖伦" },
        ["曙光女神"] = new[] { "日女", "蕾欧娜" },
        ["虚空先知"] = new[] { "蚂蚱", "玛尔扎哈" },
        ["刀锋之影"] = new[] { "男刀", "泰隆" },
        ["放逐之刃"] = new[] { "锐雯" },
        ["深渊巨口"] = new[] { "大嘴", "克格莫" },
        ["暮光之眼"] = new[] { "慎" },
        ["光辉女郎"] = new[] { "光辉", "拉克丝" },
        ["远古巫灵"] = new[] { "泽拉斯" },
        ["龙血武姬"] = new[] { "龙女", "希瓦娜" },
        ["九尾妖狐"] = new[] { "狐狸", "阿狸" },
        ["法外狂徒"] = new[] { "男枪", "格雷福斯" },
        ["潮汐海灵"] = new[] { "小鱼人", "菲兹" },
        ["不灭狂雷"] = new[] { "狗熊", "沃利贝尔" },
        ["傲之追猎者"] = new[] { "狮子狗", "雷恩加尔" },
        ["惩戒之箭"] = new[] { "韦鲁斯" },
        ["深海泰坦"] = new[] { "泰坦", "诺提勒斯" },
        ["奥术先驱"] = new[] { "维克托" },
        ["北地之怒"] = new[] { "猪妹", "瑟庄妮" },
        ["无双剑姬"] = new[] { "剑姬", "菲奥娜" },
        ["爆破鬼才"] = new[] { "炸弹人", "吉格斯" },
        ["仙灵女巫"] = new[] { "璐璐" },
        ["荣耀行刑官"] = new[] { "德莱文" },
        ["战争之影"] = new[] { "人马", "赫卡里姆" },
        ["虚空掠夺者"] = new[] { "螳螂", "卡兹克" },
        ["诺克萨斯之手"] = new[] { "诺手", "德莱厄斯" },
        ["未来守护者"] = new[] { "杰斯" },
        ["冰霜女巫"] = new[] { "冰女", "丽桑卓" },
        ["皎月女神"] = new[] { "皎月", "黛安娜" },
        ["德玛西亚之翼"] = new[] { "奎因" },
        ["暗黑元首"] = new[] { "辛德拉" },
        ["铸星龙王"] = new[] { "龙王", "奥瑞利安·索尔" },
        ["影流之镰"] = new[] { "凯隐" },
        ["暮光星灵"] = new[] { "佐伊" },
        ["荆棘之兴"] = new[] { "婕拉" },
        ["虚空之女"] = new[] { "卡莎" },
        ["星籁歌姬"] = new[] { "萨勒芬妮" },
        ["迷失之牙"] = new[] { "纳尔" },
        ["生化魔人"] = new[] { "扎克" },
        ["疾风剑豪"] = new[] { "亚索", "快乐风男" },
        ["虚空之眼"] = new[] { "大眼", "维克兹" },
        ["岩雀"] = new[] { "塔莉垭" },
        ["青钢影"] = new[] { "卡蜜尔" },
        ["影流之主"] = new[] { "劫" },
        ["暴走萝莉"] = new[] { "金克丝" },
        ["河流之王"] = new[] { "蛤蟆", "塔姆" },
        ["圣枪游侠"] = new[] { "卢锡安", "奥巴马" },
        ["时间刺客"] = new[] { "艾克" },
        ["含羞蓓蕾"] = new[] { "莉莉娅" },
        ["封魔剑魂"] = new[] { "永恩" },
        ["愁云使者"] = new[] { "薇古丝" },
        ["灵罗娃娃"] = new[] { "格温" },
        ["祖安花火"] = new[] { "泽丽" },
        ["虚空女皇"] = new[] { "卑尔维斯" },
        ["纳祖芒荣耀"] = new[] { "奎桑提" },
        ["不羁之悦"] = new[] { "尼菈" },
        ["明烛"] = new[] { "米利欧" },
        ["异画师"] = new[] { "慧" },
        ["百裂冥犬"] = new[] { "纳亚菲利" },
        ["双界灵兔"] = new[] { "阿萝拉" },
        ["铁血狼母"] = new[] { "安蓓萨" },
        ["流光镜影"] = new[] { "梅尔" },
        ["不破之誓"] = new[] { "芸阿娜" },
        ["沙漠玫瑰"] = new[] { "莎弥拉" },
        ["永猎双子"] = new[] { "千珏" },
        ["涤魂圣枪"] = new[] { "赛娜" },
        ["万花通灵"] = new[] { "妮蔻" },
        ["残月之肃"] = new[] { "厄斐琉斯" },
        ["镕铁少女"] = new[] { "芮尔" },
        ["血港鬼影"] = new[] { "派克" },
        ["元素女皇"] = new[] { "奇亚娜" },
        ["正义天使"] = new[] { "凯尔" },
        ["解脱者"] = new[] { "塞拉斯" },
        ["腕豪"] = new[] { "瑟提" },
        ["魔法猫咪"] = new[] { "悠米", "猫" },
        ["破败之王"] = new[] { "佛耶戈" },

        // ---- items ----
        ["无尽之刃"] = new[] { "无尽", "IE", "暴击剑" },
        ["鬼索的狂暴之刃"] = new[] { "羊刀", "鬼索" },
        ["灭世者的死亡之帽"] = new[] { "帽子", "大帽" },
        ["中娅沙漏"] = new[] { "金身", "沙漏" },
        ["守护天使"] = new[] { "复活甲", "春哥" },
        ["卢安娜的飓风"] = new[] { "飓风" },
        ["破败王者之刃"] = new[] { "破败" },
        ["三相之力"] = new[] { "三相" },
        ["神圣分离者"] = new[] { "神分" },
        ["渴血战斧"] = new[] { "渴血" },
        ["挺进破坏者"] = new[] { "挺进" },
        ["黑色切割者"] = new[] { "黑切" },
        ["荆棘之甲"] = new[] { "反甲" },
        ["狂徒铠甲"] = new[] { "狂徒" },
        ["振奋盔甲"] = new[] { "振奋" },
        ["亡者的板甲"] = new[] { "板甲" },
        ["自然之力"] = new[] { "自然" },
        ["兰顿之兆"] = new[] { "兰顿" },
        ["冰霜之心"] = new[] { "冰心" },
        ["深渊面具"] = new[] { "深渊" },
        ["帝国指令"] = new[] { "帝国" },
        ["月石再生器"] = new[] { "月石" },
        ["钢铁烈阳之匣"] = new[] { "鸟盾" },
        ["炽热香炉"] = new[] { "香炉" },
        ["流水法杖"] = new[] { "流水" },
        ["恶魔之拥"] = new[] { "恶魔" },
        ["影焰"] = new[] { "影焰" },
        ["视界专注"] = new[] { "视界" },
        ["虚空之杖"] = new[] { "法穿棒" },
        ["莫雷洛秘典"] = new[] { "鬼书" },
        ["瑞莱的冰晶节杖"] = new[] { "冰杖" },
        ["星界驱驰"] = new[] { "星界" },
        ["时光之杖"] = new[] { "时光" },
        ["大天使之杖"] = new[] { "大天使" },
        ["魔切"] = new[] { "魔切" },
        ["玛莫提乌斯之噬"] = new[] { "饮魔刀" },
        ["死亡之舞"] = new[] { "死舞" },
        ["斯特拉克的挑战护手"] = new[] { "血手" },
        ["破舰者"] = new[] { "破舰" },
        ["巨型九头蛇"] = new[] { "巨九" },
        ["贪欲九头蛇"] = new[] { "贪九" },
        ["狂战士胫甲"] = new[] { "攻速鞋" },
        ["法师之靴"] = new[] { "法穿鞋" },
        ["水银之靴"] = new[] { "水银鞋", "韧性鞋" },
        ["铁板靴"] = new[] { "布甲鞋" },
        ["明朗之靴"] = new[] { "CD鞋" },
        ["疾行之靴"] = new[] { "五速鞋" },
        ["海克斯科技火箭腰带"] = new[] { "推推棒", "腰带" },
        ["卢登的伙伴"] = new[] { "卢登" },
        ["兰德里的折磨"] = new[] { "面具", "大面具" },
        ["峡谷制造者"] = new[] { "制造者" },
        ["纳什之牙"] = new[] { "纳什" },
        ["智慧末刃"] = new[] { "智慧" },
        ["界弓"] = new[] { "界弓" },
        ["海妖杀手"] = new[] { "海妖" },
        ["收集者"] = new[] { "收集者" },
        ["多米尼克领主的致意"] = new[] { "大轻语" },
        ["凡性的提醒"] = new[] { "小轻语", "重伤弓" },
        ["疾射火炮"] = new[] { "火炮" },
        ["幻影之舞"] = new[] { "绿叉" },
        ["斯塔缇克电刃"] = new[] { "电刀" },
        ["基舍艾斯碎片"] = new[] { "碎片" },
        ["暴风大剑"] = new[] { "大剑" },
        ["十字镐"] = new[] { "十字镐" },
        ["最后的轻语"] = new[] { "轻语" },
        ["女神之泪"] = new[] { "眼泪" },
        ["遗失的章节"] = new[] { "章节" },
        ["燃烧宝石"] = new[] { "燃烧宝石" },
        ["考尔菲德的战锤"] = new[] { "战锤" },
        ["秒表"] = new[] { "秒表" },
        ["增幅典籍"] = new[] { "书" },
        ["红水晶"] = new[] { "红水晶" },
        ["蓝水晶"] = new[] { "蓝水晶" },
        ["布甲"] = new[] { "布甲" }
    };
}

/// <summary>Pinyin search aliases, derived from the official name with TinyPinyin.</summary>
public static class PinyinAliases
{
    public static (string Initials, string Full)? TryCreate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var initials = new StringBuilder(name.Length);
        var full = new StringBuilder(name.Length * 4);
        var sawChinese = false;
        foreach (char character in name)
        {
            if (TinyPinyin.PinyinHelper.IsChinese(character))
            {
                string syllable = TinyPinyin.PinyinHelper.GetPinyin(character);
                if (string.IsNullOrEmpty(syllable))
                {
                    return null;
                }

                string lower = syllable.ToLowerInvariant();
                initials.Append(lower[0]);
                full.Append(lower);
                sawChinese = true;
            }
            else if (char.IsLetterOrDigit(character) && character < 128)
            {
                initials.Append(char.ToLowerInvariant(character));
                full.Append(char.ToLowerInvariant(character));
            }
        }

        if (!sawChinese || initials.Length < 2)
        {
            return null;
        }

        return (initials.ToString(), full.ToString());
    }
}
