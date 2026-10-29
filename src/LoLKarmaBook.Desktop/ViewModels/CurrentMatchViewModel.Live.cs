using System.Collections.ObjectModel;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Desktop.ViewModels;

public sealed partial class CurrentMatchViewModel
{
    private bool _liveUnavailable;
    private string? _liveReminderSessionId;
    private readonly HashSet<Guid> _announcedLivePlayers = new();
    private string DraftContextKey => _current?.SourceKind == SourceKind.Live
        ? "live:" + _current.LocalSessionId : CurrentScenarioKey;

    /// <summary>Stale rosters stay visible for context, but cannot receive new notes or reminders.</summary>
    public void MarkLiveUnavailable(string reason)
    {
        if (_main.IsDemoProfile) return;
        FlushDraftOnClose();
        _liveUnavailable = true;
        Reminders.Clear();
        ReminderSummary = "实时连接中断，已暂停再遇检查。";
        ReminderSummarySeverity = "warning";
        RaiseSessionProperties();
    }

    /// <summary>
    /// True while the last completed live roster is still available for marking.
    /// </summary>
    public bool CanMarkLastMatch => _profileReady && !_main.IsDemoProfile && _main.HasMatchSnapshot;

    public IAsyncCommand MarkLastMatchCommand => _main.MarkLastMatchCommand;

    internal void RefreshLastMatchState()
    {
        OnPropertyChanged(nameof(CanMarkLastMatch));
        MarkLastMatchCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Refreshes immutable observations without replacing the editor or selected participant.</summary>
    public async Task ApplyLiveSnapshotAsync(MatchSession? session)
    {
        if (!_profileReady || _main.IsDemoProfile || IsSaving) return;
        if (session is not null && session.SourceKind != SourceKind.Live)
            throw new ArgumentException("Expected a live observation.", nameof(session));

        _liveUnavailable = false;
        if (session is null)
        {
            if (_current is not null)
            {
                FlushDraftOnClose();
                _autosaveCts?.Cancel();
                _previous = _current;
                _current = null;
                ClearRoster();
                // Keep text in the editor and the protected journal until the next session is selected.
                EndEditing(silent: true);
            }
            Reminders.Clear();
            ReminderSummary = "等待进入选人或游戏，届时自动检测本局玩家。";
            ReminderSummarySeverity = "info";
            RaiseSessionProperties();
            return;
        }

        bool sameSession = _current?.SourceKind == SourceKind.Live
            && (_current.LocalSessionId == session.LocalSessionId
                || (_current.ExternalMatchId is not null && _current.ExternalMatchId == session.ExternalMatchId));
        if (!sameSession)
        {
            FlushDraftOnClose();
            _autosaveCts?.Cancel();
            _previous = _current ?? _previous;
            _current = session;
            _suppressAutosave = true;
            try
            {
                BuildRoster();
                BeginNewNote(autoRestore: false);
                RestoreDraft();
            }
            finally { _suppressAutosave = false; }
        }
        else
        {
            _current = session;
            ReconcileLiveRoster(session);
        }

        if (!sameSession || _liveReminderSessionId is null)
        {
            _liveReminderSessionId = session.LocalSessionId;
            _announcedLivePlayers.Clear();
        }

        // Core persists receipts, including across a process restart or reconnection.
        // The local set also covers LCU gaining an external match id between phases.
        string profileId = _main.CurrentProfileId;
        ReminderEvaluation delivered;
        using (MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
            delivered = await lease.Notebook.EvaluateRemindersAsync(session).ConfigureAwait(true);
        if (!IsCurrentLiveObservation(session, profileId)) return;
        Reminder[] first = delivered.Reminders.Where(r => _announcedLivePlayers.Add(r.PlayerId)).ToArray();
        await EvaluateRemindersAsync().ConfigureAwait(true);
        if (!IsCurrentLiveObservation(session, profileId)) return;
        if (first.Length > 0)
        {
            string[] names = first.Select(r => r.CurrentDisplayId ?? "已记录玩家").ToArray();
            _main.Status.Warn("再次相遇：" + string.Join("、", names) + "，存在本机历史记录。");
            _main.NotifyReminders(names);
        }
        RaiseSessionProperties();
    }

    private bool IsCurrentLiveObservation(MatchSession session, string profileId) => _profileReady
        && !_liveUnavailable && ReferenceEquals(_current, session) && _main.CurrentProfileId == profileId;

    private void ReconcileLiveRoster(MatchSession session)
    {
        var existing = AllRows.ToDictionary(r => r.ParticipantKey, StringComparer.Ordinal);
        RosterRowViewModel[] incoming = session.Participants.Select(p =>
        {
            if (!existing.TryGetValue(p.ParticipantKey, out RosterRowViewModel? row))
                return new RosterRowViewModel(p);
            row.UpdateParticipant(p);
            return row;
        }).ToArray();

        if (_selectedParticipant is not null && !incoming.Contains(_selectedParticipant))
        {
            FlushDraftOnClose();
            _autosaveCts?.Cancel();
            SelectedParticipant = null;
        }
        ReconcileRows(Allies, incoming.Where(r => r.Participant.Side == ParticipantSide.Ally).ToArray());
        ReconcileRows(Enemies, incoming.Where(r => r.Participant.Side == ParticipantSide.Enemy).ToArray());
        ReconcileRows(OtherParticipants, incoming.Where(r => r.Participant.Side == ParticipantSide.Unknown).ToArray());
        OnPropertyChanged(nameof(SelectionHeading));
        OnPropertyChanged(nameof(SelectionSubheading));
        OnPropertyChanged(nameof(CanChooseCategories));
    }

    private static void ReconcileRows(ObservableCollection<RosterRowViewModel> target, RosterRowViewModel[] desired)
    {
        for (int index = target.Count - 1; index >= 0; index--)
            if (!desired.Contains(target[index])) target.RemoveAt(index);
        for (int index = 0; index < desired.Length; index++)
        {
            int oldIndex = target.IndexOf(desired[index]);
            if (oldIndex < 0) target.Insert(index, desired[index]);
            else if (oldIndex != index) target.Move(oldIndex, index);
        }
    }
}
