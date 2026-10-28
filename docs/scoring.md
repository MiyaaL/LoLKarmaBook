# Scoring modules

Status: implemented in `src/LoLKarmaBook.Scoring/`, tests in `tests/LoLKarmaBook.Scoring.Tests/`, fixtures in `fixtures/scoring/`.
Target framework: `net10.0`. No NuGet dependencies. Calculations run locally.

These local calculators are available through the library and CLI. The desktop's
Hexdata page displays reports from `LoLKarmaBook.Hexdata`; those remote scores
are separate from the local algorithms documented here.

Three local calculations:

1. **Recent performance** (`performance-1.0.0`) scores completed match stats.
2. **Team Elo** (`synthetic-team-elo-1.0.0`) updates player ratings from team results. Existing type names and the algorithm identifier are retained.
3. **Estimated Elo** (`estimated-elo-1.0.0`) blends the two for one player: team Elo plus a bounded performance adjustment. It requires a completed roster for each match.

### Estimated Elo

```csharp
public static EstimatedElo Calculate(
    string playerId,
    IReadOnlyList<RatingMatchInput> matches,
    IReadOnlyList<MatchStatRow> statRows,
    string scope);
```

`Rating` = team Elo + `(performance - 50) * 4 * (0.25 / 0.75)`, so a full
100-point performance score is worth ±66.7 Elo around the 1500 baseline. When the
stat rows cannot produce a performance score, the team Elo stands alone. The
result is rounded to 0.05 and flagged provisional below 10 games.

The CLI `estimate-demo` command generates its input in memory through
`SyntheticScenarioGenerator.CreateEstimatedEloPerformanceDemo()`.

Datasets may carry an optional roster:

```json
"playerId": "synthetic:target",
"roster": {
  "ownerId": "synthetic:target",
  "matches": [
    { "matchId": "demo-performance-001", "timestamp": "2026-01-01T00:00:00+00:00",
      "teammates": ["…4 ids…"], "opponents": ["…5 ids…"] }
  ]
}
```

The roster is all-or-nothing: same length, same order and same `matchId` as
`matches`, four distinct teammates and five distinct opponents per match. Without
it the file is performance-only and cannot supply a team-based Elo estimate.

Both dataset formats accept source labels from imported data and preserve those labels on export.
The existing defaults, `user-owned-completed-matches` and `synthetic`, remain valid for examples.

## Public API

All types are in the `LoLKarmaBook.Scoring` assembly.

### Recent performance

```csharp
namespace LoLKarmaBook.Scoring.Performance;

public sealed record MatchStatRow(
    string MatchId, bool Win,
    int Kills, int Deaths, int Assists, int TeamTotalKills,
    double TotalDamageDealt, double TeamTotalDamageDealt,
    double TotalDamageTaken, double TeamTotalDamageTaken,
    double GoldEarned, double TeamTotalGoldEarned);

public sealed class PerformanceDataset
{
    public int SchemaVersion { get; init; } = 1;       // must be 1
    public string Source { get; init; } = PerformanceJson.UserOwnedSource; // validated source label
    public IReadOnlyList<MatchStatRow> Matches { get; init; } = [];
}

public static class PerformanceCalculator
{
    public const string AlgorithmVersion = "performance-1.0.0";
    public const double SamplePriorScore = 60.0;
    public const int SamplePriorGames = 5;

    // Pure calculator. Validates all rows; throws ScoringValidationException.
    public static PerformanceScore Calculate(IReadOnlyList<MatchStatRow> matches);

    // Safe path: validates schemaVersion and source metadata first.
    public static PerformanceScore Calculate(PerformanceDataset dataset);
}

public static class PerformanceMath
{
    // x when x is finite and > 0, otherwise 0.
    public static double PositiveClean(double x);
    // null when part <= 0 or total <= part (equality is intentionally excluded), otherwise part/total.
    public static double? Ratio(double part, double total);
}

public sealed record PerformanceScore(
    bool IsAvailable,                 // false when aggregate KDA is unavailable
    int GameCount,
    int WinCount,
    double WinRate,
    double? AggregateKda,             // null when kills + assists == 0
    double? RawWeightedSum,           // null when unavailable
    double? NormalizedScore,          // raw * 100 / active weight
    double? FinalScore,               // 0..100, shrunk toward 60 with 5 pseudo-games
    PerformanceComponents Components,
    PerformanceContributions Contributions,
    PerformanceWeights Weights,
    IReadOnlyList<string> MissingMetrics,
    string AlgorithmVersion);

public sealed record PerformanceComponents(
    double? KillParticipation,
    double? DamageShare,
    double? GoldShare,
    double? TakenShare,
    double? Conversion);

// Weighted contribution (points, not normalized) for each component.
public sealed record PerformanceContributions(
    double WinRate, double Kda, double KillParticipation,
    double Conversion, double Damage, double Taken);

public sealed record PerformanceWeights(
    double WinRate, double Kda, double KillParticipation,
    double Conversion, double Damage, double Taken)
{
    public double Total { get; }      // 28..100
}

public static class PerformanceJson
{
    public const int SchemaVersion = 1;
    public const long MaxBytes = 5 * 1024 * 1024;

    // Size guard, strict schema, metadata/finite/integer/duplicate validation.
    public static PerformanceDataset Parse(string json);
    public static PerformanceDataset LoadFile(string path);
    public static string Export(PerformanceDataset dataset, bool indented = true);
}
```

`ScoringValidationException.Code` is a stable machine-readable code, e.g.
`performance.empty`, `performance.duplicate-match-id`, `performance.invalid-number`,
`performance.source`, `performance.json`, `performance.non-finite`, `rating.duplicate-id-conflict`,
`rating.scope`, `rating.teams`, `rating.json`, `scoring.file-too-large`, `scoring.file-read`.

### Duplicate JSON properties

Both importers reject a repeated property name **in any object, at any depth**, before
deserialization. This prevents conflicting values (for example two `source` or `schemaVersion`
entries, or two `win`/`winner` keys) from being resolved by "last value wins".

- Two layers: .NET 10 `JsonSerializerOptions.AllowDuplicateProperties = false`, plus an explicit
  `JsonDocument` walk that reports the JSON path (for example `$.matches[0].win`).
- Comparison is case-insensitive (`source` vs `Source`), because both spellings bind to one member.
  This is independent of `PropertyNameCaseInsensitive = false`, which only affects matching.
- The failure is always `ScoringValidationException` with `Code` `performance.json` or
  `rating.json`; serializer errors, malformed JSON and unsupported shapes are wrapped the same way,
  so callers never see a raw `JsonException`/`NotSupportedException`.
- Missing required fields and wrong schema versions keep their existing codes
  (`performance.structure`/`rating.structure`, `performance.schema-version`/`rating.schema-version`).

### Team Elo

```csharp
namespace LoLKarmaBook.Scoring.Rating;

public sealed class SyntheticRatingDataset
{
    public int SchemaVersion { get; init; } = 1;   // must be 1
    public string Source { get; init; } = "synthetic"; // default; other valid source labels accepted
    public SyntheticScope Scope { get; init; } = new();
    public IReadOnlyList<SyntheticRatingMatch> Matches { get; init; } = [];
}

public sealed record SyntheticScope(
    string Operator, string Realm, string Queue, string Patch);

public sealed record SyntheticRatingMatch(
    string MatchId,
    DateTimeOffset Timestamp,
    SyntheticScope? Scope,             // null => inherit dataset scope; otherwise must equal it
    IReadOnlyList<string> TeamA,       // exactly 5 distinct player IDs; disjoint from TeamB
    IReadOnlyList<string> TeamB,
    string Winner);                    // "A" or "B"

public static class SyntheticRatingCalculator
{
    public const string AlgorithmVersion = "synthetic-team-elo-1.0.0";
    public const double InitialRating = 1500.0;
    public const double Scale = 400.0;
    public const double KFactor = 24.0;
    public const int ProvisionalGameThreshold = 10;

    // targets == null: return every player. Otherwise only those IDs (must exist).
    public static SyntheticRatingResult Calculate(
        SyntheticRatingDataset dataset,
        IEnumerable<string>? targetPlayerIds = null);
}

public sealed record SyntheticRatingResult(
    IReadOnlyList<PlayerRating> Players,
    IReadOnlyList<SyntheticMatchRating> History,
    int PlayerCount,
    int ConnectedPoolCount,
    int LargestConnectedPoolSize,
    int MatchCount,                    // after deterministic dedupe
    int DuplicateMatchCount,
    string AlgorithmVersion);

public sealed record PlayerRating(
    string PlayerId, double Rating, int Games, bool IsProvisional,
    string ConnectedPoolId, int ConnectedPoolSize);

public sealed record SyntheticMatchRating(
    string MatchId, DateTimeOffset Timestamp,
    double TeamAAverageBefore, double TeamBAverageBefore,
    double ExpectedTeamA, double Delta, string Winner,
    IReadOnlyList<RatingChange> Changes);

public sealed record RatingChange(
    string PlayerId, string Team, double RatingBefore, double RatingAfter);

public static class SyntheticRatingJson
{
    public const int SchemaVersion = 1;
    public const long MaxBytes = 5 * 1024 * 1024;

    public static SyntheticRatingDataset Parse(string json);
    public static SyntheticRatingDataset LoadFile(string path);
    public static string Export(SyntheticRatingDataset dataset, bool indented = true);
}

public static class SyntheticScenarioGenerator
{
    public const string DefaultTargetPlayerId = "synthetic:target";
    public static SyntheticRatingDataset CreateRatingDemo(int targetMatchCount = 14);
    public static PerformanceDataset CreatePerformanceDemo(int matchCount = 12);
}
```

Note: `IReadOnlyList<T>` members are serialized as JSON arrays. JSON is camelCase. Unknown fields, wrong schema versions, duplicate IDs and non-finite numbers are rejected.

## Recent performance algorithm

Every row is validated: non-empty unique `MatchId`, finite non-negative numbers, non-negative integer counts. `positiveClean(x)` = `x` if finite and `> 0`, else `0`.

`ratio(part,total)` = `null` when `part <= 0` **or** `total <= part`; otherwise `part / total`. Equality is deliberately excluded, including `part == total` (a player cannot take a 100% share in this model). This parity-required behaviour makes missing/zero/over-total shares safely unavailable instead of producing 1 or >1.

For `n > 0` games:

- `winRate = wins / n`
- `KDA = (sumKills + sumAssists) / max(1, sumDeaths)`; `null` when the numerator is 0.
- Per-game available shares are averaged (not ratio-of-sums): kill participation, damage share, gold share, damage-taken share.
- If aggregate KDA is `null`, the score is **unavailable** (`FinalScore`, `NormalizedScore`, `RawWeightedSum` are `null`); it is never converted to 0.
- Terms are `clamp01(value / denominator)`:
  - KDA `/ 8`
  - kill participation `/ 0.5`
  - conversion = `damageShare / goldShare / 1.6` (0 when either share is missing or `goldShare <= 0`)
  - damage share `/ 0.35`
  - damage taken share `/ 0.4`
- `raw = 20*winRate + 8*kda + 17*kp + 30*conversion + 20*damage + 5*taken`
- Active weights: start at 28 (win + KDA); `+17` if kill participation exists; `+30` if damage and gold shares exist and gold share > 0; `+20` if damage share exists; `+5` if taken share exists.
- `normalized = raw * 100 / active`
- `final = clamp(0, 100, (normalized * n + 60 * 5) / (n + 5))`

`MissingMetrics` uses stable names: `aggregate-kda`, `kill-participation`, `damage-share`, `gold-share`, `conversion`, `taken-share`, plus the warning `conversion-overflow` (diagnostic only, see below). It is empty when every metric is present.

Comparator rule used throughout: a per-game share counts only when `part > 0` and `total > part`. Zero totals, zero parts, `part == total` and `part > total` are all "not available" for that row.

### Numeric handling (overflow and clamp)

- Every input is required to be finite and non-negative before calculation; non-finite JSON numbers (for example `NaN`, `1e999`) are rejected at the boundary.
- `clamp01` reproduces the legacy clamp exactly:
  `function c(e){return !Number.isFinite(e) || e <= 0 ? 0 : e >= 1 ? 1 : e}`
  So **any non-finite value, including `+Infinity`, maps to 0**; values `<= 0` map to 0; values `>= 1` map to 1; otherwise the value passes through.
- An extremely small positive gold share makes `damageShare / goldShare / 1.6` overflow to `+Infinity`.
  Because `clamp01(+Infinity) == 0`, the conversion term **contributes 0**, while its 30-point
  weight stays active: active weights depend only on input availability (damage and gold shares
  present, gold > 0), exactly as in the legacy algorithm.
- Non-finite values are never exposed in the result DTO. When conversion overflows,
  `Components.Conversion` is `null` and `MissingMetrics` contains the warning
  `conversion-overflow` (it does not contain `conversion`, and `Weights.Conversion` is still 30).
  If a future change ever produced a non-finite raw or normalized value, the calculator throws
  `ScoringValidationException("performance.non-finite")` instead of returning it.
- The result contains only finite numbers (or `null`), so it serializes with a stock
  `JsonSerializer` without allowing named floating-point literals. Tests assert this.

## Team Elo algorithm

`source` accepts a label of 1–120 characters, without leading/trailing whitespace or control characters; examples include `synthetic`, `live` and `manual`. The dataset retains the supplied label. Mixed per-match scopes, malformed teams (not exactly 5 distinct IDs per side, overlap, empty IDs), conflicting duplicate IDs and non-finite input are rejected. Identical duplicate games (same ID and identical canonical content) are deduplicated deterministically and reported via `DuplicateMatchCount`.

Matches are sorted by timestamp (UTC instant) then `MatchId` using ordinal ordering. Within one match:

- snapshot all 10 pre-game ratings;
- `expectedA = 1 / (1 + 10^((meanB - meanA) / 400))`;
- `delta = 24 * (resultA - expectedA)`, where `resultA` is 1 if A wins, else 0;
- every A player gets `+= delta`, every B player `-= delta` (zero-sum, all players change by the same magnitude).

Initial rating 1500. `Games` counts processed matches per player. `IsProvisional` is `Games < 10`. Connected pools are computed from the match graph; `ConnectedPoolId` is the lexicographically smallest player ID in that pool. `History` exposes the transparent per-match snapshot, expected value, delta and every rating change.

## JSON import/export

Both importers accept at most **5 MiB** (5,242,880 bytes), reject malformed JSON and unknown fields, require `schemaVersion: 1`, and validate source metadata before calculation. A source label must be 1–120 characters, contain no control characters, and have no leading/trailing whitespace. Import and export preserve its original value. Export uses the same schema.

### Completed performance dataset

```json
{
  "schemaVersion": 1,
  "source": "user-owned-completed-matches",
  "matches": [
    {
      "matchId": "demo-performance-001",
      "win": true,
      "kills": 10,
      "deaths": 2,
      "assists": 5,
      "teamTotalKills": 20,
      "totalDamageDealt": 30000.0,
      "teamTotalDamageDealt": 60000.0,
      "totalDamageTaken": 20000.0,
      "teamTotalDamageTaken": 100000.0,
      "goldEarned": 12000.0,
      "teamTotalGoldEarned": 60000.0
    }
  ]
}
```

### Team Elo dataset

```json
{
  "schemaVersion": 1,
  "source": "synthetic",
  "scope": {
    "operator": "synthetic",
    "realm": "demo-realm",
    "queue": "synthetic-queue",
    "patch": "demo-patch-1"
  },
  "matches": [
    {
      "matchId": "demo-rating-001",
      "timestamp": "2026-01-01T00:00:00+00:00",
      "teamA": ["synthetic:a1", "synthetic:a2", "synthetic:a3", "synthetic:a4", "synthetic:a5"],
      "teamB": ["synthetic:b1", "synthetic:b2", "synthetic:b3", "synthetic:b4", "synthetic:b5"],
      "winner": "A"
    }
  ]
}
```

A match may include its own `scope` object; it must exactly equal the dataset scope. Omit it to inherit.

Fixtures:

- `fixtures/scoring/performance-demo.json` — 12 completed matches, all metrics available.
- `fixtures/scoring/performance-missing-metrics.json` — zero/equality/missing cases.
- `fixtures/scoring/synthetic-rating-demo.json` — target player with 14 games and changing teams.
- `fixtures/scoring/synthetic-rating-disconnected.json` — two disconnected pools and a duplicate pair demonstration.

Fixtures are fabricated data. The demo generator produces the same shapes deterministically without file I/O.

## Library integration

```csharp
using LoLKarmaBook.Scoring;
using LoLKarmaBook.Scoring.Performance;

// Recent performance: either generate the demo or import completed match data.
var demo = SyntheticScenarioGenerator.CreatePerformanceDemo(12);
var score = PerformanceCalculator.Calculate(demo);
// Display score.FinalScore (null => unavailable), score.GameCount, score.Weights,
// score.MissingMetrics and score.AlgorithmVersion. Never render FinalScore == null as 0.

var imported = PerformanceJson.LoadFile("performance-demo.json"); // strict, <= 5 MiB
var importedScore = PerformanceCalculator.Calculate(imported);
```

Team Elo currently has no desktop wiring. It can be called through the library or CLI.

Handle `ScoringValidationException` by showing its `Code` and message; do not retry or coerce the input.

Expected fixture properties, useful for smoke checks:

| Call | Result |
| --- | --- |
| `CreatePerformanceDemo(12)` + `Calculate` | 12 games, 8 wins, all metrics present, `FinalScore` in (0, 100] |
| `fixtures/scoring/performance-demo.json` + `Calculate` | 12 games, 8 wins, no missing metrics |
| `fixtures/scoring/performance-missing-metrics.json` + `Calculate` | 4 games, available, weights 100, no missing metrics (rows combine to cover every metric) |
| `CreateRatingDemo(14)` target | 14 games, `IsProvisional == false`, connected pool 19 players |
| `fixtures/scoring/synthetic-rating-disconnected.json` | 2 connected pools of 10, 1 duplicate deduped, `DuplicateMatchCount == 1` |

## Verification

Commands run from the repository root:

```text
dotnet run --project tests/LoLKarmaBook.Scoring.Tests/LoLKarmaBook.Scoring.Tests.csproj -c Release
dotnet run --project src/LoLKarmaBook.Cli -c Release -- verify-performance-reference fixtures/scoring/reference-parity.json
```

The reference fixture contains 84 cases, compared with a tolerance of `1e-10`.

The case `finite-input-overflow` (finite inputs; conversion overflows) pins the clamp semantics:
legacy `clamp01(+Infinity) == 0` gives 59.07055555555556, whereas mapping `+Infinity` to 1 gives
99.25, so it fails that gate.

Duplicate-property cases exercised: duplicate top-level `schemaVersion` and `source` in both
formats (both orders), case-variant `Source`/`source`, duplicate nested `win`, `kills`, `winner`,
`teamA`, and duplicate keys inside the nested `scope` object. Each raises `ScoringValidationException`
with a `$.matches[0]`-style path, and each has a non-duplicated control document that still parses.

## Usage notes

- Source labels describe the dataset and are preserved in the imported/exported envelope.
- Scope fields keep calculations within one operator, realm, queue and patch; each match must inherit or match the dataset scope.
- The example fixtures contain fabricated data. Other datasets use the same structure and numeric validation.
- Rosters can come from simulated matches, a local JSON import, or the Riot module (`LoLKarmaBook.Riot`):
  account-v1 resolves the Riot ID to a PUUID, match-v5 supplies match IDs and detail documents, and
  `RiotMatchParser` reduces each match to exactly the roster + stat row shape this scoring layer
  consumes (team totals summed over the owner's own side). Supported maps include Summoner's Rift,
  Howling Abyss and Butcher's Bridge; remakes and incomplete 5v5 rosters are skipped. The Riot module is read-only,
  does not persist the API key, and has no dependency on the scoring layer's file format.
- Mainland China uses the signed-in local client's match history through `LoLKarmaBook.Client`,
  without an API key. Its scoring identifiers are hashes and its source includes the platform and selected queue.
- [Riot queue metadata](https://static.developer.riotgames.com/docs/lol/queues.json) identifies 2400 as
  ARAM: Mayhem; the mainland client reports this mode as KIWI on map 12.
