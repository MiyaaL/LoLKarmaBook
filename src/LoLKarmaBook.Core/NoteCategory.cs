namespace LoLKarmaBook.Core;

/// <summary>
/// The five fixed, user-selected note categories in the MVP. None of these is
/// inferred from score, KDA, items or automation.
/// </summary>
public enum NoteCategory
{
    AfkLeave = 1,
    PassivePlay = 2,
    VerbalAbuse = 3,
    LowSkill = 4,
    OddBuild = 5,
    RandomChampion = 6
}

public static class NoteCategories
{
    /// <summary>Product default: reminders last 365 days.</summary>
    public const int DefaultReminderDays = 365;

    public static IReadOnlyList<NoteCategory> All { get; } = Array.AsReadOnly(new[]
    {
        NoteCategory.AfkLeave,
        NoteCategory.PassivePlay,
        NoteCategory.VerbalAbuse,
        NoteCategory.LowSkill,
        NoteCategory.OddBuild
    });

    public static string GetCode(NoteCategory category) => category switch
    {
        NoteCategory.AfkLeave => "AFK_LEAVE",
        NoteCategory.PassivePlay => "PASSIVE_PLAY",
        NoteCategory.VerbalAbuse => "VERBAL_ABUSE",
        NoteCategory.LowSkill => "LOW_SKILL",
        NoteCategory.OddBuild => "ODD_BUILD",
        NoteCategory.RandomChampion => "RANDOM_CHAMPION",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown note category.")
    };

    public static string GetDisplayName(NoteCategory category) => category switch
    {
        NoteCategory.AfkLeave => "挂机逃跑",
        NoteCategory.PassivePlay => "消极游戏",
        NoteCategory.VerbalAbuse => "言语挑衅、辱骂",
        NoteCategory.LowSkill => "游戏技术水平较低",
        NoteCategory.OddBuild => "乱出装",
        NoteCategory.RandomChampion => "乱选英雄",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown note category.")
    };

    public static bool TryParseCode(string? code, out NoteCategory category)
    {
        category = default;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        foreach (NoteCategory candidate in All)
        {
            if (string.Equals(GetCode(candidate), code.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                category = candidate;
                return true;
            }
        }

        return false;
    }

    public static bool IsDefined(NoteCategory category) =>
        category is >= NoteCategory.AfkLeave and <= NoteCategory.RandomChampion;
}
