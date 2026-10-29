using System.Windows.Input;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// A stored note as shown in 我的记录. It deliberately has no historical player
/// name: the record is identified by its local note id, categories, date and
/// session reference only. A current name can be supplied separately by callers
/// that have a fresh session, never from storage.
/// </summary>
public sealed class RecordRowViewModel : ObservableObject
{
    private bool _isEditing;
    private string _body;

    public RecordRowViewModel(StoredNote note, RecordRowActions actions)
    {
        Note = note;
        _body = note.Body;
        // A record is identified by the player, so the stored in-game id leads.
        // Older rows predate that field and say so instead of showing a number.
        PlayerName = note.PlayerDisplayName is { Length: > 0 } name ? name : string.Empty;
        EventDate = NotePresenter.LocalDate(note.EventTime);
        EventDateTime = NotePresenter.LocalDateTime(note.EventTime);
        StatusLabel = NotePresenter.StatusLabel(note.Status);
        StatusHint = NotePresenter.StatusHint(note.Status);
        CategoryText = NotePresenter.CategoryList(note.Categories.Select(c => c.Category));
        TagsText = note.Tags.Count == 0
            ? string.Empty
            : string.Join(" ", note.Tags.Select(tag => "#" + tag.OfficialName));
        TagsHint = note.Tags.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, note.Tags.Select(tag => NotePresenter.TagLabel(tag)));
        IReadOnlyList<NoteCategory> activeCategories = note.GetActiveCategories(DateTimeOffset.UtcNow);
        HasExpiredCategories = activeCategories.Count == 0;
        ActiveCategoryText = HasExpiredCategories
            ? "提醒已全部过期（记录仍保留）"
            : NotePresenter.CategoryList(activeCategories);
        IsActive = note.Status == NoteStatus.Active;
        IsDraft = note.Status == NoteStatus.Draft;
        IsArchived = note.Status == NoteStatus.Archived;
        IsRetracted = note.Status == NoteStatus.Retracted;
        IsEditable = note.Status is NoteStatus.Active or NoteStatus.Draft;

        // Buttons stay enabled and explain why an action is refused, so a disabled
        // control never leaves the user guessing. The service still enforces the
        // rules (e.g. status transitions, optimistic revision).
        EditCommand = new AsyncRelayCommand(
            () => actions.EditAsync(this),
            onException: actions.Report);
        EditDisabledReason = IsEditable
            ? string.Empty
            : NotePresenter.DisabledReason("只有生效中或草稿状态的记录可以编辑；已归档/已撤回的记录请先新建。");
        PublishCommand = new AsyncRelayCommand(() => actions.PublishAsync(this), () => IsDraft, onException: actions.Report);
        PublishDisabledReason = IsDraft ? string.Empty : "仅草稿可以发布为生效记录。";
        ArchiveCommand = new AsyncRelayCommand(() => actions.ArchiveAsync(this), () => Note.Status is NoteStatus.Active or NoteStatus.Draft, onException: actions.Report);
        ArchiveDisabledReason = Note.Status is NoteStatus.Active or NoteStatus.Draft
            ? string.Empty
            : "已归档或已撤回的记录不能再归档。";
        RetractCommand = new AsyncRelayCommand(() => actions.RetractAsync(this), () => Note.Status is NoteStatus.Active or NoteStatus.Archived, onException: actions.Report);
        RetractDisabledReason = Note.Status is NoteStatus.Active or NoteStatus.Archived
            ? string.Empty
            : "只有生效中或已归档的记录可以撤回。";
        DeleteCommand = new AsyncRelayCommand(
            () => actions.DeleteAsync(this),
            onException: actions.Report);
    }

    public StoredNote Note { get; }

    public Guid NoteId => Note.NoteId;

    /// <summary>Primary line of a record: the player's in-game id.</summary>
    public string PlayerName { get; }

    public bool HasPlayerName => PlayerName.Length > 0;

    public bool HasExpiredCategories { get; }

    public string EventDate { get; }

    public string EventDateTime { get; }

    public string StatusLabel { get; }

    public string StatusHint { get; }

    public string CategoryText { get; }

    /// <summary>Official champion/item tags, shown after the categories.</summary>
    public string TagsText { get; }

    public bool HasTags => TagsText.Length > 0;

    public string TagsHint { get; }

    public string ActiveCategoryText { get; }

    public bool IsActive { get; }

    /// <summary>
    /// Only states the user still has to act on are labelled: a retracted record is
    /// simply a record whose reminder was withdrawn, so it needs no badge.
    /// </summary>
    public bool ShowStatusBadge => IsDraft || IsArchived;

    public bool IsDraft { get; }

    public bool IsArchived { get; }

    public bool IsRetracted { get; }

    public bool IsEditable { get; }

    public string Body
    {
        get => _body;
        private set
        {
            if (SetProperty(ref _body, value))
            {
                OnPropertyChanged(nameof(HasBody));
            }
        }
    }

    public bool HasBody => !string.IsNullOrWhiteSpace(Body);

    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    public string CreatedText => "创建于 " + NotePresenter.LocalDateTime(Note.CreatedAt);

    public string UpdatedText => Note.UpdatedAt > Note.CreatedAt
        ? "修改于 " + NotePresenter.LocalDateTime(Note.UpdatedAt)
        : string.Empty;

    public bool HasUpdated => Note.UpdatedAt > Note.CreatedAt;

    public string RevisionText => $"版本 {Note.Revision}";

    public IAsyncCommand EditCommand { get; }

    public IAsyncCommand PublishCommand { get; }

    public IAsyncCommand ArchiveCommand { get; }

    public IAsyncCommand RetractCommand { get; }

    public IAsyncCommand DeleteCommand { get; }

    public string EditDisabledReason { get; }

    public string PublishDisabledReason { get; }

    public string ArchiveDisabledReason { get; }

    public string RetractDisabledReason { get; }

    public void UpdateBody(string body) => Body = body;

    public sealed class RecordRowActions
    {
        public required Func<RecordRowViewModel, Task> EditAsync { get; init; }

        public required Func<RecordRowViewModel, Task> PublishAsync { get; init; }

        public required Func<RecordRowViewModel, Task> ArchiveAsync { get; init; }

        public required Func<RecordRowViewModel, Task> RetractAsync { get; init; }

        public required Func<RecordRowViewModel, Task> DeleteAsync { get; init; }

        public required Action<Exception> Report { get; init; }
    }
}
