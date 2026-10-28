using System.Collections.ObjectModel;
using System.Windows.Input;
using System.IO;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;
using LoLKarmaBook.Storage;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// 当前对局: synthetic A/B selection, roster with previous local tags, the five
/// category multi-select and an editable note. Saving never claims success until
/// the transaction committed.
/// </summary>
public sealed partial class CurrentMatchViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SyntheticScenario _scenario = SyntheticScenario.CreateDefault();
    private readonly ObservableCollection<RosterRowViewModel> _rows = new();
    private readonly Dictionary<NoteCategory, bool> _categorySelection = new();
    private readonly ObservableCollection<TagChipViewModel> _tags = new();
    private readonly ObservableCollection<TagSuggestionViewModel> _tagSuggestions = new();
    private MatchSession? _current;
    private MatchSession? _previous;
    private long _nextSequence = 1;
    private RosterRowViewModel? _selectedParticipant;
    private string _noteBody = string.Empty;
    private string _tagQuery = string.Empty;
    private DateTime? _eventDate;
    private string _eventTimeText = string.Empty;
    private int _reminderDays = NoteCategories.DefaultReminderDays;
    private StoredNote? _editingNote;
    private string _autosaveStatus = "自动保存：尚未有草稿";
    private bool _isAutosaveError;
    private bool _isSaving;
    private bool _suppressAutosave;
    private string _reminderSummary = "选择玩家并至少勾选一个类别后即可保存。";
    private string _reminderSummarySeverity = "info";
    private string _recoveredDraftNotice = string.Empty;
    private bool _recoveredDraftIsError;
    private Guid _pendingRequestId = Guid.NewGuid();
    private bool _profileReady;

    public CurrentMatchViewModel(MainViewModel main)
    {
        _main = main;

        foreach (NoteCategory category in NoteCategories.All)
        {
            _categorySelection[category] = false;
            Categories.Add(new CategoryChipViewModel(category, OnCategoryChanged));
        }

        RowSelectedCommand = new RelayCommand<RosterRowViewModel>(row =>
        {
            if (row is not null)
            {
                SelectedParticipant = row;
            }
        });
        ScenarioACommand = new AsyncRelayCommand(() => LoadScenarioAsync("a"));
        ScenarioBCommand = new AsyncRelayCommand(() => LoadScenarioAsync("b"));
        NewNoteCommand = new RelayCommand(BeginNewNote);
        CancelEditCommand = new RelayCommand(CancelEdit);
        SaveCommand = new AsyncRelayCommand(SaveAsync, CanSave);
        ResetReminderDaysCommand = new RelayCommand(() =>
        {
            ReminderDays = NoteCategories.DefaultReminderDays;
        });
        UseEventNowCommand = new RelayCommand(() =>
        {
            DateTime now = DateTime.Now;
            EventDate = now.Date;
            EventTimeText = now.ToString("HH:mm");
        });
        DismissAutosaveCommand = new RelayCommand(() =>
        {
            AutosaveStatus = "自动保存：已清除错误提示（草稿文件可能仍不可写）";
            IsAutosaveError = false;
        });
        AddTagCommand = new RelayCommand<TagSuggestionViewModel>(suggestion =>
        {
            if (suggestion is not null)
            {
                AddTag(suggestion.Entry);
            }
        });
        AcceptTopTagCommand = new RelayCommand(AcceptTopTag, () => _tagSuggestions.Count > 0);
        RemoveTagCommand = new RelayCommand<TagChipViewModel>(chip =>
        {
            if (chip is not null)
            {
                RemoveTag(chip);
            }
        });
    }

    public ObservableCollection<CategoryChipViewModel> Categories { get; } = new();

    public ObservableCollection<RosterRowViewModel> Allies { get; } = new();

    public ObservableCollection<RosterRowViewModel> Enemies { get; } = new();

    public ObservableCollection<RosterRowViewModel> OtherParticipants { get; } = new();

    public ObservableCollection<ReminderCardViewModel> Reminders { get; } = new();

    public MatchSession? Current => _current;

    public MatchSession? Previous => _previous;

    public bool HasSession => _current is not null;

    public string SessionTitle => _current switch
    {
        null => IsDemoMode ? "未选择模拟对局" : "等待对局",
        _ when _current.SourceKind == SourceKind.Live => "实时对局 · " + NotePresenter.GamePhaseLabel(_current.Phase)
            + (_liveUnavailable ? "（连接已中断）" : string.Empty),
        _ when _current.LocalSessionId == SyntheticScenario.MatchALocalSessionId => "模拟对局 A · 上一局对局（已结束）",
        _ => "模拟对局 B · 本局对局（再遇检查）"
    };

    public string SessionMeta
    {
        get
        {
            if (_current is null)
            {
                return IsDemoMode ? "选择模拟对局 A 或 B。" : "进入选人或游戏后自动显示名单。";
            }

            MatchSession s = _current;
            string roster = s.RosterComplete ? "名单完整" : "名单不完整";
            return $"{NotePresenter.SourceKindLabel(s.SourceKind)} · {NotePresenter.GamePhaseLabel(s.Phase)} · "
                   + $"队列 {s.QueueId?.ToString() ?? "未知"} · {roster} · 观察于 {NotePresenter.LocalDateTime(s.ObservedAt)}";
        }
    }

    public string PreviousMeta => _previous is null
        ? "无上一局记录"
        : $"{NotePresenter.GamePhaseLabel(_previous.Phase)} · {NotePresenter.LocalDateTime(_previous.ObservedAt)}";

    public string CurrentScenarioKey => _current?.LocalSessionId switch
    {
        SyntheticScenario.MatchALocalSessionId => "a",
        SyntheticScenario.MatchBLocalSessionId => "b",
        _ => string.Empty
    };

    public string ScenarioDescription => CurrentScenarioKey switch
    {
        "a" => "对局 A：为演示玩家X 保存记录后切到对局 B。",
        "b" => "对局 B：演示玩家X 已改名；同名不同身份的玩家Y 不应提示。",
        _ => IsDemoMode ? "先加载对局 A 保存记录，再加载对局 B。"
            : _liveUnavailable ? "连接中断，草稿已保留。"
            : string.Empty
    };

    public bool IsDemoMode => _main.IsDemoProfile;

    public bool IsScenarioA => CurrentScenarioKey == "a";

    public bool IsScenarioB => CurrentScenarioKey == "b";

    public RosterRowViewModel? SelectedParticipant
    {
        get => _selectedParticipant;
        set
        {
            if (ReferenceEquals(_selectedParticipant, value))
            {
                return;
            }

            SetSelectedParticipantVisual(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedParticipant));
            OnPropertyChanged(nameof(SelectionHeading));
            OnPropertyChanged(nameof(SelectionSubheading));
            OnPropertyChanged(nameof(CanChooseCategories));
            if (_editingNote is not null)
            {
                // Editing is bound to a specific note; changing player abandons the edit.
                EndEditing(silent: true);
            }

            RaiseSaveState();
            QueueAutosave();
        }
    }

    public bool HasSelectedParticipant => _selectedParticipant is not null;

    // Keep the visual selection in sync even when restoring a draft without
    // invoking the editor-changing behavior of the public selection setter.
    private void SetSelectedParticipantVisual(RosterRowViewModel? value)
    {
        if (_selectedParticipant is not null) _selectedParticipant.IsSelected = false;
        _selectedParticipant = value;
        if (_selectedParticipant is not null) _selectedParticipant.IsSelected = true;
    }

    public bool CanChooseCategories => !_liveUnavailable && _selectedParticipant?.CanReceiveNote == true;

    public string SelectionHeading => _selectedParticipant is null
        ? "未选择玩家"
        : _selectedParticipant.DisplayName;

    public string SelectionSubheading => _selectedParticipant is null
        ? "在左侧名单中选择一名玩家。"
        : $"{_selectedParticipant.SideText}"
          + (_selectedParticipant.IsSelf ? " · 不能为自己保存记录" : string.Empty);

    public string NoteBody
    {
        get => _noteBody;
        set
        {
            if (SetProperty(ref _noteBody, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(NoteBodyCount));
                OnPropertyChanged(nameof(NoteBodyCountText));
                RaiseSaveState();
                QueueAutosave();
            }
        }
    }

    public int NoteBodyCount => _noteBody.Length;

    public string NoteBodyCountText => $"{NoteBodyCount} / 500";

    /// <summary>Official champion/item tags attached to the record being edited.</summary>
    public ObservableCollection<TagChipViewModel> Tags => _tags;

    public bool HasTags => _tags.Count > 0;

    /// <summary>What the user is typing in the tag search box.</summary>
    public string TagQuery
    {
        get => _tagQuery;
        set
        {
            if (SetProperty(ref _tagQuery, value ?? string.Empty))
            {
                RefreshTagSuggestions();
                QueueAutosave();
            }
        }
    }

    public ObservableCollection<TagSuggestionViewModel> TagSuggestions => _tagSuggestions;

    public bool HasTagSuggestions => _tagSuggestions.Count > 0;

    public string TagCatalogHint
    {
        get
        {
            (int champions, int items) = _main.Tags.Counts;
            return $"只接受官方英雄/装备：英雄 {champions} 个，装备 {items} 件；可搜民间叫法、拼音首字母或英文名。";
        }
    }

    public DateTime? EventDate
    {
        get => _eventDate;
        set
        {
            if (SetProperty(ref _eventDate, value))
            {
                OnPropertyChanged(nameof(EventTimePreview));
                QueueAutosave();
            }
        }
    }

    public string EventTimeText
    {
        get => _eventTimeText;
        set
        {
            if (SetProperty(ref _eventTimeText, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(EventTimePreview));
                OnPropertyChanged(nameof(HasEventTimeError));
                QueueAutosave();
            }
        }
    }

    public string EventTimePreview
    {
        get
        {
            if (TryResolveEventTime(out DateTimeOffset value, out string? error))
            {
                return "事件时间：" + NotePresenter.LocalDateTime(value);
            }

            return error ?? "事件时间：将使用保存时刻";
        }
    }

    public bool HasEventTimeError => _eventTimeText.Trim().Length > 0 && !TryResolveEventTime(out _, out _);

    public int ReminderDays
    {
        get => _reminderDays;
        set
        {
            if (value != _reminderDays)
            {
                int clamped = Math.Clamp(value, 0, 3650);
                if (SetProperty(ref _reminderDays, clamped))
                {
                    OnPropertyChanged(nameof(ReminderDaysHint));
                    QueueAutosave();
                }
            }
        }
    }

    public string ReminderDaysHint =>
        ReminderDays == 0
            ? "永久：始终参与再遇提醒。"
            : $"到期约 {NotePresenter.LocalDate(DateTimeOffset.UtcNow.AddDays(ReminderDays))}。";

    public string SelectedCategoriesText
    {
        get
        {
            NoteCategory[] selected = SelectedCategories().ToArray();
            return selected.Length == 0 ? "未选择类别" : NotePresenter.CategoryList(selected);
        }
    }

    public string AutosaveStatus
    {
        get => _autosaveStatus;
        private set => SetProperty(ref _autosaveStatus, value);
    }

    public bool IsAutosaveError
    {
        get => _isAutosaveError;
        private set => SetProperty(ref _isAutosaveError, value);
    }

    public string ReminderSummary
    {
        get => _reminderSummary;
        private set => SetProperty(ref _reminderSummary, value);
    }

    public string ReminderSummarySeverity
    {
        get => _reminderSummarySeverity;
        private set => SetProperty(ref _reminderSummarySeverity, value);
    }

    public string RecoveredDraftNotice
    {
        get => _recoveredDraftNotice;
        private set
        {
            if (SetProperty(ref _recoveredDraftNotice, value))
            {
                OnPropertyChanged(nameof(HasRecoveredDraftNotice));
            }
        }
    }

    public bool HasRecoveredDraftNotice => !string.IsNullOrEmpty(RecoveredDraftNotice);

    public bool RecoveredDraftIsError
    {
        get => _recoveredDraftIsError;
        private set => SetProperty(ref _recoveredDraftIsError, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value))
            {
                RaiseSaveState();
            }
        }
    }

    public bool IsEditing => _editingNote is not null;

    public string SaveButtonText => IsEditing ? "保存修改" : "保存记录";

    public bool HasReminders => Reminders.Count > 0;

    public string ReminderIntro => CurrentScenarioKey switch
    {
        "" => _liveUnavailable ? "连接中断。" : string.Empty,
        "a" => "对局 A 不显示再遇提醒。",
        _ when Reminders.Count == 0 => "对局 B：没有找到同一稳定身份的既往记录。",
        _ => string.Empty
    };

    public ICommand RowSelectedCommand { get; }

    public IAsyncCommand ScenarioACommand { get; }

    public IAsyncCommand ScenarioBCommand { get; }

    public ICommand NewNoteCommand { get; }

    public ICommand CancelEditCommand { get; }

    public IAsyncCommand SaveCommand { get; }

    public ICommand ResetReminderDaysCommand { get; }

    public ICommand UseEventNowCommand { get; }

    public ICommand DismissAutosaveCommand { get; }

    public ICommand AddTagCommand { get; }

    public RelayCommand AcceptTopTagCommand { get; }

    public ICommand RemoveTagCommand { get; }

    public async Task LoadAsync()
    {
        if (!_main.IsProfileReady)
        {
            return;
        }

        _profileReady = true;
        _liveUnavailable = false;
        _suppressAutosave = true;
        try
        {
            SessionStore.State state = _main.SessionStore.LoadState();
            _nextSequence = state.Sequence + 1;
            _current = IsDemoMode ? state.Current : null;
            _previous = IsDemoMode ? state.Previous : null;
            BuildRoster();

            ReminderDays = NoteCategories.DefaultReminderDays;
            BeginNewNote(autoRestore: false);
            RestoreDraft();
            await EvaluateRemindersAsync().ConfigureAwait(true);
        }
        finally
        {
            _suppressAutosave = false;
        }

        RaiseSessionProperties();
    }

    public void PrepareForProfileSwitch()
    {
        FlushDraftOnClose();
        _autosaveCts?.Cancel();
        _profileReady = false;
        _liveUnavailable = false;
        _announcedLivePlayers.Clear();
        _liveReminderSessionId = null;
        _current = null;
        _previous = null;
        ClearRoster();
        Reminders.Clear();
        _editingNote = null;
        _suppressAutosave = true;
        try
        {
            // Drop the editor state, but keep the pending request id so a retry after
            // the space reopens still replays the same request idempotently.
            foreach (CategoryChipViewModel chip in Categories)
            {
                chip.IsSelected = false;
            }

            NoteBody = string.Empty;
            EventDate = null;
            EventTimeText = string.Empty;
            ReminderDays = _main.PersistedSettings.DefaultReminderDays;
            SetTags(null);
            RecoveredDraftNotice = string.Empty;
            RecoveredDraftIsError = false;
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(SaveButtonText));
        }
        finally
        {
            _suppressAutosave = false;
        }

        RaiseSessionProperties();
        OnPropertyChanged(nameof(HasReminders));
    }

    public void CancelPending()
    {
        _profileReady = false;
        _main.InvalidateEndOfGame();
    }

    public async Task RefreshAsync()
    {
        if (!_profileReady)
        {
            return;
        }

        await EvaluateRemindersAsync().ConfigureAwait(true);
    }

    private async Task LoadScenarioAsync(string key)
    {
        if (!_main.IsProfileReady)
        {
            _main.Status.Error("尚未打开笔记空间，无法加载模拟对局。");
            return;
        }

        try
        {
            long sequence = ++_nextSequence;
            MatchSession session = key == "a"
                ? SessionStore.BuildMatchA(_scenario, sequence)
                : SessionStore.BuildMatchB(_scenario, sequence);

            // Explicit re-selection is a deliberate "new observation": keep the
            // other snapshot as previous and treat this one as current.
            _previous = _current;
            _current = session;
            SaveSessionState(key);

            _suppressAutosave = true;
            try
            {
                BuildRoster();
                BeginNewNote(autoRestore: false);
                RestoreDraft();
                await EvaluateRemindersAsync().ConfigureAwait(true);
            }
            finally
            {
                _suppressAutosave = false;
            }

            RaiseSessionProperties();
            _main.Status.Show(
                key == "a" ? "已加载模拟对局 A（虚构数据）。" : "已加载模拟对局 B（虚构数据），已检查再遇记录。",
                StatusSeverity.Info);
        }
        catch (Exception ex)
        {
            _main.ShowError(ex, "加载模拟对局失败");
        }
    }

    private void SaveSessionState(string scenarioKey)
    {
        try
        {
            _main.SessionStore.Save(new SessionStore.State
            {
                CurrentScenario = scenarioKey,
                Sequence = _nextSequence,
                LastSwitchedAt = DateTimeOffset.UtcNow,
                Current = _current,
                Previous = _previous
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _main.Log.Warn("sessions", "Save failed", ex);
            AutosaveStatus = "会话记录保存失败：重启后将回到上次成功保存的对局。";
            IsAutosaveError = true;
        }
    }

    private void BuildRoster()
    {
        ClearRoster();
        foreach (Participant participant in _current?.Participants ?? Array.Empty<Participant>())
        {
            var row = new RosterRowViewModel(participant);
            switch (participant.Side)
            {
                case ParticipantSide.Ally:
                    Allies.Add(row);
                    break;
                case ParticipantSide.Enemy:
                    Enemies.Add(row);
                    break;
                default:
                    OtherParticipants.Add(row);
                    break;
            }
        }
    }

    private void ClearRoster()
    {
        Allies.Clear();
        Enemies.Clear();
        OtherParticipants.Clear();
        SetSelectedParticipantVisual(null);
        OnPropertyChanged(nameof(SelectedParticipant));
        OnPropertyChanged(nameof(HasSelectedParticipant));
        OnPropertyChanged(nameof(SelectionHeading));
        OnPropertyChanged(nameof(SelectionSubheading));
        OnPropertyChanged(nameof(CanChooseCategories));
    }

    private async Task EvaluateRemindersAsync()
    {
        Reminders.Clear();
        if (_liveUnavailable)
        {
            ReminderSummary = "实时连接中断，已暂停再遇检查。";
            ReminderSummarySeverity = "warning";
            OnPropertyChanged(nameof(HasReminders));
            OnPropertyChanged(nameof(ReminderIntro));
            return;
        }
        if (_current is null)
        {
            ReminderSummary = "尚未加载对局。";
            ReminderSummarySeverity = "info";
            OnPropertyChanged(nameof(HasReminders));
            OnPropertyChanged(nameof(ReminderIntro));
            return;
        }

        try
        {
            // FindRemindersAsync is a pure preview and never consumes receipts.
            // The lease pins this profile for the whole read.
            MatchSession observed = _current;
            string profileId = _main.CurrentProfileId;
            IReadOnlyList<Reminder> reminders;
            using (MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
            {
                reminders = await lease.Notebook
                    .FindRemindersAsync(observed)
                    .ConfigureAwait(true);
            }

            if (!_profileReady || _liveUnavailable || !ReferenceEquals(_current, observed)
                || _main.CurrentProfileId != profileId) return;

            var byKey = new Dictionary<string, Reminder>(StringComparer.Ordinal);
            foreach (Reminder reminder in reminders)
            {
                byKey[reminder.ParticipantKey] = reminder;
                Reminders.Add(new ReminderCardViewModel(reminder, IgnoreForSessionAsync));
            }

            foreach (RosterRowViewModel row in AllRows)
            {
                if (byKey.TryGetValue(row.ParticipantKey, out Reminder? reminder))
                {
                    row.SetHistory(reminder.ActiveCategories, reminder.DistinctSessionCount);
                }
                else if (row.Participant.IdentityStrength == IdentityStrength.VerifiedStable)
                {
                    row.SetHistory(Array.Empty<NoteCategory>(), 0);
                }
                else
                {
                    row.SetHistoryUnavailable(row.IsSelf
                        ? "本人账号不参与提醒。"
                        : "该玩家没有稳定身份，本应用不会自动识别或匹配历史。");
                }
            }

            ReminderSummary = reminders.Count == 0
                ? "没有匹配到既往记录。"
                : $"找到 {reminders.Count} 个有既往记录的账号。";
            ReminderSummarySeverity = reminders.Count == 0 ? "info" : "warning";
        }
        catch (Exception ex)
        {
            _main.ShowError(ex, "检查再遇记录失败");
            ReminderSummary = "再遇检查失败，请看顶部错误提示。";
            ReminderSummarySeverity = "error";
        }

        OnPropertyChanged(nameof(HasReminders));
        OnPropertyChanged(nameof(ReminderIntro));
    }

    private async Task IgnoreForSessionAsync(ReminderCardViewModel card)
    {
        if (_current is null)
        {
            return;
        }

        try
        {
            bool saved;
            using (MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
            {
                saved = await lease.Notebook
                    .IgnoreForSessionAsync(_current, card.Reminder.Identity)
                    .ConfigureAwait(true);
            }
            if (saved)
            {
                Reminders.Remove(card);
                OnPropertyChanged(nameof(HasReminders));
                OnPropertyChanged(nameof(ReminderIntro));
                _main.Status.Success("已在本次对局中忽略该提醒；记录本身不会被删除。");
                await EvaluateRemindersAsync().ConfigureAwait(true);
            }
            else
            {
                _main.Status.Warn("无法记录本次忽略：本地库中没有该账号的既往记录。");
            }
        }
        catch (Exception ex)
        {
            _main.ShowError(ex, "保存本局忽略失败");
        }
    }

    private IEnumerable<RosterRowViewModel> AllRows =>
        Allies.Concat(Enemies).Concat(OtherParticipants);

    public void BeginNewNote() => BeginNewNote(autoRestore: true);

    private void BeginNewNote(bool autoRestore)
    {
        EndEditing(silent: true);
        _suppressAutosave = true;
        try
        {
            foreach (CategoryChipViewModel chip in Categories)
            {
                chip.IsSelected = false;
            }

            NoteBody = string.Empty;
            EventDate = DateTime.Now.Date;
            EventTimeText = DateTime.Now.ToString("HH:mm");
            ReminderDays = _main.PersistedSettings.DefaultReminderDays;
            SetTags(null);
            // A new record is a different payload: new idempotency id.
            _pendingRequestId = Guid.NewGuid();
        }
        finally
        {
            _suppressAutosave = false;
        }

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonText));
        RaiseSaveState();
        QueueAutosave();
        if (autoRestore)
        {
            // Choosing "新记录" is an explicit reset: drop the old draft file.
            try
            {
                _main.Autosave.Clear(DraftContextKey);
                AutosaveStatus = "自动保存：新记录，尚未有草稿";
                IsAutosaveError = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AutosaveStatus = "自动保存清理失败：" + StatusMessageViewModel.Describe(ex);
                IsAutosaveError = true;
            }
        }
    }

    /// <summary>
    /// Starts editing a stored note in the current roster. Resolution is async
    /// because it uses the store's identity lookup; the edit only proceeds when the
    /// note's player id maps to exactly one verified-stable participant here.
    /// </summary>
    public async Task<bool> BeginEditAsync(StoredNote note)
    {
        if (_current is null || note.PlayerId is not { } playerId)
        {
            _main.Status.Warn("这条记录没有可匹配的稳定身份，无法在当前名单中打开编辑。");
            return false;
        }

        RosterRowViewModel? match = null;
        using (MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
        {
            foreach (RosterRowViewModel candidate in AllRows)
            {
                if (candidate.Participant.IdentityStrength != IdentityStrength.VerifiedStable
                    || candidate.Participant.Identity is not { } identity)
                {
                    continue;
                }

                Guid? known = await lease.Workspace.Store
                    .FindPlayerIdAsync(identity)
                    .ConfigureAwait(true);
                if (known == playerId)
                {
                    match = candidate;
                    break;
                }
            }
        }

        if (match is null)
        {
            _main.Status.Warn("当前名单中找不到这条记录对应的玩家。请先加载包含该玩家的模拟对局再编辑。");
            return false;
        }

        RosterRowViewModel row = match;
        _suppressAutosave = true;
        try
        {
            _editingNote = note;
            SelectedParticipant = row;
            HashSet<NoteCategory> selected = note.Categories.Select(c => c.Category).ToHashSet();
            foreach (CategoryChipViewModel chip in Categories)
            {
                chip.IsSelected = selected.Contains(chip.Category);
            }

            NoteBody = note.Body;
            DateTime local = note.EventTime.ToLocalTime().DateTime;
            EventDate = local.Date;
            EventTimeText = local.ToString("HH:mm");
            ReminderDays = DeriveReminderDays(note);
            SetTags(note.Tags);
        }
        finally
        {
            _suppressAutosave = false;
        }

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonText));
        RaiseSaveState();
        return true;
    }

    private static int DeriveReminderDays(StoredNote note)
    {
        DateTimeOffset latest = note.Categories.Max(c => c.ReminderExpiresAt);
        int days = (int)Math.Round((latest - note.UpdatedAt).TotalDays);
        return Math.Clamp(days, 0, 3650);
    }

    private void EndEditing(bool silent)
    {
        _editingNote = null;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonText));
        if (!silent)
        {
            BeginNewNote(autoRestore: false);
        }
    }

    private void CancelEdit()
    {
        if (!IsEditing)
        {
            return;
        }

        _editingNote = null;
        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonText));
        _main.Status.Show("已取消编辑，未写入任何修改。", StatusSeverity.Info);
        RestoreDraft();
    }

    private IEnumerable<NoteCategory> SelectedCategories() =>
        NoteCategories.All.Where(c => _categorySelection[c]);

    private NoteTag[] SelectedTags() => _tags.Select(chip => chip.Tag).ToArray();

    private void RefreshTagSuggestions()
    {
        _tagSuggestions.Clear();
        try
        {
            string query = _tagQuery.Trim();
            if (query.Length > 0)
            {
                var alreadySelected = _tags
                    .Select(chip => (chip.Tag.Kind, chip.Tag.RefId))
                    .ToHashSet();
                foreach (TagEntry entry in _main.Tags.Search(query, 12))
                {
                    if (!alreadySelected.Contains((entry.Kind, entry.Id)))
                    {
                        _tagSuggestions.Add(new TagSuggestionViewModel(entry));
                    }

                    if (_tagSuggestions.Count >= 8)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _main.Log.Warn("tags", "Tag suggestion failed", ex);
        }

        OnPropertyChanged(nameof(HasTagSuggestions));
        OnPropertyChanged(nameof(TagCatalogHint));
        AcceptTopTagCommand.RaiseCanExecuteChanged();
    }

    private void AcceptTopTag()
    {
        if (_tagSuggestions.FirstOrDefault() is { } first)
        {
            AddTag(first.Entry);
        }
    }

    private void AddTag(TagEntry entry)
    {
        if (_tags.Any(chip => chip.Tag.Kind == entry.Kind && chip.Tag.RefId == entry.Id))
        {
            return;
        }

        _tags.Add(new TagChipViewModel(
            new NoteTag(entry.Kind, entry.Id, entry.OfficialName),
            RemoveTag));
        OnPropertyChanged(nameof(HasTags));
        TagQuery = string.Empty;
        RaiseSaveState();
        QueueAutosave();
    }

    private void RemoveTag(TagChipViewModel chip)
    {
        if (_tags.Remove(chip))
        {
            OnPropertyChanged(nameof(HasTags));
            RaiseSaveState();
            QueueAutosave();
        }
    }

    /// <summary>Replaces the editor's tags, ignoring malformed draft values.</summary>
    private void SetTags(IEnumerable<NoteTag>? tags)
    {
        _tags.Clear();
        foreach (NoteTag tag in tags ?? Array.Empty<NoteTag>())
        {
            if (!Enum.IsDefined(tag.Kind) || tag.RefId <= 0 || string.IsNullOrWhiteSpace(tag.OfficialName))
            {
                continue;
            }

            if (_tags.Any(chip => chip.Tag.Kind == tag.Kind && chip.Tag.RefId == tag.RefId))
            {
                continue;
            }

            _tags.Add(new TagChipViewModel(tag, RemoveTag));
        }

        OnPropertyChanged(nameof(HasTags));
        RefreshTagSuggestions();
    }

    private void OnCategoryChanged(NoteCategory category, bool isSelected)
    {
        _categorySelection[category] = isSelected;
        OnPropertyChanged(nameof(SelectedCategoriesText));
        RaiseSaveState();
        QueueAutosave();
    }

    private bool CanSave() =>
        _profileReady
        && !_liveUnavailable
        && !IsSaving
        && _current is not null
        && _selectedParticipant?.CanReceiveNote == true
        && SelectedCategories().Any()
        && NoteBody.Length <= 500
        && !HasEventTimeError;

    /// <summary>Re-evaluates the save command; also called by the shell.</summary>
    public void RaiseSaveState()
    {
        OnPropertyChanged(nameof(SelectedCategoriesText));
        SaveCommand.RaiseCanExecuteChanged();
    }

    private bool TryResolveEventTime(out DateTimeOffset value, out string? error)
    {
        value = default;
        error = null;
        DateTime date = (EventDate ?? DateTime.Now).Date;
        string text = EventTimeText.Trim();
        if (text.Length == 0)
        {
            value = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date));
            return true;
        }

        if (!TimeSpan.TryParseExact(text.Replace('：', ':'), new[] { @"h\:mm", @"hh\:mm" }, System.Globalization.CultureInfo.InvariantCulture, out TimeSpan time)
            || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
        {
            error = "事件时间格式应为 HH:mm（例如 18:40）。";
            return false;
        }

        DateTime local = date.Add(time);
        try
        {
            value = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        }
        catch (ArgumentException)
        {
            error = "事件时间无效。";
            return false;
        }

        if (value > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            error = "事件时间不能明显晚于当前时间。";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Saves the open note. The request id stays the same until a save actually
    /// commits (or the user genuinely starts a different record), so retrying after
    /// an uncertain failure replays the same request instead of duplicating it.
    /// </summary>
    private async Task SaveAsync()
    {
        if (_current is null || _selectedParticipant is null)
        {
            return;
        }

        MatchSession session = _current;
        NoteCategory[] categories = SelectedCategories().ToArray();
        if (categories.Length == 0)
        {
            _main.Status.Error("请至少选择一个类别。");
            return;
        }

        if (!TryResolveEventTime(out DateTimeOffset eventTime, out string? timeError))
        {
            _main.Status.Error(timeError ?? "事件时间无效。");
            return;
        }

        RosterRowViewModel row = _selectedParticipant;
        StoredNote? editing = _editingNote;
        IsSaving = true;
        RaiseSaveState();
        try
        {
            string participantKey = row.ParticipantKey;
            if (editing is not null)
            {
                NoteEdit edit = new(categories, NoteBody, eventTime, ReminderDays, SelectedTags());
                StoredNote updated;
                using (MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
                {
                    updated = await lease.Notebook
                        .UpdateNoteAsync(editing.NoteId, editing.Revision, edit)
                        .ConfigureAwait(true);
                }

                FinishSave(updated, row, created: false);
            }
            else
            {
                string requestId = _pendingRequestId.ToString("N");
                var draft = new NoteDraft(requestId, categories, NoteBody, eventTime, ReminderDays, SelectedTags());
                // Write the pending id before the commit so a restart after an
                // uncertain result still retries the same request.
                SaveDraftNow();
                StoredNote saved;
                using (MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
                {
                    saved = await lease.Notebook
                        .SaveNoteAsync(session, participantKey, draft)
                        .ConfigureAwait(true);
                }

                // Only rotate after the commit is known to have succeeded.
                if (string.Equals(_pendingRequestId.ToString("N"), requestId, StringComparison.Ordinal))
                {
                    _pendingRequestId = Guid.NewGuid();
                }

                FinishSave(saved, row, created: true);
            }
        }
        catch (Exception ex)
        {
            // Keep all input and keep the same request id for a retry.
            _main.ShowError(ex, "保存记录失败（输入已保留，可重试；不会重复创建记录）");
            AutosaveStatus = "自动保存：保存失败，草稿仍保留在输入框中";
            IsAutosaveError = true;
        }
        finally
        {
            IsSaving = false;
            RaiseSaveState();
        }
    }

    private void FinishSave(StoredNote note, RosterRowViewModel row, bool created)
    {
        string action = created ? "已保存记录" : "已保存修改";
        _main.Status.Success($"{action}：{row.DisplayName} · "
                             + $"{NotePresenter.CategoryList(note.Categories.Select(c => c.Category))}。");

        _suppressAutosave = true;
        try
        {
            _editingNote = null;
            foreach (CategoryChipViewModel chip in Categories)
            {
                chip.IsSelected = false;
            }

            NoteBody = string.Empty;
            _pendingRequestId = Guid.NewGuid();
            SetTags(null);
        }
        finally
        {
            _suppressAutosave = false;
        }

        OnPropertyChanged(nameof(IsEditing));
        OnPropertyChanged(nameof(SaveButtonText));
        RaiseSaveState();
        try
        {
            _main.Autosave.Clear(DraftContextKey);
            AutosaveStatus = "自动保存：记录已提交，草稿已清除";
            IsAutosaveError = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutosaveStatus = "自动保存清理失败：" + StatusMessageViewModel.Describe(ex);
            IsAutosaveError = true;
        }

        _ = RefreshRemindersAfterSaveAsync();
    }

    private async Task RefreshRemindersAfterSaveAsync()
    {
        try
        {
            await EvaluateRemindersAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _main.ShowError(ex, "刷新再遇状态失败");
        }
    }

    private void RaiseSessionProperties()
    {
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(Previous));
        OnPropertyChanged(nameof(HasSession));
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(SessionMeta));
        OnPropertyChanged(nameof(PreviousMeta));
        OnPropertyChanged(nameof(CurrentScenarioKey));
        OnPropertyChanged(nameof(ScenarioDescription));
        OnPropertyChanged(nameof(IsScenarioA));
        OnPropertyChanged(nameof(IsScenarioB));
        OnPropertyChanged(nameof(IsDemoMode));
        OnPropertyChanged(nameof(CanChooseCategories));
        OnPropertyChanged(nameof(HasReminders));
        OnPropertyChanged(nameof(ReminderIntro));
        RaiseSaveState();
    }

    // ---------------- Autosave ----------------

    private CancellationTokenSource? _autosaveCts;

    private void QueueAutosave()
    {
        if (_suppressAutosave || !_profileReady || !HasSelectedParticipant)
        {
            return;
        }

        _autosaveCts?.Cancel();
        _autosaveCts?.Dispose();
        var cts = new CancellationTokenSource();
        _autosaveCts = cts;
        _ = AutosaveAfterDelayAsync(cts.Token);
    }

    private async Task AutosaveAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(700, token).ConfigureAwait(true);
            if (token.IsCancellationRequested)
            {
                return;
            }

            SaveDraftNow();
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer input.
        }
    }

    private void SaveDraftNow()
    {
        if (!_profileReady || _selectedParticipant is null)
        {
            return;
        }

        try
        {
            _main.Autosave.Save(new AutosaveJournal.Draft
            {
                Scenario = DraftContextKey,
                ParticipantKey = _selectedParticipant.ParticipantKey,
                Categories = SelectedCategories().Select(NoteCategories.GetCode).ToList(),
                Body = NoteBody,
                Tags = SelectedTags()
                    .Select(tag => new AutosaveJournal.DraftTag
                    {
                        Kind = (int)tag.Kind,
                        RefId = tag.RefId,
                        Name = tag.OfficialName
                    })
                    .ToList(),
                EventTimeUnixSeconds = TryResolveEventTime(out DateTimeOffset value, out _)
                    ? value.ToUnixTimeSeconds()
                    : null,
                ReminderDays = ReminderDays,
                SavedAt = DateTimeOffset.UtcNow,
                EditingNoteId = _editingNote?.NoteId,
                PendingRequestId = _pendingRequestId.ToString("N")
            });
            AutosaveStatus = "自动保存：草稿已写入本机（DPAPI 保护） · "
                             + DateTime.Now.ToString("HH:mm:ss");
            IsAutosaveError = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException
                                       or System.Security.Cryptography.CryptographicException)
        {
            _main.Log.Warn("autosave", "Draft write failed", ex);
            _main.Status.Warn("草稿未能写入本机：" + StatusMessageViewModel.Describe(ex)
                              + "。输入仍保留在此窗口，但关闭程序会丢失未保存内容。");
            AutosaveStatus = "自动保存失败：" + StatusMessageViewModel.Describe(ex)
                             + "。输入仍保留在此窗口，但关闭程序会丢失未保存内容。";
            IsAutosaveError = true;
        }
    }

    /// <summary>Called during a safe window close: flush the draft synchronously.</summary>
    public void FlushDraftOnClose()
    {
        if (!_suppressAutosave && _profileReady && HasSelectedParticipant)
        {
            SaveDraftNow();
        }
    }

    private void RestoreDraft()
    {
        RecoveredDraftNotice = string.Empty;
        AutosaveJournal.Draft? draft;
        try
        {
            draft = _main.Autosave.Load(DraftContextKey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AutosaveStatus = "自动保存读取失败：" + StatusMessageViewModel.Describe(ex);
            _main.Status.Warn("读取上次草稿失败：" + StatusMessageViewModel.Describe(ex));
            IsAutosaveError = true;
            return;
        }

        if (draft is null || string.IsNullOrEmpty(draft.ParticipantKey))
        {
            return;
        }

        if (!string.Equals(draft.Scenario, DraftContextKey, StringComparison.Ordinal))
        {
            return;
        }

        RosterRowViewModel? row = AllRows.FirstOrDefault(r =>
            string.Equals(r.ParticipantKey, draft.ParticipantKey, StringComparison.Ordinal) && r.CanReceiveNote);
        if (row is null)
        {
            if (draft.Categories.Count > 0 || !string.IsNullOrWhiteSpace(draft.Body))
            {
                RecoveredDraftNotice = "发现一份自动保存的草稿，但它对应的玩家不在当前名单中，未自动载入。";
                RecoveredDraftIsError = false;
            }

            return;
        }

        _suppressAutosave = true;
        try
        {
            SetSelectedParticipantVisual(row);
            OnPropertyChanged(nameof(SelectedParticipant));
            OnPropertyChanged(nameof(HasSelectedParticipant));
            OnPropertyChanged(nameof(SelectionHeading));
            OnPropertyChanged(nameof(SelectionSubheading));
            OnPropertyChanged(nameof(CanChooseCategories));

            foreach (CategoryChipViewModel chip in Categories)
            {
                chip.IsSelected = draft.Categories.Contains(NoteCategories.GetCode(chip.Category), StringComparer.Ordinal);
            }

            NoteBody = draft.Body ?? string.Empty;
            SetTags(draft.Tags?
                .Where(tag => Enum.IsDefined((TagKind)tag.Kind))
                .Select(tag => new NoteTag((TagKind)tag.Kind, tag.RefId, tag.Name ?? string.Empty)));
            if (draft.EventTimeUnixSeconds is { } seconds)
            {
                DateTime local = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime().DateTime;
                EventDate = local.Date;
                EventTimeText = local.ToString("HH:mm");
            }
            else
            {
                EventDate = DateTime.Now.Date;
                EventTimeText = DateTime.Now.ToString("HH:mm");
            }

            ReminderDays = Math.Clamp(draft.ReminderDays, 0, 3650);
            if (Guid.TryParseExact(draft.PendingRequestId, "N", out Guid pending))
            {
                // Same idempotency key as the interrupted save.
                _pendingRequestId = pending;
            }
        }
        finally
        {
            _suppressAutosave = false;
        }

        RecoveredDraftNotice = $"已恢复 {NotePresenter.LocalDateTime(draft.SavedAt)} 自动保存的草稿，提交保存前不会影响提醒。";
        RecoveredDraftIsError = false;
        AutosaveStatus = "自动保存：已恢复上次草稿";
        IsAutosaveError = false;
        RaiseSaveState();
    }
}

/// <summary>One candidate in the tag search box; selecting it stores the official entry.</summary>
public sealed class TagSuggestionViewModel
{
    public TagSuggestionViewModel(TagEntry entry)
    {
        Entry = entry;
    }

    public TagEntry Entry { get; }

    public string DisplayName => Entry.OfficialName;

    public string KindLabel => Entry.Kind == TagKind.Champion ? "英雄" : "装备";

    public string KindBadge => Entry.Kind == TagKind.Champion ? "英" : "装";

    public string AutomationName => KindLabel + "：" + DisplayName;
}

/// <summary>A selected official tag shown as a removable chip under the note body.</summary>
public sealed class TagChipViewModel
{
    public TagChipViewModel(NoteTag tag, Action<TagChipViewModel> remove)
    {
        Tag = tag;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public NoteTag Tag { get; }

    public string DisplayName => Tag.OfficialName;

    public string KindLabel => Tag.Kind == TagKind.Champion ? "英雄" : "装备";

    public string RemoveLabel => "移除 " + DisplayName;

    public ICommand RemoveCommand { get; }
}

public sealed class CategoryChipViewModel : ObservableObject{
    private readonly Action<NoteCategory, bool> _onChanged;
    private bool _isSelected;

    public CategoryChipViewModel(NoteCategory category, Action<NoteCategory, bool> onChanged)
    {
        Category = category;
        _onChanged = onChanged;
        DisplayName = NoteCategories.GetDisplayName(category);
        Code = NoteCategories.GetCode(category);
        Hint = category switch
        {
            NoteCategory.AfkLeave => "建议备注：大致时间、持续多久、是否返回",
            NoteCategory.PassivePlay => "建议备注：具体表现、发生时间",
            NoteCategory.VerbalAbuse => "建议备注：简述情境，无需复制完整聊天",
            NoteCategory.LowSkill => "建议备注：英雄、位置、具体操作（仅对当局）",
            NoteCategory.OddBuild => "建议备注：具体装备及当时情境",
            _ => string.Empty
        };
    }

    public NoteCategory Category { get; }

    public string DisplayName { get; }

    public string Code { get; }

    public string Hint { get; }

    public string IconKey => Category switch
    {
        NoteCategory.AfkLeave => "Icon.AfkLeave",
        NoteCategory.PassivePlay => "Icon.PassivePlay",
        NoteCategory.VerbalAbuse => "Icon.VerbalAbuse",
        NoteCategory.LowSkill => "Icon.LowSkill",
        _ => "Icon.OddBuild"
    };

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                _onChanged(Category, value);
            }
        }
    }
}

public sealed class ReminderCardViewModel
{
    public ReminderCardViewModel(Reminder reminder, Func<ReminderCardViewModel, Task> ignore)
    {
        Reminder = reminder;
        IgnoreCommand = new AsyncRelayCommand(() => ignore(this), () => IsIgnorable,
            ex => { });
    }

    public Reminder Reminder { get; }

    public string Name => Reminder.CurrentDisplayId ?? "（当前名字未提供）";

    public string Side => NotePresenter.SideLabel(Reminder.Side);

    public string CategoriesText => NotePresenter.CategoryList(Reminder.ActiveCategories);

    /// <summary>Summary only: dates, categories and session count. No old names.</summary>
    public string NotesSummary => string.Join(
        Environment.NewLine,
        Reminder.Notes.Select(n =>
            $"{NotePresenter.LocalDate(n.EventTime)} · {NotePresenter.CategoryList(n.Categories)}"));

    public string SessionCountText => $"涉及 {Reminder.DistinctSessionCount} 局";

    public string MostRecent => "最近记录：" + NotePresenter.LocalDate(Reminder.MostRecentEventTime);

    public bool IsIgnorable => true;

    public string IgnoreDisabledReason => "不可用：当前没有可忽略的提醒。";

    public AsyncRelayCommand IgnoreCommand { get; }
}
