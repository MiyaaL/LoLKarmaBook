using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// Read-only "再次相遇" summary shown in the end-of-match window. It is built
/// from the pinned match roster, so it remains available after the client has
/// left the post-game phase. Nothing here can write: ignoring a reminder from the
/// window only hides it for the current match (see <see cref="IgnoreCommand"/>).
/// </summary>
public sealed class EndOfGameReminderViewModel
{
    private readonly Action<EndOfGameReminderViewModel> _ignore;

    public EndOfGameReminderViewModel(Reminder reminder, Action<EndOfGameReminderViewModel> ignore)
    {
        Reminder = reminder;
        _ignore = ignore;
        IgnoreCommand = new RelayCommand(() => _ignore(this));
    }

    public Reminder Reminder { get; }

    public string Name => Reminder.CurrentDisplayId ?? "（当前名字未提供）";

    public string Side => NotePresenter.SideLabel(Reminder.Side);

    public string CategoriesText => NotePresenter.CategoryList(Reminder.ActiveCategories);

    public string MostRecent => "最近记录：" + NotePresenter.LocalDate(Reminder.MostRecentEventTime);

    public string SessionCountText => $"涉及 {Reminder.DistinctSessionCount} 局";

    /// <summary>Dates and categories only; stored notes keep no historical names.</summary>
    public string NotesSummary => string.Join(
        Environment.NewLine,
        Reminder.Notes.Select(n =>
            $"{NotePresenter.LocalDate(n.EventTime)} · {NotePresenter.CategoryList(n.Categories)}"));

    public RelayCommand IgnoreCommand { get; }
}
