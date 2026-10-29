using System.IO;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

public sealed partial class MainViewModel
{
    private readonly HashSet<string> _finishedMatches = new(StringComparer.Ordinal);
    private MatchRosterSnapshot? _matchSnapshot;
    private MatchRosterSnapshot? _observedMatchSnapshot;
    private MatchSession? _lastFinishedSession;
    private DateTimeOffset? _lastMatchFinishedAt;
    private EndOfGameViewModel? _endOfGame;
    private bool _endOfGameOpenedManually;
    private bool _lastMatchExpired;
    private int _endOfGameGeneration;
    private static readonly TimeSpan LastMatchWindow = TimeSpan.FromMinutes(20);
    internal Func<DateTimeOffset> EndOfGameNow { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>
    /// Raised when the end-of-match window must be shown or closed. The window is
    /// only a view of <see cref="EndOfGame"/>, so the view model never touches
    /// windows itself. null means "close".
    /// </summary>
    public event Action<EndOfGameViewModel?>? EndOfGameChanged;

    /// <summary>True while the end-of-match window is open (realtime keeps polling).</summary>
    public bool IsEndOfGameOpen => _endOfGame is not null;

    /// <summary>True while the last completed roster is within its marking window.</summary>
    public bool HasMatchSnapshot => _matchSnapshot is not null && _lastMatchFinishedAt is { } finishedAt
        && EndOfGameNow() - finishedAt < LastMatchWindow;

    /// <summary>Opens the end-of-match window for the last completed match.</summary>
    public IAsyncCommand MarkLastMatchCommand { get; private set; } = null!;

    /// <summary>Non-null while the end-of-match window is open.</summary>
    public EndOfGameViewModel? EndOfGame => _endOfGame;

    /// <summary>
    /// Called after a live observation has been applied to the current match.
    /// Pins the roster and, once the client reports the match as over, opens the
    /// window when this match met known players. Nothing else becomes visible.
    /// </summary>
    private async Task ObserveMatchLifecycleAsync(MatchSession? session)
    {
        ExpireLastMatch();
        if (session is null)
        {
            // Lobby and brief disconnects do not erase a completed match's roster.
            if (!_endOfGameOpenedManually) CloseEndOfGame();
            Current.RefreshLastMatchState();
            return;
        }
        if (session.SourceKind != SourceKind.Live || IsDemoProfile) return;

        bool sameObservedMatch = IsSameMatch(_observedMatchSnapshot, session);
        if (_observedMatchSnapshot is not null && !sameObservedMatch)
        {
            // A genuine new match is a boundary; repeated phases of that match are not.
            _endOfGameGeneration++;
            CloseEndOfGame();
        }
        _observedMatchSnapshot = sameObservedMatch
            ? _observedMatchSnapshot!.Refresh(session)
            : MatchRosterSnapshot.Capture(session, Array.Empty<Reminder>());

        if (ReminderPolicy.IsFinishedPhase(session.Phase))
        {
            // The client reveals hidden summoner names once the match is over, so the
            // newest finished observation is the authoritative roster for this match.
            _lastFinishedSession = session;
            if (!IsSameMatch(_matchSnapshot, session))
            {
                _endOfGameGeneration++;
                CloseEndOfGame();
                _matchSnapshot = MatchRosterSnapshot.Capture(session, Array.Empty<Reminder>());
                _lastMatchFinishedAt = EndOfGameNow();
                _lastMatchExpired = false;
            }
            Current.RefreshLastMatchState();
            if (HasMatchSnapshot) await PromptEndOfMatchAsync(_matchSnapshot!).ConfigureAwait(true);
            _endOfGame?.RefreshRoster(session);
            return;
        }

        if (!_endOfGameOpenedManually) CloseEndOfGame();
        Current.RefreshLastMatchState();
    }

    private static bool IsSameMatch(MatchRosterSnapshot? snapshot, MatchSession session) => snapshot is not null
        && (snapshot.LocalSessionId == session.LocalSessionId || snapshot.IsSameMatch(session));

    /// <summary>
    /// Newest finished observation of the same match, which carries the summoner names
    /// the client only reveals after the game ends.
    /// </summary>
    private MatchSession LatestFinishedSession(MatchSession fallback) => _lastFinishedSession is { } latest
        && (string.Equals(latest.MatchKey, fallback.MatchKey, StringComparison.Ordinal)
            || string.Equals(latest.LocalSessionId, fallback.LocalSessionId, StringComparison.Ordinal))
        ? latest
        : fallback;

    private void ExpireLastMatch()
    {
        if (_matchSnapshot is null || HasMatchSnapshot || _lastMatchExpired) return;
        _lastMatchExpired = true;
        _endOfGameGeneration++;
        CloseEndOfGame();
        Current.RefreshLastMatchState();
    }

    /// <summary>
    /// Captures the reminders this match would deliver and, when it matched known
    /// players, shows the end-of-match window. Runs once per match.
    /// </summary>
    private async Task PromptEndOfMatchAsync(MatchRosterSnapshot snapshot)
    {
        bool seen = _finishedMatches.Contains(snapshot.MatchKey)
            || _finishedMatches.Contains("local:" + snapshot.LocalSessionId);
        _finishedMatches.Add(snapshot.MatchKey);
        _finishedMatches.Add("local:" + snapshot.LocalSessionId);
        if (seen) return;
        int generation = _endOfGameGeneration;
        string profileId = CurrentProfileId;
        try
        {
            using WorkspaceLease lease = await AcquireWorkspaceAsync().ConfigureAwait(true);
            MatchSession target = LatestFinishedSession(snapshot.Session);
            IReadOnlyList<Reminder> reminders = await lease.Notebook
                .FindRemindersAsync(target)
                .ConfigureAwait(true);
            if (reminders.Count == 0 || _endOfGame is not null
                || !IsEndOfGameContextCurrent(snapshot, generation, profileId, lease.Notebook)) return;
            OpenEndOfGame(snapshot.WithReminders(target, reminders), lease.Notebook, manually: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (generation != _endOfGameGeneration || _isShuttingDown) return;
            _log.Warn("eog", "End-of-match reminder lookup failed", ex);
            Status.Warn("读取再遇记录失败，本局仍可手动标记玩家。");
        }
    }

    private bool IsEndOfGameContextCurrent(MatchRosterSnapshot snapshot, int generation, string profileId,
        NotebookService notebook) => generation == _endOfGameGeneration && !_isShuttingDown && !IsDemoProfile
        && HasMatchSnapshot && ReferenceEquals(_matchSnapshot, snapshot) && CurrentProfileId == profileId
        && ReferenceEquals(TryGetWorkspace()?.Notebook, notebook);

    private void InitializeEndOfGame()
    {
        MarkLastMatchCommand = new AsyncRelayCommand(RequestEndOfGameAsync, () => CanRequestEndOfGame,
            ex => ShowError(ex, "打开本局标记失败"));
        WorkspaceClosing += (_, _) => ResetMatchLifecycle();
    }

    private bool CanRequestEndOfGame => !_isShuttingDown && !IsMaintenanceBusy && IsProfileReady && Current.CanMarkLastMatch;

    private void OpenEndOfGame(MatchRosterSnapshot snapshot, NotebookService notebook, bool manually)
    {
        CloseEndOfGame();
        var viewModel = new EndOfGameViewModel(this, snapshot, notebook);
        viewModel.DraftSaved += OnEndOfGameDraftSaved;
        _endOfGame = viewModel;
        _endOfGameOpenedManually = manually;
        OnPropertyChanged(nameof(IsEndOfGameOpen));
        EndOfGameChanged?.Invoke(_endOfGame);
    }

    /// <summary>Closes the window (title bar or 「暂不标记」).</summary>
    public void CloseEndOfGame()
    {
        if (_endOfGame is null) return;
        EndOfGameViewModel closing = _endOfGame;
        closing.DraftSaved -= OnEndOfGameDraftSaved;
        _endOfGame = null;
        _endOfGameOpenedManually = false;
        closing.NotifyClosed();
        EndOfGameChanged?.Invoke(null);
        OnPropertyChanged(nameof(IsEndOfGameOpen));
    }

    /// <summary>A note was saved from the end-of-match window.</summary>
    private void OnEndOfGameDraftSaved(object? sender, EventArgs e)
    {
        Status.Success("赛后记录已保存。");
        // Refresh the active page so the new note is visible without a manual reload.
        if (!_isShuttingDown) _ = RefreshActiveTabAsync();
    }

    /// <summary>
    /// Opens the window on demand (「标记上一局」) for a match that produced no
    /// prompt, or whose prompt was already dismissed.
    /// </summary>
    public async Task<bool> RequestEndOfGameAsync()
    {
        ExpireLastMatch();
        MatchRosterSnapshot? snapshot = _matchSnapshot;
        if (!CanRequestEndOfGame || snapshot is null)
        {
            Status.Warn(snapshot is null ? "尚未检测到已结束对局，请在对局结束后再试。" : "上一局标记窗口已失效，请等待下一局结束。");
            return false;
        }
        if (_endOfGame is not null)
        {
            _endOfGameOpenedManually = true;
            EndOfGameChanged?.Invoke(_endOfGame);
            return true;
        }

        int generation = _endOfGameGeneration;
        string profileId = CurrentProfileId;
        try
        {
            using WorkspaceLease lease = await AcquireWorkspaceAsync().ConfigureAwait(true);
            MatchSession target = LatestFinishedSession(snapshot.Session);
            IReadOnlyList<Reminder> reminders = await lease.Notebook
                .FindRemindersAsync(target)
                .ConfigureAwait(true);
            if (!IsEndOfGameContextCurrent(snapshot, generation, profileId, lease.Notebook)) return false;
            OpenEndOfGame(snapshot.WithReminders(target, reminders), lease.Notebook, manually: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (generation != _endOfGameGeneration || _isShuttingDown) return false;
            _log.Warn("eog", "Opening the end-of-match window failed", ex);
            Status.Warn("无法读取本局再遇记录，请稍后重试。");
            return false;
        }
    }

    /// <summary>
    /// An open window belongs to the profile it was built from, so a profile
    /// switch closes it instead of leaving stale data on screen.
    /// </summary>
    private void OnProfileSwitched()
    {
        ResetMatchLifecycle();
    }

    /// <summary>Window or title-bar close: dismissing never writes a note.</summary>
    public void DismissEndOfGame() => CloseEndOfGame();

    internal void InvalidateEndOfGame() => ResetMatchLifecycle();

    private void ResetMatchLifecycle()
    {
        _endOfGameGeneration++;
        CloseEndOfGame();
        _matchSnapshot = null;
        _observedMatchSnapshot = null;
        _lastFinishedSession = null;
        _lastMatchFinishedAt = null;
        _lastMatchExpired = false;
        _finishedMatches.Clear();
        Current.RefreshLastMatchState();
    }
}
