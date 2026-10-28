using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using LoLKarmaBook.Core;
using LoLKarmaBook.Desktop.Infrastructure;

namespace LoLKarmaBook.Desktop.Services;

/// <summary>
/// Remembers which synthetic sessions the user has explored, per profile, so an
/// app restart does not lose the "Match A → Match B" continuity. This file holds
/// no note text: only synthetic ids, a timestamp and the chosen mode.
///
/// Synthetic session ids come from <see cref="SyntheticScenario"/> and are
/// identical across profiles, so the journal must be profile-scoped.
/// </summary>
public sealed class SessionStore
{
    private readonly string _path;
    private readonly DiagnosticLog _log;
    private readonly object _gate = new();

    public SessionStore(string directory, string profileId, DiagnosticLog log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, $"sessions-{profileId}.json");
        _log = log;
    }

    public string FilePath => _path;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public State Load() => LoadState();

    public sealed class State
    {
        /// <summary>null | "a" | "b". Determines the next synthetic snapshot number.</summary>
        public string? CurrentScenario { get; set; }

        /// <summary>Bumped on every session switch so sequences never repeat.</summary>
        public long Sequence { get; set; }

        public DateTimeOffset? LastSwitchedAt { get; set; }

        /// <summary>Snapshot actually displayed, re-read only when re-opening the tab.</summary>
        public MatchSession? Current { get; set; }

        public MatchSession? Previous { get; set; }
    }

    private sealed class Persisted
    {
        public int SchemaVersion { get; set; } = 1;
        public string? CurrentScenario { get; set; }
        public long Sequence { get; set; }
        public DateTimeOffset? LastSwitchedAt { get; set; }
        public PersistedSession? Current { get; set; }
        public PersistedSession? Previous { get; set; }
    }

    private sealed class PersistedSession
    {
        public string? Scenario { get; set; }
        public long Sequence { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
    }

    public State LoadState()
    {
        var state = new State();
        if (!File.Exists(_path))
        {
            return state;
        }

        try
        {
            Persisted? persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path), ReadOptions);
            if (persisted is null || persisted.SchemaVersion != 1)
            {
                return state;
            }

            state.CurrentScenario = NormalizeScenario(persisted.CurrentScenario);
            state.Sequence = Math.Max(0, persisted.Sequence);
            state.LastSwitchedAt = persisted.LastSwitchedAt;
            state.Current = ToSession(persisted.Current);
            state.Previous = ToSession(persisted.Previous);

            // A persisted sequence must always dominate the restored sessions.
            foreach (PersistedSession? item in new[] { persisted.Current, persisted.Previous })
            {
                if (item is not null)
                {
                    state.Sequence = Math.Max(state.Sequence, item.Sequence);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            // Corrupt journal: behave like a first run rather than failing to start.
            _log.Warn("sessions", "会话记录无法读取，已忽略。", ex);
        }

        return state;
    }

    public void Save(State state)
    {
        var persisted = new Persisted
        {
            CurrentScenario = state.CurrentScenario,
            Sequence = state.Sequence,
            LastSwitchedAt = state.LastSwitchedAt,
            Current = ToPersisted(state.Current),
            Previous = ToPersisted(state.Previous)
        };

        lock (_gate)
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(persisted, JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
    }

    public void Delete()
    {
        lock (_gate)
        {
            try
            {
                File.Delete(_path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("sessions", "会话记录删除失败。", ex);
            }
        }
    }

    private static MatchSession? ToSession(PersistedSession? item)
    {
        if (item?.Scenario is null)
        {
            return null;
        }

        long sequence = Math.Max(1, item.Sequence);
        DateTimeOffset observedAt = item.ObservedAt == default
            ? new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero)
            : item.ObservedAt;

        SyntheticScenario scenario = new(observedAt);
        return item.Scenario switch
        {
            "a" => BuildMatchA(scenario, sequence),
            "b" => BuildMatchB(scenario, sequence),
            _ => null
        };
    }

    private static PersistedSession? ToPersisted(MatchSession? session)
    {
        if (session is null)
        {
            return null;
        }

        string? scenario = session.LocalSessionId switch
        {
            SyntheticScenario.MatchALocalSessionId => "a",
            SyntheticScenario.MatchBLocalSessionId => "b",
            _ => null
        };

        return scenario is null
            ? null
            : new PersistedSession
            {
                Scenario = scenario,
                Sequence = session.Sequence,
                ObservedAt = session.ObservedAt
            };
    }

    /// <summary>
    /// Match A as a fresh snapshot. Core's scenario always reports sequence 1; the
    /// desktop advances the sequence on every switch so SessionRegistry rejects
    /// stale snapshots instead of accepting an identical generation.
    /// </summary>
    public static MatchSession BuildMatchA(SyntheticScenario scenario, long sequence) =>
        new(
            scenario.MatchA.LocalSessionId,
            scenario.MatchA.SourceKind,
            scenario.MatchA.ProviderInstanceId,
            scenario.MatchA.Generation,
            sequence,
            scenario.MatchA.ObservedAt,
            scenario.MatchA.Phase,
            scenario.MatchA.QueueId,
            scenario.MatchA.MapId,
            scenario.MatchA.ExternalMatchId,
            scenario.MatchA.RosterComplete,
            scenario.MatchA.Participants);

    public static MatchSession BuildMatchB(SyntheticScenario scenario, long sequence) =>
        new(
            scenario.MatchB.LocalSessionId,
            scenario.MatchB.SourceKind,
            scenario.MatchB.ProviderInstanceId,
            scenario.MatchB.Generation,
            sequence,
            scenario.MatchB.ObservedAt,
            scenario.MatchB.Phase,
            scenario.MatchB.QueueId,
            scenario.MatchB.MapId,
            scenario.MatchB.ExternalMatchId,
            scenario.MatchB.RosterComplete,
            scenario.MatchB.Participants);

    private static string? NormalizeScenario(string? value) =>
        value is "a" or "b" ? value : null;
}
