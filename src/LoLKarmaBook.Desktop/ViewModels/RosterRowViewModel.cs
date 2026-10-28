using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Services;

namespace LoLKarmaBook.Desktop.ViewModels;

/// <summary>
/// One roster line. The binding key is the immutable participant key, never the
/// position or display name, so a re-sorted roster cannot move a pending edit.
/// </summary>
public sealed class RosterRowViewModel : ObservableObject
{
    private string _historyText = "—";
    private bool _hasHistory;
    private string _historyTooltip = string.Empty;
    private bool _isSelected;

    public RosterRowViewModel(Participant participant)
    {
        Participant = participant;
    }

    public Participant Participant { get; private set; }

    public string ParticipantKey => Participant.ParticipantKey;

    public string DisplayName => Participant.Visibility == RosterVisibility.Visible
            ? Participant.CurrentDisplayId ?? "（可见但未提供名字）"
            : "（身份隐藏，未提供名字）";

    public string SideText => NotePresenter.SideLabel(Participant);
    public string ChampionOrDash => Participant.Champion ?? "—";
    public string VisibilityText => NotePresenter.VisibilityLabel(Participant);
    public string IdentityText => NotePresenter.IdentityLabel(Participant);
    public bool IsHidden => Participant.Visibility != RosterVisibility.Visible;
    public bool IsSelf => Participant.IsSelf;
    public bool CanReceiveNote => Participant.Visibility == RosterVisibility.Visible && !Participant.IsSelf;
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
    public string IdentityKindGlyph => Participant.IdentityStrength switch
        {
            IdentityStrength.VerifiedStable => "稳定",
            IdentityStrength.NameOnly => "仅名字",
            _ => "无身份"
        };

    public void UpdateParticipant(Participant participant)
    {
        if (participant.ParticipantKey != ParticipantKey)
            throw new ArgumentException("A roster row cannot change its participant key.", nameof(participant));
        Participant = participant;
        foreach (string property in new[] { nameof(Participant), nameof(DisplayName), nameof(SideText),
                     nameof(ChampionOrDash), nameof(VisibilityText), nameof(IdentityText), nameof(IsHidden),
                     nameof(IsSelf), nameof(CanReceiveNote), nameof(IdentityKindGlyph) })
            OnPropertyChanged(property);
    }

    /// <summary>"言语挑衅、辱骂 · 1 局" or "无记录".</summary>
    public string HistoryText
    {
        get => _historyText;
        private set => SetProperty(ref _historyText, value);
    }

    public bool HasHistory
    {
        get => _hasHistory;
        private set => SetProperty(ref _hasHistory, value);
    }

    public string HistoryTooltip
    {
        get => _historyTooltip;
        private set => SetProperty(ref _historyTooltip, value);
    }

    public void SetHistory(IReadOnlyList<NoteCategory> categories, int distinctSessions)
    {
        if (categories.Count == 0)
        {
            HistoryText = IsSelf ? "本人" : "无记录";
            HasHistory = false;
            HistoryTooltip = string.Empty;
            return;
        }

        HistoryText = $"{NotePresenter.CategoryList(categories)} · {distinctSessions} 局";
        HasHistory = true;
        HistoryTooltip = "该账号存在既往记录。";
    }

    public void SetHistoryUnavailable(string reason)
    {
        HistoryText = "—";
        HasHistory = false;
        HistoryTooltip = reason;
    }
}
