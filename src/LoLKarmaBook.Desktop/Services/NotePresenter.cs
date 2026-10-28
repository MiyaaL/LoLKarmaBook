using System.Globalization;
using LoLKarmaBook.Core;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>
/// Chinese presentation helpers shared by pages. They never invent a display
/// name: stored notes carry no name history, so callers must supply any current
/// name explicitly (and only when it is known to be current).
/// </summary>
public static class NotePresenter
{
    public static string StatusLabel(NoteStatus status) => status switch
    {
        NoteStatus.Draft => "草稿",
        NoteStatus.Active => "生效中",
        NoteStatus.Archived => "已归档",
        NoteStatus.Retracted => "已撤回",
        _ => "未知"
    };

    public static string StatusHint(NoteStatus status) => status switch
    {
        NoteStatus.Draft => "草稿不会参与再次相遇提醒。",
        NoteStatus.Active => "在有效期内，同账号在演示对局中再遇时会提示。",
        NoteStatus.Archived => "已归档：保留记录，但不再提醒。",
        NoteStatus.Retracted => "已撤回：保留记录便于核对，但不再提醒。",
        _ => string.Empty
    };

    public static string SideLabel(ParticipantSide side) => side switch
    {
        ParticipantSide.Ally => "队友",
        ParticipantSide.Enemy => "对手",
        _ => "未知"
    };

    public static string SideLabel(Participant participant) => participant switch
    {
        { IsSelf: true } => "本人",
        _ => SideLabel(participant.Side)
    };

    public static string VisibilityLabel(Participant participant) => participant.Visibility switch
    {
        RosterVisibility.Visible => "身份可见",
        RosterVisibility.Hidden => "身份隐藏",
        _ => "可见性未知"
    };

    public static string IdentityLabel(Participant participant) => participant.IdentityStrength switch
    {
        IdentityStrength.VerifiedStable => "稳定身份已确认",
        IdentityStrength.NameOnly => "仅显示名（不自动识别）",
        _ => "无可靠身份"
    };

    public static string CategoryList(IEnumerable<NoteCategory> categories, bool includeExpired = false) =>
        string.Join("、", categories
            .Distinct()
            .OrderBy(c => c)
            .Select(NoteCategories.GetDisplayName));

    public static string CategoryList(IEnumerable<NoteCategoryEntry> entries, DateTimeOffset now) =>
        string.Join("、", entries
            .OrderBy(e => e.Category)
            .Select(e => NoteCategories.GetDisplayName(e.Category)
                         + (e.ReminderExpiresAt <= now ? "（已过期）" : string.Empty)));

    public static string TagLabel(NoteTag tag) => tag.Kind switch
    {
        TagKind.Champion => "英雄 · " + tag.OfficialName,
        TagKind.Item => "装备 · " + tag.OfficialName,
        _ => tag.OfficialName
    };

    public static string TagList(IEnumerable<NoteTag> tags) =>
        string.Join("、", tags.Select(TagLabel));

    public static string LocalDate(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public static string LocalDateTime(DateTimeOffset value) =>
        value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture);

    public static string GamePhaseLabel(GamePhase phase) => phase switch
    {
        GamePhase.Lobby => "大厅",
        GamePhase.Matchmaking => "匹配中",
        GamePhase.ChampionSelect => "选人阶段",
        GamePhase.Loading => "加载阶段",
        GamePhase.InGame => "对局进行中",
        GamePhase.Finished => "已结束",
        GamePhase.Disconnected => "连接中断",
        _ => "阶段未知"
    };

    public static string SourceKindLabel(SourceKind source) => source switch
    {
        SourceKind.Synthetic => "模拟对局",
        SourceKind.Manual => "手工记录",
        SourceKind.Live => "真实客户端",
        _ => "未知来源"
    };

    public static string ProfileLabel(string profileId) =>
        profileId switch
        {
            "default" => "默认空间",
            "synthetic-demo" => "模拟演示空间",
            _ => profileId
        };

    /// <summary>Explains why an action is unavailable; shown on disabled controls.</summary>
    public static string DisabledReason(string text) => "不可用：" + text;
}
