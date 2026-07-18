# zfs-hold-tight Safety Specification

`zfs-hold-tight` is the only component of the backup system that *deletes* snapshots, so
it is built to one principle:

> **Delete only when the system is provably in a healthy steady state. When anything
> looks off, keep everything, do nothing, and alert.**

Deleting is irreversible; refusing to delete is always safe. This document is the
contract the tool implements (`zfs-hold-tight.cs`). Every condition below is a reason
*not* to delete.

## Rules, briefly

Everything below elaborates and justifies these — this is the whole policy in plain
terms, in one place, before the detail:

- **Delete only when the system is provably healthy.** Anything ambiguous ⇒ keep
  everything, alert, do nothing destructive. (Guards 1–5.)
- **A plain daily survives until at least 2 newer held keepers exist.** That's the entire
  retention invariant — no day-count window, just "is it covered by something newer that
  will survive." (`minNewerHolds`, see Context below.)
- **Never delete more than 2x the weekly cadence (14) in one run without `--force`.** A
  routine week's batch is only ~6-7; an extra week of slack is cheap, and a flat cap beats
  trying to time it precisely. (Guard 6.)
- **A dataset's first-ever destructive prune always needs `--force`, no matter how
  small the backlog.** (A first prune with nothing yet doomed establishes on its own —
  there's nothing destructive to gate.) The routine cap above governs "is this normal,"
  not "is it OK to destroy anything here for the first time" — it isn't sized to gate
  that decision. (Guard 7.)
- **A day this tool legitimately pruned is not a feed gap.** The recent-window check
  (guard 1) only flags a day as missing if it's neither present *nor* already covered by
  the two-holds invariant — otherwise a routine weekly batch would trip its own gap alarm
  for days afterward. Coverage (is this day accounted for) and scale (is the amount of
  change unusual) are deliberately separate questions, answered by different guards.
- **This tool's guards protect its own pruning decision, not replication health.** Guard 1
  is not a feed/replication monitor and isn't meant to be one — "did last night's pull
  actually happen" is a separate, better-suited job already done by
  `orientation/health-check.md`'s `replication.query` check. Guard 1 only asks whether
  *this tool's own* retention math can be trusted right now.

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
   newest daily must each have a daily snapshot, *or be older than the current hold
   frontier* — a day this tool already legitimately pruned (per the two-holds invariant)
   isn't a gap, it's expected coverage, and doesn't count as missing.

   **Scope, explicitly: this guard protects only this tool's own pruning decision — it is
   not a replication/feed-health monitor, and isn't meant to be one.** Its question is
   narrow: "is the feed healthy enough that my retention math (which assumes continuous
   recent coverage) is trustworthy right now?" Once 2 newer keepers genuinely cover a day,
   that guarantee holds regardless of *why* the day itself is gone — pruned by this tool,
   or never replicated in the first place — because the guarantee is about the keepers
   that exist, not about whether every intervening daily was ever captured. Detecting
   *replication* health specifically — did last night's pull actually happen — is a
   separate, better-suited job already done elsewhere: `orientation/health-check.md` step
   1 checks `replication.query` state directly (every task `FINISHED`, `last_snapshot` is
   today's), which sees the replication task itself rather than inferring backward from
   snapshot presence. A transient replication gap that self-heals before it ages past the
   frontier could, in principle, go unflagged by this guard specifically — that's fine;
   it's not this guard's job, and the dedicated check would have already caught it.

   Without the frontier exclusion, this guard would fight its own tool's retention: a
   routine weekly batch destroys several of the days inside this window, and the very next
   run(s) would see those days "missing" and false-trip a feed-gap alert on data the tool
   itself correctly removed — this was a real, once-shipped bug (found by an independent
   second-model review after 10 rounds of the first reviewer missing it, since the old
   `guardMaxDeletePerRun: 5` never let a batch complete far enough to expose it) — see
   `tests/run-tests.sh` T42 for the reproduction. This is deliberately the *coverage*
   question ("is it safe that this day is gone") — kept distinct from the *scale* question
   ("is the amount of change unusual"), which is guard 6's job alone; a day still inside
   the window that's neither present nor past the frontier is a real gap. *(Load-bearing
   guard: the canonical trap is stale data plus freshly re-stamped weekly/monthly
   snapshots fooling a naive "keep newest N" into pruning real coverage.)*
2. **Newest daily fresh.** The most recent daily must be ≤ `guardMaxDailyAgeHours`
   (default 26h) old. A stale newest means replication isn't delivering.
3. **Clock sane.** No snapshot may be dated in the future, and `now` must be after the
   newest snapshot. A wrong clock would make `keep_until` expire everything at once.
   Snapshot-name timestamps are parsed as UTC (`AssumeUniversal`); a source stamping
   names in a zone *ahead* of UTC would make every snapshot look future-dated and trip
   this guard permanently — fails safe (WORRY, not a silent misinterpretation), but worth
   knowing if you ever see every dataset stuck on "clock?" after a source timezone change.
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
   `guardMaxDeletePerRun` (default 14 — 2x the 7-day ISO-week cadence) snapshots for one
   dataset without `--force`. A weekly keeper only becomes permanent when its ISO-week
   closes, so the dailies it stops covering become doomed together, once a week, not
   smoothly day by day — steady state is a ~6-7 batch every 7 days, not 0–1/day. The 2x
   multiplier gives one full extra week of slack (e.g. a missed run) before a batch is
   treated as abnormal rather than routine.
   **Considered and rejected: deriving the default from `minNewerHolds` (× 7 per policy),**
   on the theory that a deep-tail policy's (e.g. `archive/memories`, `minNewerHolds=24`)
   first-ever hold-frontier activation releases a backlog proportional to `minNewerHolds`.
   Simulated the actual algorithm for an organically-grown dataset and that premise is
   false: the max single-run doomed count stays ~6 regardless of `minNewerHolds`, because
   an organic dataset's own history never extends further back than its keeper count
   implies — there's no room for a backlog to build up behind the frontier. Confirmed
   against `archive/memories`' real snapshot history on the live deployment: its
   daily-tagged history starts exactly as far back as its snapshot count implies, one per
   day, no gap — genuinely organic, not a cold-started backlog. The one scenario that DOES
   release a real `minNewerHolds`-dependent burst — reducing `minNewerHolds` on an
   already-established dataset — isn't fixed by deriving from the new (smaller) N either.
   Don't re-derive this without re-simulating first.
   **Accepted tradeoff:** this guard is also the last line of defense against non-routine
   *steady-state* anomalies (a retention-policy misconfig, a frontier-calc bug, a
   keeper-history gap) — raising it from 5 to 14 raises that bar too, not just the
   routine-batch tolerance: a bug producing 6-14 unexpected doomed dailies (the exact
   boundary: `doomed.Count > MaxDeletePerRun`, so 14 itself still passes) now prunes
   silently instead of tripping WORRY, with no other check that re-narrows this bound.
   Deliberate: the guard's old resolution (5) never once passed a real routine week on
   this deployment, so it never actually functioned as a steady-state anomaly detector
   either — it only ever fired on the routine case. 14 is a resolution the tool can
   actually clear in normal operation, in exchange for coarser steady-state anomaly
   detection.
7. **Cold-start floor.** Independent of `guardMaxDeletePerRun`, a dataset this tool has
   never established requires `--force` for **any** nonzero doomed count, not just a
   large one — guard 6's cap governs "is this ROUTINE," not "is it OK to destroy
   anything here for the first time," so it is not sized to gate that decision (a
   pre-existing backlog short enough to stay under guard 6's cap — no fixed day count is a
   safe cutoff, see docs/TRUENAS.md — would otherwise clear silently on the very first
   plain `--prune` with no review). "Established" = at least
   one snapshot already carries a real `<ns>:tier` value — normally this is *only* ever
   written by Promote, but the check (`zfs list`) reads whatever value is currently
   visible, including one that arrived by ZFS property **inheritance** (someone runs
   `zfs set <ns>:tier=weekly` on a *parent* dataset) or a **received** property (carried
   over by replication, if the source ever had the same namespace value set on it) rather
   than this tool's own Promote call. Both require a human to have set the property
   somewhere in the hierarchy — not a replication-driven attack path — but the floor
   doesn't currently distinguish "Promote wrote this" from "this is merely visible here,"
   so a stray `zfs set` on a parent dataset could unintentionally waive the floor. Not
   fixed: doing so requires checking property *source* (`zfs get -s local`), which needs a
   second, non-batched `zfs` call per dataset — judged not worth the cost for a
   self-inflicted misconfiguration, not a scenario this tool's threat model is defending
   against (see `truenas/README.md`'s residual risks in the orientation repo).
   Checked and enforced *before* Promote runs, so a blocked cold-start pass can never tag
   a keeper and thereby clear itself without `--force`. A dataset establishes the first
   time `--prune` completes for it: immediately, with no `--force` needed, if it has
   nothing doomed yet (Promote only tags/holds — nothing to review); via `--force` the
   first time it does have something doomed. After that one completion, every later
   unforced run is governed by the routine `guardMaxDeletePerRun` cap instead.
8. **Population floor.** If a dataset has fewer dailies than its keep count, nothing is
   beyond the window — log an informational note and proceed (there's nothing to prune
   yet anyway); a sudden drop is itself suspicious.

A broken source feed shows up as stale/gappy dailies, so guards 1–2 enforce replication
health by proxy.

**A `SKIP` is not passive — protection only ever increases on a worry.** When any guard above
withholds (`SKIP`s) a dataset, the tool still proactively `zfs hold`s every keeper
(`<ns>:monthly` / `<ns>:weekly`, including the suspect newest where applicable). It
**never releases a hold or deletes anything** on a worried run — a release/delete only
ever happens on a fully-healthy, under-cap run. Guards 1–5 additionally write a
`<ns>:worry=<date> <reason>` breadcrumb on the tip; guards 6 and 7 (the volume
circuit-breaker and cold-start floor) do not — they're visible via their `WORRY` log line
and healthchecks `/fail` ping instead, not a snapshot property.

> **Detection vs. recovery.** Guards 4–5 are best-effort *early warning* — they alert and
> stop pruning the granular recent dailies, but a patient low-and-slow attack (a few % a
> day) evades both. The actual protection for deletion *and* encryption is **retention**:
> pre-incident keepers survive defender-side for years (independent of the source), so you
> recover by rolling back to one as long as you notice within the retention window.

## Per-snapshot protections — never delete a snapshot that is…

9. **The newest daily** — the replication incremental base. Absolute, always.
10. **Within the daily tail** — a plain daily survives until at least `minNewerHolds`
    newer held keepers exist (default `2`).
11. **A week keeper** — newest daily in its ISO-week, within weekly retention.
12. **A month keeper** — newest daily in its month, within monthly retention.
13. **Not yet covered** — a daily is deleted only once the derived destroy depth of
    newer held keepers covers it. Never open a coverage hole.
14. **Held** — any foreign/defensive `zfs hold` (e.g. `<ns>:adhoc`) makes the snapshot
    off-limits; it is skipped, never forced. The tool's own managed keeper holds
    (`<ns>:monthly` / `<ns>:weekly`) are released only for demotion or immediately before
    destroying an expired keeper. (And if `zfs holds` itself fails, the dataset is
    `SKIP`ped (`ERROR`) rather than pruned while blind to holds.)
15. **Not a recognized `autosnap_<ts>_daily`** — legacy `_weekly`/`_monthly`, manual, and
    unparseable names are out of scope (and an unparseable date skips that one snapshot,
    never aborts the run).

## Operational guarantees

16. **Dry-run by default.** Deletion happens only with `--prune`.
17. **Per-dataset isolation.** A guard tripping on one dataset HOLDs only that dataset;
    healthy datasets still prune. A global fault (e.g. a bad clock) HOLDs everything.
18. **Single run at a time.** A mutating run takes an exclusive lock; a second overlapping
    run exits without touching anything.
19. **Always logs; alerts on trouble.** Every run appends a timestamped record to the log
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
