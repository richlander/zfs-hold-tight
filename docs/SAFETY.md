# zfs-hold-tight Safety Specification

`zfs-hold-tight` is the only component of the backup system that *deletes* snapshots, so
it is built to one principle:

> **Delete only when the system is provably in a healthy steady state. When anything
> looks off, keep everything, do nothing, and alert.**

Deleting is irreversible; refusing to delete is always safe. This document is the
contract the tool implements (`zfs-hold-tight.cs`). Every condition below is a reason
*not* to delete.

## Context

The intended deployment pulls only `_daily` snapshots from a source via replication.
`zfs-hold-tight` thins those dailies on the **defender side** into a
grandfather-father-son keep set — recent dailies + one per ISO-week + one per month —
tagging each keeper with `<ns>:keep_until` / `<ns>:tier` (`<ns>` = `propertyNamespace`,
default `zht`) and destroying dailies whose
keep_until has passed. It never *creates* snapshots: creating one ahead of the
replication base is what a `zfs receive` pre-retention step would destroy; pruning old
snapshots never conflicts.

## Run-level guards — any guard ⇒ `WORRY` + `SKIP` that dataset (delete nothing), and alert

Evaluated **independently per dataset**, anchored on the newest daily, not wall-clock and
not aggregated across datasets — so an attack confined to one small dataset is judged on
that dataset's own baseline and flagged on its own. Each threshold below is a global
default that a retention policy may override (so a write-once media policy can tighten the
content guards while a churny scratch policy relaxes them).

1. **Recent window complete.** The `guardRecentWindowDays` (default 7) days ending at the
   newest daily must each have a daily snapshot — no gaps. *(Load-bearing guard: the
   canonical trap is stale data plus freshly re-stamped weekly/monthly snapshots fooling
   a naive "keep newest N" into pruning real coverage.)*
2. **Newest daily fresh.** The most recent daily must be ≤ `guardMaxDailyAgeHours`
   (default 26h) old. A stale newest means replication isn't delivering.
3. **Clock sane.** No snapshot may be dated in the future, and `now` must be after the
   newest snapshot. A wrong clock would make `keep_until` expire everything at once.
4. **No sudden data shrinkage.** If the newest daily references more than
   `guardMaxDailyShrinkPct` (default 33%) less data than the recent baseline median, HOLD
   — a likely mass deletion on the source. Catches outright **deletion** (`referenced`
   drops). Set to 0 to disable.
5. **No excessive churn.** If the newest daily's `written` (bytes changed since the prior
   snapshot) exceeds `guardMaxDailyChurnPct` (default 50%) of the dataset's `referenced`
   size, HOLD — a likely mass rewrite / **in-place encryption** (which guard 4 misses,
   since encryption preserves size while scrambling contents). Heuristic: legitimately
   churny datasets will trip it, so relax/disable it per policy. Set to 0 to disable.
6. **Volume circuit-breaker.** A single run may not delete more than
   `guardMaxDeletePerRun` (default 5) snapshots for one dataset without `--force`. Steady
   state deletes 0–1/day; a large number signals an abnormal state or a logic bug.
7. **Population floor.** If a dataset has fewer dailies than its keep count, nothing is
   beyond the window — skip and note it; a sudden drop is itself suspicious.

A broken source feed shows up as stale/gappy dailies, so guards 1–2 enforce replication
health by proxy.

**A `SKIP` is not passive — protection only ever increases on a worry.** When any guard above
withholds (`SKIP`s) a dataset, the tool still proactively `zfs hold`s every keeper
(`<ns>:monthly` / `<ns>:weekly`, including the suspect newest where applicable) and writes a
`<ns>:worry=<date> <reason>` breadcrumb on the tip. It **never releases a hold or deletes
anything** on a worried run — a release/delete only ever happens on a fully-healthy,
under-cap run.

> **Detection vs. recovery.** Guards 4–5 are best-effort *early warning* — they alert and
> stop pruning the granular recent dailies, but a patient low-and-slow attack (a few % a
> day) evades both. The actual protection for deletion *and* encryption is **retention**:
> pre-incident keepers survive defender-side for years (independent of the source), so you
> recover by rolling back to one as long as you notice within the retention window.

## Per-snapshot protections — never delete a snapshot that is…

8. **The newest daily** — the replication incremental base. Absolute, always.
9. **Within the daily tail** — a plain daily survives until at least `minNewerHolds`
    newer held keepers exist (default `2`).
10. **A week keeper** — newest daily in its ISO-week, within weekly retention.
11. **A month keeper** — newest daily in its month, within monthly retention.
12. **Not yet covered** — a daily is deleted only once the derived destroy depth of
    newer held keepers covers it. Never open a coverage hole.
13. **Held** — any foreign/defensive `zfs hold` (e.g. `<ns>:adhoc`) makes the snapshot
    off-limits; it is skipped, never forced. The tool's own managed keeper holds
    (`<ns>:monthly` / `<ns>:weekly`) are released only for demotion or immediately before
    destroying an expired keeper. (And if `zfs holds` itself fails, the dataset is
    `SKIP`ped (`ERROR`) rather than pruned while blind to holds.)
14. **Not a recognized `autosnap_<ts>_daily`** — legacy `_weekly`/`_monthly`, manual, and
    unparseable names are out of scope (and an unparseable date skips that one snapshot,
    never aborts the run).

## Operational guarantees

15. **Dry-run by default.** Deletion happens only with `--prune`.
16. **Per-dataset isolation.** A guard tripping on one dataset HOLDs only that dataset;
    healthy datasets still prune. A global fault (e.g. a bad clock) HOLDs everything.
17. **Single run at a time.** A mutating run takes an exclusive lock; a second overlapping
    run exits without touching anything.
18. **Always logs; alerts on trouble.** Every run appends a timestamped record to the log
    directory (and stdout). On `--prune` it pings healthchecks.io on a clean run, and
    `<url>/fail` (with diagnostics) whenever any guard HOLDs a dataset or a destroy fails
    — so an unhealthy-but-not-pruning state is **visible, never silent**.

## Worked example (the trap guards 1–2 prevent)

The source's daily feed stalls: the newest daily is 6 days old, but a config change just
re-stamped fresh weekly/monthly snapshots. A naive "keep newest 30 dailies + keepers"
could begin deleting older dailies — destroying real coverage that nothing is replacing.
Guard 1 (6-day gap) and guard 2 (stale newest) both fail, so `zfs-hold-tight` deletes
nothing and pings `/fail`. The operator sees a stalled feed instead of slow, silent data
loss.
