using LoLKarmaBook.Core;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>
/// A roster pinned for the rest of one match. The live client retains the
/// post-game roster only briefly, so the end-of-match prompt and any reminder
/// shown late in the match work from this copy instead of the next observation.
/// Lives in memory only; nothing is written to the profile database.
/// </summary>
public sealed class MatchRosterSnapshot
{
    private MatchRosterSnapshot(MatchSession source, IReadOnlyList<Reminder> reminders)
    {
        MatchKey = source.MatchKey;
        LocalSessionId = source.LocalSessionId;
        Session = source;
        Reminders = reminders;
    }

    /// <summary>Match key of the originating live observation.</summary>
    public string MatchKey { get; }

    /// <summary>Local session id of the last observed roster (note provenance).</summary>
    public string LocalSessionId { get; }

    /// <summary>The final roster, kept only as a selection source.</summary>
    public MatchSession Session { get; }

    /// <summary>Reminders this match delivers, captured before the roster expires.</summary>
    public IReadOnlyList<Reminder> Reminders { get; }

    public bool IsSameMatch(MatchSession other) =>
        string.Equals(MatchKey, other.MatchKey, StringComparison.Ordinal);

    public static MatchRosterSnapshot Capture(MatchSession source, IReadOnlyList<Reminder> reminders) =>
        new(source, reminders);

    public MatchRosterSnapshot WithReminders(MatchSession source, IReadOnlyList<Reminder> reminders) =>
        new(source, reminders);

    /// <summary>Same participants and match, newer observation time.</summary>
    public MatchRosterSnapshot Refresh(MatchSession source) => new(source, Reminders);
}
