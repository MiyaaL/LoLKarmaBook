using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// 我的记录: list/filter by status, category and date, with archive/retract/
/// delete/edit. Stored notes carry no display names, so the list shows local note
/// ids and categories instead of historic names.
/// </summary>
public sealed class RecordsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly ObservableCollection<RecordRowViewModel> _records = new();
    private readonly RecordRowViewModel.RecordRowActions _actions;
    private bool _hasLoadedOnce;
    private bool _isLoading;
    private bool _profileReady;
    private string _statusMessage = string.Empty;
    private FilterOption<NoteStatus?> _selectedStatus;
    private FilterOption<NoteCategory?> _selectedCategory;
    private FilterOption<string> _selectedRange;
    private int _totalCount;

    public RecordsViewModel(MainViewModel main)
    {
        _main = main;
        _actions = new RecordRowViewModel.RecordRowActions
        {
            EditAsync = EditAsync,
            PublishAsync = PublishAsync,
            ArchiveAsync = ArchiveAsync,
            RetractAsync = RetractAsync,
            DeleteAsync = DeleteAsync,
            Report = ex => _main.ShowError(ex, "更新记录失败")
        };

        StatusOptions = new[]
        {
            new FilterOption<NoteStatus?>(null, "全部状态"),
            new FilterOption<NoteStatus?>(NoteStatus.Active, "生效中"),
            new FilterOption<NoteStatus?>(NoteStatus.Draft, "草稿"),
            new FilterOption<NoteStatus?>(NoteStatus.Archived, "已归档"),
            new FilterOption<NoteStatus?>(NoteStatus.Retracted, "已撤回")
        };

        CategoryOptions = new[]
        {
            new FilterOption<NoteCategory?>(null, "全部类别"),
            new FilterOption<NoteCategory?>(NoteCategory.AfkLeave, "挂机逃跑"),
            new FilterOption<NoteCategory?>(NoteCategory.PassivePlay, "消极游戏"),
            new FilterOption<NoteCategory?>(NoteCategory.VerbalAbuse, "言语挑衅、辱骂"),
            new FilterOption<NoteCategory?>(NoteCategory.LowSkill, "游戏技术水平较低"),
            new FilterOption<NoteCategory?>(NoteCategory.OddBuild, "乱出装")
        };

        RangeOptions = new[]
        {
            new FilterOption<string>(null, "全部时间"),
            new FilterOption<string>("365", "最近 365 天"),
            new FilterOption<string>("30", "最近 30 天"),
            new FilterOption<string>("7", "最近 7 天")
        };

        _selectedStatus = StatusOptions[0];
        _selectedCategory = CategoryOptions[0];
        _selectedRange = RangeOptions[0];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => _profileReady && !_isLoading);
        OpenDemoCommand = new AsyncRelayCommand(OpenSyntheticDemoAsync, () => _profileReady && !IsDemoProfile);
        ClearFiltersCommand = new RelayCommand(ClearFilters);
    }

    public ObservableCollection<RecordRowViewModel> Records => _records;

    public IReadOnlyList<FilterOption<NoteStatus?>> StatusOptions { get; }

    public IReadOnlyList<FilterOption<NoteCategory?>> CategoryOptions { get; }

    public IReadOnlyList<FilterOption<string>> RangeOptions { get; }

    public FilterOption<NoteStatus?> SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public FilterOption<NoteCategory?> SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public FilterOption<string> SelectedRange
    {
        get => _selectedRange;
        set
        {
            if (SetProperty(ref _selectedRange, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public bool IsDemoProfile => _main.IsDemoProfile;

    public string ProfileTitle => _main.CurrentProfileTitle;

    public string ProfileHint => _main.IsDemoProfile
        ? "当前空间仅用于模拟演示，记录与默认空间完全分离。"
        : "当前空间保存你自己的手工记录；切换空间后历史不会混用。";

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                RaiseRefresh();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetProperty(ref _totalCount, value))
            {
                OnPropertyChanged(nameof(CountText));
            }
        }
    }

    public string CountText => _isLoading
        ? "正在读取…"
        : $"共 {TotalCount} 条记录";

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrEmpty(StatusMessage);

    public bool HasRecords => _records.Count > 0;

    public string EmptyHint => _main.IsDemoProfile
        ? "演示空间里还没有记录。到「当前对局」加载模拟对局 A，为演示玩家X 保存一条记录。"
        : "还没有记录。到「当前对局」加载模拟对局并保存你的第一条个人观察。";

    public IAsyncCommand RefreshCommand { get; }

    public IAsyncCommand OpenDemoCommand { get; }

    public ICommand ClearFiltersCommand { get; }

    public async Task LoadAsync()
    {
        _profileReady = true;
        _hasLoadedOnce = true;
        OnPropertyChanged(nameof(IsDemoProfile));
        OnPropertyChanged(nameof(ProfileTitle));
        OnPropertyChanged(nameof(ProfileHint));
        OnPropertyChanged(nameof(EmptyHint));
        RaiseRefresh();
        await RefreshAsync().ConfigureAwait(true);
    }

    public void PrepareForProfileSwitch()
    {
        _profileReady = false;
        _records.Clear();
        TotalCount = 0;
        OnPropertyChanged(nameof(HasRecords));
        RaiseRefresh();
    }

    public async Task RefreshAsync()
    {
        if (!_profileReady || _main.IsProfileReady is false)
        {
            return;
        }

        IsLoading = true;
        try
        {
            NoteQuery query = BuildQuery();
            using IDisposable lease = await _main.AcquireWorkspaceAsync().ConfigureAwait(true);
            IReadOnlyList<StoredNote> notes = await _main.Workspace.Notebook
                .ListNotesAsync(query)
                .ConfigureAwait(true);

            _records.Clear();
            foreach (StoredNote note in notes)
            {
                _records.Add(new RecordRowViewModel(note, _actions));
            }

            TotalCount = notes.Count;
            StatusMessage = string.Empty;
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(CountText));
        }
        catch (Exception ex)
        {
            StatusMessage = "读取记录失败：" + StatusMessageViewModel.Describe(ex);
            _main.Log.Error("records", "Load failed", ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasRecords));
            OnPropertyChanged(nameof(CountText));
        }
    }

    private NoteQuery BuildQuery()
    {
        DateTimeOffset? from = null;
        if (_selectedRange?.Value is { } daysText
            && int.TryParse(daysText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int days))
        {
            from = DateTimeOffset.UtcNow.AddDays(-days);
        }

        return new NoteQuery
        {
            Status = _selectedStatus?.Value,
            Categories = _selectedCategory?.Value is { } category
                ? new[] { category }
                : null,
            EventFrom = from,
            EventTo = null
        };
    }

    private void ClearFilters()
    {
        _selectedStatus = StatusOptions[0];
        _selectedCategory = CategoryOptions[0];
        _selectedRange = RangeOptions[0];
        OnPropertyChanged(nameof(SelectedStatus));
        OnPropertyChanged(nameof(SelectedCategory));
        OnPropertyChanged(nameof(SelectedRange));
        _ = RefreshAsync();
    }

    private Task OpenSyntheticDemoAsync()
    {
        ProfileDescriptor? demo = _main.Profiles.FirstOrDefault(p => p.Id == ProfileCatalog.DemoProfileId);
        if (demo is null)
        {
            _main.Status.Error("找不到内置的演示空间。");
            return Task.CompletedTask;
        }

        _main.SelectedProfile = demo;
        return Task.CompletedTask;
    }

    private async Task EditAsync(RecordRowViewModel row)
    {
        // The edit form lives on 当前对局 because a note must be bound to a real
        // participant row (never to a name).
        bool opened = await _main.Current.BeginEditAsync(row.Note).ConfigureAwait(true);
        if (opened)
        {
            _main.SelectTab("current");
            _main.Status.Show(
                "已在「当前对局」打开这条记录的编辑表单。注意：编辑会更新事件时间；如需保留原时间请勿修改。",
                StatusSeverity.Info);
        }
    }

    private async Task PublishAsync(RecordRowViewModel row) =>
        await UpdateStatusAsync(row, "发布草稿", n => n.PublishDraftAsync(row.NoteId, row.Note.Revision))
            .ConfigureAwait(true);

    private async Task ArchiveAsync(RecordRowViewModel row) =>
        await UpdateStatusAsync(row, "归档记录", n => n.ArchiveNoteAsync(row.NoteId, row.Note.Revision))
            .ConfigureAwait(true);

    private async Task RetractAsync(RecordRowViewModel row)
    {
        bool confirmed = Confirmation.Ask(
            "撤回这条记录？",
            "撤回后记录仍保留在列表中便于核对，但不会再产生再次相遇提醒。记录不会因此删除。",
            "撤回");
        if (!confirmed)
        {
            return;
        }

        await UpdateStatusAsync(row, "撤回记录", n => n.RetractNoteAsync(row.NoteId, row.Note.Revision))
            .ConfigureAwait(true);
    }

    private async Task DeleteAsync(RecordRowViewModel row)
    {
        bool confirmed = Confirmation.Ask(
            "永久删除这条记录？",
            "删除会移除记录、类别与关联的修改日志，且无法在本应用中撤销。"
            + "若只是想停止提醒，请改用「归档」或「撤回」。",
            "永久删除");
        if (!confirmed)
        {
            return;
        }

        try
        {
            bool deleted;
            using (await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
            {
                deleted = await _main.Workspace.Notebook.DeleteNoteAsync(row.NoteId).ConfigureAwait(true);
            }

            _main.Status.Show(
                deleted ? "记录已删除。" : "记录不存在，可能已被删除。",
                deleted ? StatusSeverity.Success : StatusSeverity.Warning);
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _main.ShowError(ex, "删除记录失败");
        }
    }

    private async Task UpdateStatusAsync(
        RecordRowViewModel row,
        string action,
        Func<NotebookService, Task<StoredNote>> operation)
    {
        try
        {
            StoredNote updated;
            // The lease pins the workspace the operation runs against, so a profile
            // switch cannot redirect the write into a different database.
            using (await _main.AcquireWorkspaceAsync().ConfigureAwait(true))
            {
                updated = await operation(_main.Workspace.Notebook).ConfigureAwait(true);
            }

            _main.Status.Success($"{action}成功：{NotePresenter.StatusLabel(updated.Status)}。");
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _main.ShowError(ex, action + "失败（可能是记录已被其他窗口修改）");
        }
    }

    /// <summary>Re-evaluates the refresh button (open space, first load).</summary>
    public void RaiseRefresh() => RefreshCommand.RaiseCanExecuteChanged();

    /// <summary>Used by 设置 after restore/clear so the list reflects reality.</summary>
    public async Task ReloadAfterMaintenanceAsync()
    {
        if (_hasLoadedOnce && _profileReady)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }
}

/// <summary>Modal yes/no used for irreversible actions.</summary>
public static class Confirmation
{
    /// <summary>
    /// Uses the app-provided confirmation hook when available (WPF MessageBox);
    /// otherwise refuses, so a missing host can never silently delete data.
    /// </summary>
    public static Func<string, string, string, bool>? Handler { get; set; }

    public static bool Ask(string title, string message, string confirmLabel) =>
        Handler?.Invoke(title, message, confirmLabel) ?? false;
}
