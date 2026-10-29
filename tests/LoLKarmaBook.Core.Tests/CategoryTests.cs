using LoLKarmaBook.Core;
using static LoLKarmaBook.Core.Tests.TestSupport;

namespace LoLKarmaBook.Core.Tests;

internal static class CategoryTests
{
    public static async Task NewCategories_AreSelectableAndPersist()
    {
        // Every defined category must be offered by the UI list.
        foreach (NoteCategory category in Enum.GetValues<NoteCategory>())
        {
            Check.True(NoteCategories.IsDefined(category), "category not recognised: " + category);
            Check.True(NoteCategories.All.Contains(category), "category missing from All: " + category);
            Check.True(NoteCategories.TryParseCode(NoteCategories.GetCode(category), out NoteCategory parsed)
                       && parsed == category, "code round-trip failed: " + category);
        }

        Check.Equal("仇杀", NoteCategories.GetDisplayName(NoteCategory.Vendetta));
        Check.Equal("乱选英雄", NoteCategories.GetDisplayName(NoteCategory.RandomChampion));

        using var temp = new TempDirectory();
        using var protector = CreateProtector();
        var clock = new TestClock();
        var scenario = new SyntheticScenario();
        var store = await OpenStoreAsync(temp.Combine("root"), "categories", protector);
        try
        {
            var service = new NotebookService(store, clock);
            StoredNote note = await service.SaveNoteAsync(
                scenario.MatchA,
                SyntheticScenario.MatchAXParticipantKey,
                Draft(categories: new[] { NoteCategory.Vendetta, NoteCategory.RandomChampion }));
            Check.Equal(2, note.Categories.Count);
            Check.True(note.Categories.Any(entry => entry.Category == NoteCategory.Vendetta),
                "仇杀 was not stored");

            StoredNote? reloaded = await service.GetNoteAsync(note.NoteId);
            Check.True(reloaded!.Categories.Any(entry => entry.Category == NoteCategory.Vendetta),
                "仇杀 did not survive the reload");
        }
        finally
        {
            await store.DisposeAsync();
        }
    }
}
