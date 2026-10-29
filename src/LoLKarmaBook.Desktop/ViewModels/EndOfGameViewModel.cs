using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// End-of-match window state. The window exists only when the user asked to mark
/// someone or this match met known players; otherwise a finished match produces no
/// window and the app is invisible during play. Never writes on its own: a note is
/// saved only through <see cref="SaveAsync"/> after the user picks a participant
/// and at least one category.
/// </summary>
public sealed class EndOfGameViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private MatchRosterSnapshot _snapshot;
    private readonly NotebookService _notebook;
    private readonly string _profileId;
    private EndOfGamePlayerViewModel? _selectedPlayer;
    private CategoryChoiceViewModel? _selectedCategory;
    private readonly ObservableCollection<TagSuggestionViewModel> _tagSuggestions = new();
    private readonly ObservableCollection<TagChipViewModel> _tags = new();
    private string _tagQuery = string.Empty;
    private string _body = string.Empty;
    private string _message = string.Empty;
    private string _messageSeverity = "info";

    public EndOfGameViewModel(MainViewModel main, MatchRosterSnapshot snapshot, NotebookService notebook)
    {
        _main = main;
        _snapshot = snapshot;
        _profileId = main.CurrentProfileId;

        Players = new ObservableCollection<EndOfGamePlayerViewModel>(snapshot.Session.Participants
            .Select(p => new EndOfGamePlayerViewModel(p, snapshot.LocalSessionId))
            .Where(p => p.CanReceiveNote));

        Categories = new ObservableCollection<CategoryChoiceViewModel>(
            NoteCategories.All.Select(c => new CategoryChoiceViewModel(c)));

        Reminders = new ObservableCollection<EndOfGameReminderViewModel>(snapshot.Reminders
            .Select(r => new EndOfGameReminderViewModel(r, Ignore)));

        ToggleCategoryCommand = new RelayCommand<CategoryChoiceViewModel>(toggle);
        DismissCommand = new RelayCommand(_main.CloseEndOfGame);
        AddTagCommand = new RelayCommand<TagSuggestionViewModel>(AddTag);
        RemoveTagCommand = new RelayCommand<TagChipViewModel>(RemoveTag);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave, ex => _main.ShowError(ex, "保存赛后记录失败"));
        foreach (CategoryChoiceViewModel category in Categories)
            category.PropertyChanged += OnCategoryChanged;
        _notebook = notebook;
    }

    public ObservableCollection<EndOfGamePlayerViewModel> Players { get; }

    public ObservableCollection<CategoryChoiceViewModel> Categories { get; }

    public ObservableCollection<EndOfGameReminderViewModel> Reminders { get; }

    public ObservableCollection<TagChipViewModel> Tags => _tags;

    public ObservableCollection<TagSuggestionViewModel> TagSuggestions => _tagSuggestions;

    public bool HasTags => _tags.Count > 0;

    public bool HasTagSuggestions => _tagSuggestions.Count > 0;

    public RelayCommand<TagSuggestionViewModel> AddTagCommand { get; }

    public RelayCommand<TagChipViewModel> RemoveTagCommand { get; }

    /// <summary>Same official champion/item search as the main form; folk names included.</summary>
    public string TagQuery
    {
        get => _tagQuery;
        set
        {
            if (SetProperty(ref _tagQuery, value ?? string.Empty))
            {
                RefreshTagSuggestions();
            }
        }
    }

    private void RefreshTagSuggestions()
    {
        _tagSuggestions.Clear();
        try
        {
            string query = _tagQuery.Trim();
            if (query.Length > 0)
            {
                var selected = _tags.Select(chip => (chip.Tag.Kind, chip.Tag.RefId)).ToHashSet();
                foreach (TagEntry entry in _main.Tags.Search(query, 12))
                {
                    if (!selected.Contains((entry.Kind, entry.Id)))
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
    }

    private void AddTag(TagSuggestionViewModel? suggestion)
    {
        if (suggestion is null)
        {
            return;
        }

        TagEntry entry = suggestion.Entry;
        if (_tags.Any(chip => chip.Tag.Kind == entry.Kind && chip.Tag.RefId == entry.Id))
        {
            return;
        }

        _tags.Add(new TagChipViewModel(new NoteTag(entry.Kind, entry.Id, entry.OfficialName), RemoveTag));
        OnPropertyChanged(nameof(HasTags));
        TagQuery = string.Empty;
    }

    private void RemoveTag(TagChipViewModel? chip)
    {
        if (chip is not null && _tags.Remove(chip))
        {
            OnPropertyChanged(nameof(HasTags));
        }
    }

    public bool HasReminders => Reminders.Count > 0;

    public string RemindersHeading => Reminders.Count == 0
        ? "本局没有再次遇到已记录的玩家。"
        : $"本局遇到 {Reminders.Count} 个有既往记录的账号。";

    public bool HasPlayers => Players.Count > 0;

    public string PlayersHeading => HasPlayers
        ? "本局玩家（选择一位并勾选行为）"
        : "本局名单不可用，无法赛后标记：客户端已离开赛后阶段，或名单中的玩家没有稳定身份。";

    /// <summary>
    /// The client hides some names during the match and reveals them once it is over,
    /// so an open card adopts the newest finished observation when it shows more.
    /// </summary>
    public bool RefreshRoster(MatchSession session)
    {
        bool sameMatch = _snapshot.IsSameMatch(session)
            || string.Equals(_snapshot.LocalSessionId, session.LocalSessionId, StringComparison.Ordinal);
        if (!sameMatch || !RosterChanged(_snapshot.Session, session))
        {
            return false;
        }

        string? selectedKey = _selectedPlayer?.Participant.ParticipantKey;
        _snapshot = _snapshot.Refresh(session);
        SelectedPlayer = null;
        Players.Clear();
        foreach (EndOfGamePlayerViewModel player in session.Participants
                     .Select(participant => new EndOfGamePlayerViewModel(participant, session.LocalSessionId))
                     .Where(player => player.CanReceiveNote))
        {
            Players.Add(player);
        }

        if (selectedKey is not null)
        {
            SelectedPlayer = Players.FirstOrDefault(player => player.Participant.ParticipantKey == selectedKey);
        }

        OnPropertyChanged(nameof(HasPlayers));
        OnPropertyChanged(nameof(PlayersHeading));
        return true;
    }

    private static bool RosterChanged(MatchSession before, MatchSession after)
    {
        if (before.Participants.Count != after.Participants.Count)
        {
            return true;
        }

        var previous = before.Participants.ToDictionary(participant => participant.ParticipantKey, StringComparer.Ordinal);
        foreach (Participant participant in after.Participants)
        {
            if (!previous.TryGetValue(participant.ParticipantKey, out Participant? old))
            {
                return true;
            }

            if (old.Visibility != participant.Visibility
                || !string.Equals(old.CurrentDisplayId, participant.CurrentDisplayId, StringComparison.Ordinal)
                || !string.Equals(old.Champion, participant.Champion, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public EndOfGamePlayerViewModel? SelectedPlayer
    {
        get => _selectedPlayer;
        set
        {
            if (SetProperty(ref _selectedPlayer, value))
            {
                ClearMessage();
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public CategoryChoiceViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                SaveCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string Body
    {
        get => _body;
        set => SetProperty(ref _body, value ?? string.Empty);
    }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value)) OnPropertyChanged(nameof(HasMessage));
        }
    }

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public string MessageSeverity
    {
        get => _messageSeverity;
        private set => SetProperty(ref _messageSeverity, value);
    }

    /// <summary>Raised after a note was written, so the main window can refresh.</summary>
    public event EventHandler? DraftSaved;

    public string Summary =>
        $"对局于 {_snapshot.Session.ObservedAt.ToLocalTime():yyyy-MM-dd HH:mm} 结束" +
        (_snapshot.Session.QueueId is { } queue ? $" · 队列 {queue}" : string.Empty);

    public RelayCommand<CategoryChoiceViewModel> ToggleCategoryCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand DismissCommand { get; }

    public IReadOnlyList<NoteCategory> SelectedCategories =>
        Categories.Where(c => c.IsSelected).Select(c => c.Category).ToArray();

    private bool CanSave => ReferenceEquals(_main.EndOfGame, this) && _main.CurrentProfileId == _profileId
        && _main.IsProfileReady && _main.HasMatchSnapshot && _selectedPlayer is not null && Categories.Any(c => c.IsSelected);

    internal void NotifyClosed() => SaveCommand.RaiseCanExecuteChanged();

    private void OnCategoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CategoryChoiceViewModel.IsSelected))
            SaveCommand.RaiseCanExecuteChanged();
    }

    private void toggle(CategoryChoiceViewModel? choice)
    {
        if (choice is null) return;
        choice.IsSelected = !choice.IsSelected;
        SaveCommand.RaiseCanExecuteChanged();
    }

    private async Task SaveAsync()
    {
        if (!CanSave) return;
        if (_selectedPlayer is not { } player || SelectedCategories is not { Count: > 0 } categories) return;

        if (_main.CurrentProfileId != _profileId)
        {
            SetMessage("该记录属于其他账号空间；请先切换到对应账号再保存。", "error");
            return;
        }

        try
        {
            using MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync();
            if (!ReferenceEquals(_main.EndOfGame, this) || !ReferenceEquals(lease.Notebook, _notebook))
            {
                SetMessage("笔记空间已切换，本次记录未保存。", "error");
                return;
            }

            StoredNote saved = await lease.Notebook.SaveNoteAsync(
                _snapshot.Session,
                player.Participant.ParticipantKey,
                new NoteDraft(
                    Guid.NewGuid().ToString("N"),
                    categories,
                    string.IsNullOrWhiteSpace(_body) ? null : _body,
                    _snapshot.Session.ObservedAt,
                    _main.PersistedSettings.DefaultReminderDays,
                    _tags.Select(chip => chip.Tag).ToArray()));

            // Another participant may be marked from the same window.
            foreach (CategoryChoiceViewModel choice in Categories) choice.IsSelected = false;
            _body = string.Empty;
            OnPropertyChanged(nameof(Body));
            if (_tags.Count > 0)
            {
                _tags.Clear();
                OnPropertyChanged(nameof(HasTags));
            }

            TagQuery = string.Empty;
            SaveCommand.RaiseCanExecuteChanged();
            SetMessage($"已记录 {player.DisplayId}：{NotePresenter.CategoryList(saved.Categories.Select(c => c.Category))}。",
                "success");
            DraftSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            SetMessage("保存失败：" + StatusMessageViewModel.Describe(ex), "error");
        }
    }

    private void Ignore(EndOfGameReminderViewModel reminder)
    {
        // The window only hides matching cards for itself; no receipt is written,
        // so the in-app reminder state is untouched.
        Reminders.Remove(reminder);
        OnPropertyChanged(nameof(HasReminders));
        OnPropertyChanged(nameof(RemindersHeading));
    }

    private void SetMessage(string text, string severity)
    {
        Message = text;
        MessageSeverity = severity;
    }

    private void ClearMessage()
    {
        if (HasMessage) Message = string.Empty;
    }
}

/// <summary>One roster entry the user may mark after the match.</summary>
public sealed class EndOfGamePlayerViewModel
{
    public EndOfGamePlayerViewModel(Participant participant, string localSessionId)
    {
        Participant = participant;
        LocalSessionId = localSessionId;
    }

    public Participant Participant { get; }

    public string LocalSessionId { get; }

    public string DisplayId => Participant.CurrentDisplayId ?? "未知玩家";

    public string SideText => NotePresenter.SideLabel(Participant);

    public string? Champion => Participant.Champion;

    /// <summary>Champion name shown next to the account; "—" when the client did not report one.</summary>
    public string ChampionOrDash => Participant.Champion ?? "—";

    public string IdentityText => NotePresenter.IdentityLabel(Participant);

    /// <summary>Only visible, non-self players with a verified stable identity can be marked.</summary>
    public bool CanReceiveNote =>
        Participant.Visibility == RosterVisibility.Visible
        && !Participant.IsSelf
        && Participant.IdentityStrength == IdentityStrength.VerifiedStable
        && Participant.Identity is not null;

    public string DisabledReason => CanReceiveNote ? string.Empty : Participant switch
    {
        { IsSelf: true } => "本人账号不需要记录。",
        { Visibility: RosterVisibility.Hidden } => "该玩家身份隐藏，无法建立稳定身份。",
        _ => "该玩家没有稳定身份，本应用不会自动匹配。"
    };

    /// <summary>
    /// Tooltip text, or null when there is nothing to explain. Binding an empty
    /// string would still pop an empty tooltip box next to the row.
    /// </summary>
    public string? TooltipText => string.IsNullOrWhiteSpace(DisabledReason) ? null : DisabledReason;

    public bool IsSelf => Participant.IsSelf;
}

/// <summary>Check-box state for one behaviour category (no radio group).</summary>
public sealed class CategoryChoiceViewModel : ObservableObject
{
    private bool _isSelected;

    public CategoryChoiceViewModel(NoteCategory category)
    {
        Category = category;
    }

    public NoteCategory Category { get; }

    public string DisplayName => NoteCategories.GetDisplayName(Category);

    public string Hint => Category switch
    {
        NoteCategory.AfkLeave => "备注：大致时间、持续多久、是否返回",
        NoteCategory.PassivePlay => "备注：具体表现、发生时间",
        NoteCategory.VerbalAbuse => "备注：简述情境，无需复制完整聊天",
        NoteCategory.LowSkill => "备注：英雄、位置、具体操作（仅对当局）",
        NoteCategory.OddBuild => "备注：具体装备及当时情境",
        NoteCategory.RandomChampion => "备注：选了完全不合适的英雄及原因",
        NoteCategory.Vendetta => "备注：针对谁、如何针对、发生时间",
        _ => string.Empty
    };

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
        set => SetProperty(ref _isSelected, value);
    }
}
