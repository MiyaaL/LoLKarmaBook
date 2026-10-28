namespace LoLKarmaBook.Core;

/// <summary>
/// Central reminder rules shared by the service, desktop and CLI. Reminders are
/// considered for observed rosters in active match phases. Manual input does not
/// supply an automatically matched roster.
/// </summary>
public static class ReminderPolicy
{
    /// <summary>
    /// Whether the session has an active selection, loading or in-game roster.
    /// </summary>
    public static bool IsActiveRosterPhase(GamePhase phase) =>
        phase is GamePhase.ChampionSelect or GamePhase.Loading or GamePhase.InGame;

    /// <summary>
    /// Whether the client reports the match as over. The post-game roster is kept
    /// only as a selection source for the end-of-match prompt; it never notifies
    /// during play and does not consume reminder receipts.
    /// </summary>
    public static bool IsFinishedPhase(GamePhase phase) => phase is GamePhase.Finished;

    /// <summary>
    /// Phases whose roster may be matched against stored notes. Post-game is
    /// included for the end-of-match preview only; delivery still uses
    /// <see cref="CanRemind"/> so a finished phase never fires a reminder.
    /// </summary>
    public static bool IsRosterPhase(GamePhase phase) => IsActiveRosterPhase(phase) || IsFinishedPhase(phase);

    /// <summary>
    /// True when the source kind and phase allow reminder matching/notification.
    /// </summary>
    public static bool CanRemind(MatchSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return (session.SourceKind is SourceKind.Synthetic or SourceKind.Live)
            && IsActiveRosterPhase(session.Phase);
    }
}
