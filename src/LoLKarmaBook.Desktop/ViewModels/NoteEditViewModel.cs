using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// Edits one stored record on its own, without needing a live match roster. Used
/// from 我的记录 when the record's player is not in the current game.
/// </summary>
public sealed class NoteEditViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly StoredNote _note;
    private readonly ObservableCollection<TagSuggestionViewModel> _tagSuggestions = new();
    private readonly ObservableCollection<TagChipViewModel> _tags = new();
    private string _body;
    private string _reminderDaysText;
    private string _tagQuery = string.Empty;
    private string _statusText = string.Empty;
    private bool _isPermanentReminder;
    private bool _isSaving;
    private bool _saved;

    public NoteEditViewModel(MainViewModel main, StoredNote note)
    {
        _main = main;
        _note = note;
        _body = note.Body;

        // Default to 永久: editing must never quietly shorten an existing reminder.
        _isPermanentReminder = true;
        _reminderDaysText = NoteCategories.DefaultReminderDays.ToString(CultureInfo.InvariantCulture);

        // Created before the chips so selecting them cannot run before the command exists.
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave,
            ex => _main.ShowError(ex, "更新记录失败"));
        AddTagCommand = new RelayCommand<TagSuggestionViewModel>(AddTag);
        RemoveTagCommand = new RelayCommand<TagChipViewModel>(RemoveTag);

        foreach (NoteCategory category in NoteCategories.All)
        {
            var chip = new CategoryChipViewModel(category, (_, _) => RaiseSaveState());
            chip.IsSelected = note.Categories.Any(entry => entry.Category == category);
            Categories.Add(chip);
        }

        foreach (NoteTag tag in note.Tags)
        {
            _tags.Add(new TagChipViewModel(tag, RemoveTag));
        }
    }

    public ObservableCollection<CategoryChipViewModel> Categories { get; } = new();

    public ObservableCollection<TagChipViewModel> Tags => _tags;

    public ObservableCollection<TagSuggestionViewModel> TagSuggestions => _tagSuggestions;

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand<TagSuggestionViewModel> AddTagCommand { get; }

    public RelayCommand<TagChipViewModel> RemoveTagCommand { get; }

    /// <summary>Raised once the store accepted the change.</summary>
    public event EventHandler? Saved;

    public StoredNote Note => _note;

    public string PlayerName => _note.PlayerDisplayName is { Length: > 0 } name ? name : "这条记录";

    public string Heading => "编辑 " + PlayerName;

    public string CreatedText => "创建于 " + NotePresenter.LocalDateTime(_note.CreatedAt);

    public string Body
    {
        get => _body;
        set
        {
            if (SetProperty(ref _body, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(BodyCountText));
            }
        }
    }

    public string BodyCountText => Body.Length + " / 500";

    /// <summary>Typing here searches official champion and item names, folk names included.</summary>
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

    public bool HasTagSuggestions => _tagSuggestions.Count > 0;

    public bool HasTags => _tags.Count > 0;

    public bool IsPermanentReminder
    {
        get => _isPermanentReminder;
        set
        {
            if (SetProperty(ref _isPermanentReminder, value))
            {
                OnPropertyChanged(nameof(CanEditReminderDays));
                RaiseSaveState();
            }
        }
    }

    public bool CanEditReminderDays => !IsPermanentReminder;

    public string ReminderDaysText
    {
        get => _reminderDaysText;
        set
        {
            if (SetProperty(ref _reminderDaysText, value ?? string.Empty))
            {
                RaiseSaveState();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatus));
            }
        }
    }

    public bool HasStatus => StatusText.Length > 0;

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

    private bool CanSave => !IsSaving && Categories.Any(chip => chip.IsSelected)
        && (IsPermanentReminder || TryReadDays(out _));

    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSaving = true;
        try
        {
            int days = IsPermanentReminder || !TryReadDays(out int parsed) ? 0 : parsed;
            using MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true);
            await lease.Notebook.UpdateNoteAsync(
                _note.NoteId,
                _note.Revision,
                new NoteEdit(
                    Categories.Where(chip => chip.IsSelected).Select(chip => chip.Category).ToArray(),
                    string.IsNullOrWhiteSpace(Body) ? null : Body,
                    _note.EventTime,
                    days,
                    _tags.Select(chip => chip.Tag).ToArray())).ConfigureAwait(true);

            _saved = true;
            StatusText = "已保存";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            StatusText = "保存失败：" + StatusMessageViewModel.Describe(ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    public bool WasSaved => _saved;

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
        RaiseSaveState();
    }

    private void RemoveTag(TagChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        if (_tags.Remove(chip))
        {
            OnPropertyChanged(nameof(HasTags));
            RaiseSaveState();
        }
    }

    private bool TryReadDays(out int days)
    {
        if (int.TryParse(ReminderDaysText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            days = Math.Clamp(parsed, 0, 3650);
            return true;
        }

        days = 0;
        return false;
    }

    private void RaiseSaveState()
    {
        SaveCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(BodyCountText));
    }
}
