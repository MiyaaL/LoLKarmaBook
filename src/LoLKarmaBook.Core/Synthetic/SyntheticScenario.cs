namespace LoLKarmaBook.Core;

/// <summary>
/// Deterministic fictional two-match scenario. No I/O, no network, no real
/// player data.
///
/// Match A records player X. Match B contains:
///  - X with a renamed display id but the same stable identity (must match);
///  - Y with X's old display name but a different stable identity (must NOT match);
///  - a name-only participant with the same display name (must NOT match);
///  - a hidden participant (identity/name dropped at construction);
///  - the local player (self, excluded).
/// </summary>
public sealed class SyntheticScenario
{
    public const string Operator = "tencent-cn";
    public const string IdentityNamespace = "synthetic-demo-v1";
    public const string RealmScope = "synthetic-realm";
    public const string IdKind = "puuid";

    public const string ProviderInstanceId = "synthetic-provider";
    public const string MatchALocalSessionId = "synthetic-local-a";
    public const string MatchBLocalSessionId = "synthetic-local-b";
    public const string MatchAExternalId = "synthetic-match-a";
    public const string MatchBExternalId = "synthetic-match-b";

    public const string MatchAXParticipantKey = "ally-x";
    public const string MatchBRenamedXParticipantKey = "enemy-x-renamed";
    public const string MatchBSameNameYParticipantKey = "enemy-y-same-name";
    public const string MatchBNameOnlyParticipantKey = "enemy-name-only";
    public const string MatchBHiddenParticipantKey = "enemy-hidden";
    public const string MatchBSelfParticipantKey = "self-b";

    public static PlayerIdentity PlayerXIdentity { get; } = PlayerIdentity.Create(
        Operator, IdentityNamespace, RealmScope, IdKind, "synthetic-x-7f0c4e18a2");

    public static PlayerIdentity PlayerYIdentity { get; } = PlayerIdentity.Create(
        Operator, IdentityNamespace, RealmScope, IdKind, "synthetic-y-91ab6d02c5");

    public static PlayerIdentity HiddenPlayerIdentity { get; } = PlayerIdentity.Create(
        Operator, IdentityNamespace, RealmScope, IdKind, "synthetic-hidden-3e77c0");

    public static PlayerIdentity SelfIdentity { get; } = PlayerIdentity.Create(
        Operator, IdentityNamespace, RealmScope, IdKind, "synthetic-self-5d1f");

    public static PlayerIdentity TeammateIdentity { get; } = PlayerIdentity.Create(
        Operator, IdentityNamespace, RealmScope, IdKind, "synthetic-mate-2b8a");

    public static string PlayerXOriginalDisplayName => "演示玩家X";
    public static string PlayerXRenamedDisplayName => "演示玩家X-改名后";
    public static string SameNameYDisplayName => PlayerXOriginalDisplayName;
    public static string NameOnlyDisplayName => "演示玩家X";
    public static string HiddenDisplayName => "不应出现";

    public MatchSession MatchA { get; }
    public MatchSession MatchB { get; }

    public SyntheticScenario()
        : this(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public SyntheticScenario(DateTimeOffset matchATime)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(matchATime, default);

        MatchA = new MatchSession(
            MatchALocalSessionId,
            SourceKind.Synthetic,
            ProviderInstanceId,
            generation: 1,
            sequence: 1,
            observedAt: matchATime,
            phase: GamePhase.Finished,
            queueId: 420,
            mapId: 11,
            externalMatchId: MatchAExternalId,
            rosterComplete: true,
            new[]
            {
                Participant.Visible(
                    MatchAXParticipantKey,
                    ParticipantSide.Ally,
                    PlayerXOriginalDisplayName,
                    PlayerXIdentity,
                    champion: "Ahri"),
                Participant.Visible(
                    "ally-mate",
                    ParticipantSide.Ally,
                    "演示队友",
                    TeammateIdentity,
                    champion: "Nami"),
                Participant.Visible(
                    "self-a",
                    ParticipantSide.Ally,
                    "本人",
                    SelfIdentity,
                    isSelf: true)
            });

        MatchB = new MatchSession(
            MatchBLocalSessionId,
            SourceKind.Synthetic,
            ProviderInstanceId,
            generation: 1,
            sequence: 2,
            observedAt: matchATime.AddHours(1),
            phase: GamePhase.InGame,
            queueId: 420,
            mapId: 11,
            externalMatchId: MatchBExternalId,
            rosterComplete: true,
            new[]
            {
                Participant.Visible(
                    MatchBRenamedXParticipantKey,
                    ParticipantSide.Enemy,
                    PlayerXRenamedDisplayName,
                    PlayerXIdentity,
                    champion: "Ahri"),
                Participant.Visible(
                    MatchBSameNameYParticipantKey,
                    ParticipantSide.Enemy,
                    SameNameYDisplayName,
                    PlayerYIdentity,
                    champion: "Lux"),
                Participant.VisibleNameOnly(
                    MatchBNameOnlyParticipantKey,
                    ParticipantSide.Enemy,
                    NameOnlyDisplayName,
                    champion: "Lux"),
                Participant.Hidden(
                    MatchBHiddenParticipantKey,
                    ParticipantSide.Enemy),
                Participant.Visible(
                    MatchBSelfParticipantKey,
                    ParticipantSide.Ally,
                    "本人",
                    SelfIdentity,
                    isSelf: true)
            });
    }

    public static SyntheticScenario CreateDefault() => new();
}
