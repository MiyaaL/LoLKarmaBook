# Team-Elo calculation

This folder holds `synthetic-team-elo-1.0.0`, a deterministic team-Elo calculator.
The existing public type names retain their `Synthetic` prefix for API compatibility.

`SyntheticRatingDataset.Source` is a non-empty, trimmed source label of at most 120
characters without control characters. Labels such as `synthetic`, `live` and `manual`
are accepted and preserved during JSON import and export. `synthetic` remains the
default for existing examples.

Schema version, scope consistency, team composition, player IDs, timestamps and
duplicate-match validation remain in place. Each team contains five distinct IDs,
the teams may not overlap, identical duplicate matches are deduplicated, and
conflicting duplicate matches are rejected.

`SyntheticRatingResult` contains player ratings, per-match changes, connected-pool
statistics, match counts and `AlgorithmVersion`.

The desktop has four pages and currently has no rating page. The library is exercised
by the scoring tests and the CLI's `rating-demo` command.
