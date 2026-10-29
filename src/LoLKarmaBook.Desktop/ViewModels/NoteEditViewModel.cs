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
    private string _body;
    private string _reminderDaysText;
    private string _statusText = string.Empty;
    private bool _isSaving;
    private bool _saved;

    public NoteEditViewModel(MainViewModel main, StoredNote note, Func<DateTimeOffset>? now = null)
    {
        _main = main;
        _note = note;
        _body = note.Body;
        _reminderDaysText = RemainingDays(note, (now ?? (() => DateTimeOffset.UtcNow))())
            .ToString(CultureInfo.InvariantCulture);

        // Created before the chips so selecting them cannot run before the command exists.
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave,
            ex => _main.ShowError(ex, "更新记录失败"));

        foreach (NoteCategory category in NoteCategories.All)
        {
            var chip = new CategoryChipViewModel(category, (_, _) => RaiseSaveState());
            chip.IsSelected = note.Categories.Any(entry => entry.Category == category);
            Categories.Add(chip);
        }
    }

    public ObservableCollection<CategoryChipViewModel> Categories { get; } = new();

    public AsyncRelayCommand SaveCommand { get; }

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

    private bool CanSave => !IsSaving && Categories.Any(chip => chip.IsSelected) && TryReadDays(out _);

    private async Task SaveAsync()
    {
        if (!CanSave)
        {
            return;
        }

        IsSaving = true;
        try
        {
            TryReadDays(out int days);
            using MainViewModel.WorkspaceLease lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true);
            await lease.Notebook.UpdateNoteAsync(
                _note.NoteId,
                _note.Revision,
                new NoteEdit(
                    Categories.Where(chip => chip.IsSelected).Select(chip => chip.Category).ToArray(),
                    string.IsNullOrWhiteSpace(Body) ? null : Body,
                    _note.EventTime,
                    days,
                    _note.Tags)).ConfigureAwait(true);

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

    private bool TryReadDays(out int days)
    {
        if (int.TryParse(ReminderDaysText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            days = Math.Clamp(parsed, 1, 3650);
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

    /// <summary>Days still left on the note's longest reminder, so editing does not silently shorten it.</summary>
    private static int RemainingDays(StoredNote note, DateTimeOffset now)
    {
        DateTimeOffset latest = note.Categories.Count == 0
            ? now.AddDays(NoteCategories.DefaultReminderDays)
            : note.Categories.Max(entry => entry.ReminderExpiresAt);
        double days = Math.Ceiling((latest - now).TotalDays);
        return days <= 0 ? 1 : (int)Math.Clamp(days, 1, 3650);
    }
}
