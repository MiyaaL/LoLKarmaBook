namespace LoLKarmaBook.Core;

/// <summary>
/// One official champion or item attached to a note. Only official ids and names
/// are stored; the search box may accept folk names but always resolves to one of
/// these entries. <see cref="OfficialName"/> is the name as resolved when the tag
/// was added, kept so a record stays readable even if the catalog changes later.
/// </summary>
public sealed record NoteTag(TagKind Kind, int RefId, string OfficialName)
{
    public static NoteTag Champion(int championId, string name) =>
        new(TagKind.Champion, championId, name);

    public static NoteTag Item(int itemId, string name) =>
        new(TagKind.Item, itemId, name);
}
