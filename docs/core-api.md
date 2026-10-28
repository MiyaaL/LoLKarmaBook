# LoLKarmaBook Core / Storage API

Domain and persistence contracts used by the desktop and CLI. Targets `net10.0`,
with nullable reference types and implicit usings enabled. Local game-client
integration is implemented separately in `LoLKarmaBook.Client`.

Projects:

- `src/LoLKarmaBook.Core/LoLKarmaBook.Core.csproj` — domain types, policy, synthetic/manual
  providers, `NotebookService`. No SQLite dependency.
- `src/LoLKarmaBook.Storage/LoLKarmaBook.Storage.csproj` — SQLite implementation
  (`Microsoft.Data.Sqlite` 10.0.14). References Core.
- `tests/LoLKarmaBook.Core.Tests/LoLKarmaBook.Core.Tests.csproj` — dependency-free
  console test executable (no xUnit/NUnit/MSTest). Exits non-zero on failure.

Namespaces: `LoLKarmaBook.Core`, `LoLKarmaBook.Storage`.

---

## 1. Quick start for Desktop / CLI

```csharp
using LoLKarmaBook.Core;
using LoLKarmaBook.Storage;

// The Windows desktop supplies its DPAPI-backed ITextProtector here.
// Linux tests/demo may use the explicitly test-only protector (never real data).
ITextProtector protector = desktopProtector;

// Example data directory; callers supply the selected data root.
string root = Path.Combine(AppContext.BaseDirectory, "userdata", "data");

await using SqliteProfileStore store = await SqliteProfileStore.OpenAsync(
    root, "default", protector, CancellationToken.None);

var notebook = new NotebookService(store);          // optional IClock injection
var synthetic = new SyntheticProvider();

MatchSession matchA = synthetic.GetMatchA();
StoredNote note = await notebook.SaveNoteAsync(
    matchA,
    SyntheticScenario.MatchAXParticipantKey,
    new NoteDraft(
        RequestId: Guid.NewGuid().ToString("N"),
        Categories: new[] { NoteCategory.AfkLeave, NoteCategory.VerbalAbuse },
        Body: "18:40 离开约 6 分钟",
        EventTime: DateTimeOffset.UtcNow));

MatchSession matchB = synthetic.GetMatchB();
ReminderEvaluation result = await notebook.EvaluateRemindersAsync(matchB);
foreach (Reminder reminder in result.Reminders)
{
    // reminder.CurrentDisplayId is the CURRENT session name only.
    // StoredNote.PlayerDisplayName separately retains the name at note creation.
}
```

`SqliteProfileStore` is a **scoped** store: one instance == one profile == one
database file. `NotebookService` only knows `INotebookStore` and `IClock`, so the
desktop/CLI can inject the SQLite store directly. A different store implementation
can be supplied without changing the service.

Profile databases live at:

```text
<root>/profiles/<profile-id>/lolkarmabook.db
```

`profile-id` must match `^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$` (not `.`/`..`, no
separators, no control characters). The database stores its own profile id in
`meta`; opening a database with a different profile id fails rather than leaking
data across spaces.

---

## 2. Core value types

### 2.1 Categories

```csharp
public enum NoteCategory
{
    AfkLeave,       // 挂机逃跑
    PassivePlay,    // 消极游戏
    VerbalAbuse,    // 言语挑衅、辱骂
    LowSkill,       // 游戏技术水平较低
    OddBuild        // 乱出装
}

public static class NoteCategories
{
    public const int DefaultReminderDays = 365;
    public static IReadOnlyList<NoteCategory> All { get; }
    public static string GetCode(NoteCategory category);       // AFK_LEAVE, ...
    public static string GetDisplayName(NoteCategory category); // Chinese UI text
    public static bool TryParseCode(string code, out NoteCategory category);
}
```

Five categories are supported. All default to a **365-day** reminder.
A note may select several categories; a repeated click is deduplicated.
Category expiry closes the reminder only; it does not automatically delete the
note (the note remains listable and can be archived).

### 2.2 Identity

```csharp
public sealed class PlayerIdentity : IEquatable<PlayerIdentity>
{
    public string Operator { get; }
    public string IdentityNamespace { get; }
    public string RealmScope { get; }
    public string Kind { get; }
    public string Value { get; }
    public string ScopeKey { get; } // operator|namespace|realm|kind|value, display only

    public static PlayerIdentity Create(
        string @operator, string identityNamespace, string realmScope,
        string kind, string value);

    public static bool TryCreate(
        string? @operator, string? identityNamespace, string? realmScope,
        string? kind, string? value,
        out PlayerIdentity? identity, out string? error);

    public static bool IsPlaceholderValue(string? value);
}
```

Exact matching is always **operator + namespace + realm + kind + value**, ordinal
case-sensitive after trimming. Missing/empty/zero/`unknown`/`null`/`none`/`n/a`/
`-`/`?`/all-zero GUID-like values are rejected as placeholders. Merely having the
same display name is never an identity match. A renamed player with the same
stable ID matches; two players with the same display name but different IDs do
not.

### 2.3 Participants and visibility

```csharp
public enum ParticipantSide { Ally, Enemy, Unknown }
public enum RosterVisibility { Visible, Hidden, Unknown }
public enum IdentityStrength { Unknown, NameOnly, VerifiedStable }

public sealed class Participant
{
    public string ParticipantKey { get; }
    public ParticipantSide Side { get; }
    public string? Champion { get; }
    public string? CurrentDisplayId { get; }
    public RosterVisibility Visibility { get; }
    public IdentityStrength IdentityStrength { get; }
    public PlayerIdentity? Identity { get; }
    public bool IsSelf { get; }

    public static Participant Visible(
        string participantKey, ParticipantSide side,
        string currentDisplayId, PlayerIdentity identity,
        string? champion = null, bool isSelf = false);

    public static Participant VisibleNameOnly(
        string participantKey, ParticipantSide side,
        string currentDisplayId, string? champion = null, bool isSelf = false);

    public static Participant VisibleUnknown(
        string participantKey, ParticipantSide side,
        string? currentDisplayId = null, string? champion = null, bool isSelf = false);

    public static Participant Hidden(
        string participantKey, ParticipantSide side, bool isSelf = false);

    public static Participant UnknownVisibility(
        string participantKey, ParticipantSide side, bool isSelf = false);
}
```

Hidden/unknown participants are constructed with **no identity and no display
name**. The factories drop those fields at the adapter boundary. The reminder
engine independently requires `Visibility == Visible` and `IsSelf == false`.
Name-only participants can be stored as local notes, but they never auto-match.

### 2.4 Sessions

```csharp
public enum SourceKind { Synthetic, Manual, Live }
public enum GamePhase
{
    Unknown, Lobby, Matchmaking, ChampionSelect, Loading, InGame, Finished, Disconnected
}

public sealed class MatchSession
{
    public string LocalSessionId { get; }
    public SourceKind SourceKind { get; }
    public string ProviderInstanceId { get; }
    public long Generation { get; }
    public long Sequence { get; }
    public DateTimeOffset ObservedAt { get; }
    public GamePhase Phase { get; }
    public int? QueueId { get; }
    public int? MapId { get; }
    public string? ExternalMatchId { get; }
    public bool RosterComplete { get; }
    public IReadOnlyList<Participant> Participants { get; }
    public string MatchKey { get; } // "ext:<id>" or "local:<localSessionId>"

    public MatchSession(
        string localSessionId, SourceKind sourceKind, string providerInstanceId,
        long generation, long sequence, DateTimeOffset observedAt, GamePhase phase,
        int? queueId, int? mapId, string? externalMatchId, bool rosterComplete,
        IEnumerable<Participant> participants);

    public Participant? FindParticipant(string participantKey);
    public bool IsNewerThan(MatchSession other);
}
```

Sessions are immutable (the participant list is defensively copied and exposed
read-only). `Generation`/`Sequence` are ordered per provider instance.
`SessionRegistry` enforces freshness for UI/CLI code that keeps a current view:

```csharp
public sealed class SessionRegistry
{
    public MatchSession? Current { get; }
    public MatchSession? Previous { get; }
    public string? ActiveProviderInstanceId { get; }
    public bool TryAccept(MatchSession session, out string? rejectionReason);
    public void SwitchProvider(string providerInstanceId); // explicit account/client switch
    public void Reset();
}
```

A late snapshot from an old provider instance is rejected after an explicit
`SwitchProvider`. Same provider: lower generation, or same generation with a
non-increasing sequence, is rejected. A new local session id updates `Previous`.

`ManualSessionFactory.Create(...)` builds a `SourceKind.Manual` session from
user-entered visible participants. Manual sessions never auto-match.

---

## 3. Notes and reminders

```csharp
public enum NoteStatus { Draft, Active, Archived, Retracted }
public enum NoteSaveMode { Active, Draft }

public sealed record NoteDraft(
    string RequestId,
    IReadOnlyList<NoteCategory> Categories,
    string? Body = null,
    DateTimeOffset? EventTime = null,
    int ReminderDays = NoteCategories.DefaultReminderDays,
    IReadOnlyList<NoteTag>? Tags = null);

public sealed record NoteEdit(
    IReadOnlyList<NoteCategory> Categories,
    string? Body = null,
    DateTimeOffset? EventTime = null,
    int ReminderDays = NoteCategories.DefaultReminderDays,
    IReadOnlyList<NoteTag>? Tags = null);

public enum TagKind { Champion = 1, Item = 2 }
public sealed record NoteTag(TagKind Kind, int RefId, string OfficialName);

public sealed record NoteCategoryEntry(
    NoteCategory Category,
    DateTimeOffset ReminderExpiresAt);

public sealed class StoredNote
{
    public Guid NoteId { get; }
    public string RequestId { get; }
    public Guid? PlayerId { get; }              // null for name-only/manual-only notes
    public IReadOnlyList<NoteCategoryEntry> Categories { get; }
    public string Body { get; }
    public DateTimeOffset EventTime { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset UpdatedAt { get; }
    public NoteStatus Status { get; }
    public int Revision { get; }                // optimistic concurrency
    public string? SourceLocalSessionId { get; }
    public string? ExternalMatchId { get; }
    public SourceKind SourceKind { get; }
    public string? PlayerDisplayName { get; }
    public IReadOnlyList<NoteTag> Tags { get; }

    public bool IsActive(DateTimeOffset now);
    public IReadOnlyList<NoteCategory> GetActiveCategories(DateTimeOffset now);
}
```

Validation (service rejects before a transaction starts):

- `RequestId`: non-empty, <= 128 chars, no control chars. Reusing the same
  request id with the **same payload** returns the existing note (idempotent).
  Reusing it with a different payload throws.
- at least one distinct, defined category; at most all five.
- body: optional; trimmed; `<= 500` characters; whitespace becomes empty.
- `EventTime`: defaults to the injected clock's `UtcNow`; an explicitly supplied
  default/invalid time is rejected; no more than 10 minutes in the future.
- `ReminderDays`: `0..3650`. `0` is a valid "expires now" case used by tests.
- tags: at most 12 input entries, deduplicated by champion/item kind and ID;
  IDs must be positive and official names must be non-empty, contain no control
  characters and be at most 60 characters.
- participant key must exist in the session; the participant must be
  `Visible`, non-self, and (for stable matching) `VerifiedStable`.

`NotebookService` API:

```csharp
public sealed class NotebookService
{
    public NotebookService(INotebookStore store, IClock? clock = null);

    public Task<StoredNote> SaveNoteAsync(
        MatchSession session, string participantKey, NoteDraft draft,
        NoteSaveMode mode = NoteSaveMode.Active,
        CancellationToken cancellationToken = default);

    public Task<StoredNote> UpdateNoteAsync(
        Guid noteId, int expectedRevision, NoteEdit edit,
        CancellationToken cancellationToken = default);

    public Task<StoredNote> PublishDraftAsync(
        Guid noteId, int expectedRevision,
        CancellationToken cancellationToken = default);

    public Task<StoredNote?> GetNoteAsync(
        Guid noteId, CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<StoredNote>> ListNotesAsync(
        NoteQuery? query = null, CancellationToken cancellationToken = default);

    public Task<StoredNote> ArchiveNoteAsync(
        Guid noteId, int expectedRevision,
        CancellationToken cancellationToken = default);

    public Task<StoredNote> RetractNoteAsync(
        Guid noteId, int expectedRevision,
        CancellationToken cancellationToken = default);

    public Task<bool> DeleteNoteAsync(
        Guid noteId, CancellationToken cancellationToken = default);

    // Pure preview: does NOT create/consume alert receipts.
    public Task<IReadOnlyList<Reminder>> FindRemindersAsync(
        MatchSession session, ReminderOptions? options = null,
        CancellationToken cancellationToken = default);

    // Delivers receipts: returns only reminders newly shown for this match.
    // Listing notes or calling FindReminders never consumes alerts.
    public Task<ReminderEvaluation> EvaluateRemindersAsync(
        MatchSession session, ReminderOptions? options = null,
        CancellationToken cancellationToken = default);

    // "本局忽略": persists an ignored receipt for this session/player.
    public Task<bool> IgnoreForSessionAsync(
        MatchSession session, PlayerIdentity identity,
        CancellationToken cancellationToken = default);

    public Task ClearAllAsync(CancellationToken cancellationToken = default);
}

public sealed record NoteQuery
{
    public IReadOnlyCollection<NoteCategory>? Categories { get; init; }
    public NoteStatus? Status { get; init; }
    public Guid? PlayerId { get; init; }
    public DateTimeOffset? EventFrom { get; init; }
    public DateTimeOffset? EventTo { get; init; }
}

public enum ReminderAudience { Both, AlliesOnly }
public sealed record ReminderOptions
{
    public ReminderAudience Audience { get; init; } = ReminderAudience.Both;
    public IReadOnlyCollection<Guid>? MutedPlayerIds { get; init; }
}

public sealed class Reminder
{
    public Guid PlayerId { get; }
    public PlayerIdentity Identity { get; }
    public string ParticipantKey { get; }
    public ParticipantSide Side { get; }
    public string? CurrentDisplayId { get; }      // name from the current session
    public IReadOnlyList<NoteCategory> ActiveCategories { get; }
    public IReadOnlyList<ReminderNote> Notes { get; }
    public DateTimeOffset MostRecentEventTime { get; }
    public int DistinctSessionCount { get; }      // distinct match/session keys, not clicks
}

public sealed class ReminderNote
{
    public Guid NoteId { get; }
    public IReadOnlyList<NoteCategory> Categories { get; }
    public DateTimeOffset EventTime { get; }
    public string? SessionKey { get; }
}

public enum ReminderSupport
{
    Enabled = 1, DisabledManual = 2, UnsupportedSource = 4, DisabledPhase = 5
}

// Shared rule used by service, desktop and CLI for active observed rosters.
public static class ReminderPolicy
{
    public static bool IsActiveRosterPhase(GamePhase phase);
    public static bool IsFinishedPhase(GamePhase phase);
    public static bool IsRosterPhase(GamePhase phase);
    public static bool CanRemind(MatchSession session);
}

public sealed class ReminderEvaluation
{
    public ReminderSupport Support { get; }
    public bool IsEnabled { get; }
    public IReadOnlyList<Reminder> Reminders { get; }
    public string Reason { get; }
}
```

### Reminder rules (implemented)

`SourceKind.Synthetic` or `SourceKind.Live` may produce reminders while a current
match roster is active: `ChampionSelect`, `Loading` or `InGame`
(`ReminderPolicy.CanRemind` and `IsActiveRosterPhase`). `Unknown`, `Lobby`,
`Matchmaking` and `Disconnected` return no reminders and do not write receipts.
`Finished` permits history preview through `FindRemindersAsync`, but does not
deliver notifications or write receipts through `EvaluateRemindersAsync`.
An active phase alone does not establish a stable identity; matching still
applies the rules below.

1. Consider only `Visible`, non-self participants with `VerifiedStable` identity.
   Hidden/unknown/name-only participants are skipped without leaking names or
   hit counts.
2. Find the profile-local player by the full five-part identity scope via the
   keyed HMAC index.
3. Take notes with status `Active` and `SourceKind != Manual`, excluding notes
   created in the **current** session. Manual and draft notes never match.
4. A note is eligible only while at least one selected category is unexpired
   (`ReminderExpiresAt > now`).
5. Apply audience (`AlliesOnly`) and muted player ids.
6. Persisted receipts (keyed by `session.MatchKey` + player):
   - `Ignored` (本局忽略) hides the player from **both** `FindRemindersAsync`
     preview and `EvaluateRemindersAsync` delivery, across restarts;
   - `Shown` suppresses only new notification delivery. Normal history stays
     visible in the `FindRemindersAsync` preview (a shown alert can still be
     inspected), and listing notes never consumes anything.
7. `EvaluateRemindersAsync` creates/reuses a `Shown` receipt per
   `(match key, player)`; a second call, a process restart, or a reconnect with
   the same match key does not notify again. With no external match id the local
   session id is the key; callers must preserve it to deduplicate across restart.
8. `Manual` returns `ReminderSupport.DisabledManual`; an observed Synthetic or
   Live session outside the active roster phases returns
   `ReminderSupport.DisabledPhase`. Neither case writes receipts.
9. `SaveNoteAsync` accepts Synthetic, Live and Manual sessions. A visible,
   verified-stable participant in an observed roster binds the note to the full
   account identity. Manual authoring remains local input and does not bind a
   stable identity.

### Request-id idempotency

A request id is bound to its full authoring context. The persisted fingerprint
covers, with unambiguous length-prefixed encoding: source kind, local session id,
external match id, participant key, a presence marker plus **each of the five
identity components separately**, the sorted distinct categories, body, raw event
time, reminder days, save mode and normalized tags. Reusing a request id is idempotent only when
everything matches; otherwise `CreateNoteAsync` throws
`InvalidOperationException`. In particular the same request id in a different
match (different local or external id) or with a different identity is rejected
instead of silently returning the first match's note.

`StoredNote.PlayerDisplayName` retains the participant's name at note creation,
protected at rest. It is a historical display snapshot, not an identity key.
The display name shown in a reminder comes from the current session's participant;
matching continues to use the full stable identity scope.

`NotebookService.ClearAllAsync` deletes notes, categories, tags, receipts, players and
journal rows for the current profile only.

---

## 4. Storage contracts

```csharp
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
public sealed class SystemClock : IClock { public static SystemClock Instance { get; } }

public interface ITextProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

public sealed class SyntheticTestProtector : ITextProtector
{
    // Explicitly test/demo only; refuses to be mistaken for production DPAPI.
    public static SyntheticTestProtector CreateForTestsOnly(ReadOnlySpan<byte> key32);
    public bool IsTestOnly { get; } // always true
}
```

`ITextProtector` is required by the SQLite store. Sensitive text (note body,
the stable identity value and player display-name snapshot) is stored protected.
Operator/namespace/realm/kind, categories, official tags, timestamps and the keyed
lookup index are stored as metadata.
**This is not full database encryption** and must not be described as such. The
Windows implementation is `LoLKarmaBook.Desktop.Security.DpapiTextProtector`.

The SQLite schema (per profile database, **schema version 4**) contains:

- `meta(key, value)` — `schema_version`, owning `profile_id`, and
  `identity_lookup_key` (32 random bytes, base64, stored **only** as
  `ITextProtector.Protect(...)`).
- `players(player_id, operator, identity_namespace, realm_scope, id_kind,
  value_protected, value_lookup, first_seen, last_seen)`.
  `value_lookup` is `HMAC-SHA256(profile_key, length-prefixed full scope)`; the
  original value is also stored protected. The key is random per profile, so the
  index cannot be recomputed without decrypting the protected key, and the same
  identity indexes differently in each profile. It is still an exact-match index,
  **not** a claim of anonymization.
- `notes(... request_id UNIQUE, request_fingerprint, player_id NULL,
  source_local_session_id, external_match_id, source_kind, body_protected,
  event_time, created_at, updated_at, status, revision, reminder_days,
  player_display_name_protected)`.
- `note_categories(note_id, category, reminder_expires_at, PRIMARY KEY)`.
- `note_tags(note_id, kind, ref_id, official_name, PRIMARY KEY(note_id, kind, ref_id))`.
- `alert_receipts(session_key, local_session_id, player_id, state,
  created_at, UNIQUE(session_key, player_id))`.
- `change_journal(...)`, cleaned when a note is hard-deleted.

SQLite settings: foreign keys ON, WAL, busy timeout. Note + categories + tags + player
upsert + journal are committed in one transaction. Optimistic `revision` prevents
lost edits. Hard delete is profile-scoped and removes dependent rows in the same
transaction.

### 4.1 `SqliteProfileStore`

```csharp
public sealed class SqliteProfileStore : INotebookStore, IAsyncDisposable
{
    public string ProfileId { get; }
    public string DatabasePath { get; }

    public static Task<SqliteProfileStore> OpenAsync(
        string rootDirectory, string profileId, ITextProtector protector,
        CancellationToken cancellationToken = default);

    // Explicit-path open (tests/embedded). rootDirectory is optional.
    public static Task<SqliteProfileStore> OpenDatabaseAsync(
        string databasePath, string profileId, ITextProtector protector,
        string? rootDirectory = null,
        CancellationToken cancellationToken = default);

    // INotebookStore members are described in src/LoLKarmaBook.Core/INotebookStore.cs.
    public ValueTask DisposeAsync();
}
```

`OpenAsync` validates the root and profile id, creates
`<root>/profiles/<id>/`, and refuses any path that escapes the root (including
via symlinks).

`OpenDatabaseAsync` **accepts an arbitrary explicit absolute path** and creates
the database there; that is the documented embedded/test contract. It still
validates:
- the profile id format; and
- the database's stored `profile_id` owner marker — opening another profile's
  database throws instead of leaking across spaces.

Optional hardening: when `rootDirectory` is supplied the path must be the
profile's canonical location under that root (siblings/`..`/symlink escapes are
rejected with `ArgumentException`). Relative paths without a root are rejected.

Schema versions 2 and 3 migrate to version 4 in one transaction while preserving
existing rows. Version 3 adds the protected display-name snapshot; version 4 adds
official champion/item tags. Existing notes receive no tags or invented names.
Version 1 (which used a bare SHA-256 index) and newer unsupported schemas are
rejected. Opening also rejects a different profile owner, an undecryptable lookup
key, or a missing lookup key while player rows exist.

### 4.2 Backup / restore

```csharp
public static class SqliteProfileMaintenance
{
    public static Task BackupAsync(
        string databasePath, string destinationPath, string expectedProfileId,
        CancellationToken cancellationToken = default);

    public static Task RestoreAsync(
        string backupPath, string targetDatabasePath, string expectedProfileId,
        ITextProtector protector,
        CancellationToken cancellationToken = default);
}
```

Backups use SQLite's online backup API (consistent snapshot, WAL-safe). Both
operations verify the backup's `meta.profile_id` equals `expectedProfileId`;
restoring a profile A backup into a profile B database fails before any write.
`RestoreAsync` requires `targetDatabasePath` to belong to the expected profile
(or not exist). Backups are same-device/same-Windows-user artifacts, not
portable encrypted exports.

**Restore validates the backup completely before touching the target.** The
method runs all of the following against the backup in
read-only mode, and the target is only opened for writing after every check
passes; any failure throws `InvalidOperationException` and leaves the target
byte-identical:

1. `meta.profile_id` equals `expectedProfileId`;
2. `PRAGMA integrity_check` returns `ok`;
3. `meta.schema_version` is a supported restorable version (2, 3 or 4). Version 1
   and newer unsupported backups are rejected; restored versions 2/3 migrate on
   the next store open;
4. every required table (`meta`, `players`, `notes`, `note_categories`,
   `alert_receipts`, `change_journal`, plus `note_tags` for version 4) exists and
   contains the columns required by that version, including `notes.source_kind`
   and the display-name column for versions 3/4;
5. `meta.identity_lookup_key` exists, decrypts with the supplied protector to
   exactly 32 bytes.

**Lifecycle requirement.** Callers must close/dispose the profile's
`SqliteProfileStore` **before** calling `RestoreAsync` and open a new store
**afterwards**. The store caches the profile-local HMAC identity lookup key for
the lifetime of the object. A backup is usually produced by a different store
instance, and the same profile id does **not** imply the same random lookup key,
so an already-open store would keep using the pre-restore key: new players
written through it would be indexed under the wrong key and never match later.
The restore call cannot detect or fix an open store, so this is the caller's
responsibility (the Desktop maintenance flow enters an exclusive workspace
gate that closes the store, restores, then reopens).

There is no restore overload without a protector.

---

## 5. Synthetic demo provider

```csharp
public sealed class SyntheticScenario
{
    public const string Operator = "tencent-cn";
    public const string IdentityNamespace = "synthetic-demo-v1";
    public const string RealmScope = "synthetic-realm";
    public const string IdKind = "puuid";

    public const string MatchAXParticipantKey = "ally-x";
    public const string MatchBRenamedXParticipantKey = "enemy-x-renamed";
    public const string MatchBSameNameYParticipantKey = "enemy-y-same-name";
    public const string MatchBNameOnlyParticipantKey = "enemy-name-only";
    public const string MatchBHiddenParticipantKey = "enemy-hidden";

    public static PlayerIdentity PlayerXIdentity { get; }
    public static PlayerIdentity PlayerYIdentity { get; }
    public static PlayerIdentity HiddenPlayerIdentity { get; }
    public static string PlayerXOriginalDisplayName { get; } // 演示玩家X
    public static string PlayerXRenamedDisplayName { get; }  // 演示玩家X-改名后
    public static string SameNameYDisplayName { get; }       // == PlayerXOriginalDisplayName
    public static string NameOnlyDisplayName { get; }

    public MatchSession MatchA { get; }   // source Synthetic, finished, X visible+stable
    public MatchSession MatchB { get; }   // X renamed+same id, Y same name+different id,
                                          // name-only, hidden, self; roster current
    public static SyntheticScenario CreateDefault();
}

public sealed class SyntheticProvider
{
    public SyntheticProvider(SyntheticScenario? scenario = null);
    public SyntheticScenario Scenario { get; }
    public MatchSession GetMatchA();
    public MatchSession GetMatchB();
}
```

All names and ids in this provider are fictional. It performs no I/O or network
access. Callers can also supply `SourceKind.Live` snapshots to the same notebook
API; `LoLKarmaBook.Client` supplies real local-client snapshots.

---

## 6. Test executable

`tests/LoLKarmaBook.Core.Tests` is a console app with no test-framework packages.
Each test is a named `async` method; assertions throw; any failure prints the
test name and returns exit code `1`. Temp directories are created under the
system temp path and removed in `finally`.

Run (from repository root):

```bash
dotnet run --project tests/LoLKarmaBook.Core.Tests/LoLKarmaBook.Core.Tests.csproj -c Release
```

Covered risks:

- real SQLite create/reopen and persistence after process-style dispose;
- idempotent request id, conflicting request id reuse, revision conflict;
- edit, archive, retract, delete, draft publish;
- profile database separation, delete isolation, backup/restore profile binding;
- 365-day default and expired-category suppression (injected clock);
- same stable id with rename matches; same name with different id does not;
- hidden and name-only participants never auto-match; self excluded;
- Manual sessions do not auto-match; Live sessions match stable identities,
  preserve namespace separation and deduplicate delivery across restarts/reconnects;
- alert receipt survives store/service restart; listing/find does not consume;
- single-session ignore persists;
- invalid draft/profile/path/identity inputs rejected;
- note body, display-name snapshot and identity value are protected at rest;
- official champion/item tags persist across save, edit and reopen;
- request fingerprint binds source kind, local session id, external match id and
  every identity component separately (same id + same match is idempotent,
  reuse in another session is rejected);
- `Ignored` receipt hides both preview and delivery across reopen; `Shown`
  receipt suppresses only new delivery, preview stays visible;
- `ChampionSelect`, `Loading` and `InGame` each match and deliver once per match
  key for Synthetic and Live rosters; Finished permits preview without delivery;
  Unknown/Lobby/Matchmaking/Disconnected never remind or consume receipts;
- saving a note from a Live session is idempotent, conflicting request reuse is
  rejected, and notes authored in the current match do not immediately remind;
- identity lookup uses a per-profile randomly generated HMAC key stored only
  protected in `meta`; a wrong protector or missing key with existing player
  rows fails closed, and backup/restore preserves the key;
- schema versions 2/3 migrate to version 4; version 1 is refused;
- restore validates schema version, required tables/columns and the protected
  lookup key before writing; invalid backups (unsupported schema, missing table,
  missing column, wrong protector, null protector) leave the target byte-identical.

### Integration boundaries

- Windows DPAPI and WPF runtime checks belong to the Desktop test suite;
  these Core tests use the `ITextProtector` interface with a test protector.
- Local-client observation is provided by `LoLKarmaBook.Client`; official Riot
  API match retrieval is provided by `LoLKarmaBook.Riot`.
- SQLite metadata is not encrypted. Note bodies, stable identity values and
  display-name snapshots are protected. The per-profile HMAC lookup key is also
  protected, but code running as the same Windows user can access DPAPI; this
  storage design does not provide full-database encryption or anonymization.
- Reminder deduplication across restarts relies on the persistent match key
  (`ext:<externalMatchId>` or `local:<localSessionId>`). When no external match ID
  is available, callers must preserve `LocalSessionId` for the same match.
- Restore requires a protector and a closed store. Reopen after restore to load
  the restored identity lookup key and migrate supported older schemas.
