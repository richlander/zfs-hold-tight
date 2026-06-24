# zfs-hold-tight

**Hold your signal, prune the noise.** Tiered (daily / weekly / monthly) snapshot
retention with ZFS *hold* protection for OpenZFS pools — built for the defender side of
a pull-replication backup, where it is the only thing that ever deletes a snapshot.

It thins a feed of daily snapshots into a grandfather-father-son keep set (recent dailies
+ newest-per-ISO-week + newest-per-month), tags the keepers, protects monthlies with a
`zfs hold`, and destroys dailies past expiry — **but only when the feed is provably
healthy.** If anything looks off, it holds everything, deletes nothing, and alerts.

It **prunes, never creates** snapshots, so it is safe to run against a `zfs receive`
target (it never races zettarepl's pre-retention receive).

> Deleting is irreversible; refusing to delete is always safe. Every guard below is a
> reason *not* to delete. Full contract: [docs/SAFETY.md](docs/SAFETY.md).

## Why this exists — the problem it solves

`zfs-hold-tight` is for **backup machines that receive snapshots by replication**, as part
of a ransomware-resistant design: the backup box *pulls* from the source (it never accepts
an inbound push), has no inbound SSH, and keeps retention on its **own clock** so that an
attacker who compromises the source and runs `zfs destroy` can't delete history already on
the backup. Source-side pruning must not propagate.

That design hits a hard constraint:

> **A replication receive-target dataset can't also be where you create retained
> snapshots.** A `zfs receive` of an incremental first rolls the dataset back to the last
> common snapshot, destroying anything newer. So the obvious "let the backup machine take
> its own weekly/monthly snapshots" (e.g. TrueNAS Periodic Snapshot Tasks) doesn't survive
> the next pull — the replication engine's pre-retention step deletes them.

And replicating *multiple* snapshot families to get long-term coverage is fragile:
snapshot tools like Sanoid stamp every snapshot from one run with a single identical
timestamp (to the second), and a replication engine that orders by that timestamp can
mis-order same-second snapshots and **wedge replication**. The robust choice is to
replicate **one family — the dailies.**

That leaves a gap: the backup machine has a short window of dailies and **no long-term
retention**, with no safe way to create one. `zfs-hold-tight` fills it by thinning the
**already-replicated dailies in place** — "weekly" and "monthly" become *labels for dailies
it keeps*, never new snapshot objects. Long-term grandfather-father-son retention then
lives on the backup machine, on its own clock, independent of the source; and because it
only ever prunes snapshots at or before the replication base, it never collides with the
incremental receive.

**In short:** it is *not* a general snapshot manager like Sanoid/zfs-auto-snapshot (those
*create* snapshots, typically on the source). It is the defender-side **retention** half
for a pull-replication backup target where creating snapshots is impossible.

## How it works

Each run, per dataset:

1. List the daily snapshots (`autosnap_<ts>_daily`).
2. Run the [health guards](#safety-guards). If any fails, **HOLD** the dataset (no
   deletions) and alert.
3. Identify keepers: newest-per-ISO-week (weekly) and newest-per-month (monthly).
4. Tag keepers with `lander:tier` + `lander:keep_until`; `zfs hold lander:monthly` the
   monthlies. Plain dailies stay untagged (implicitly daily). Superseded keepers are
   demoted: tags inherited away, stale monthly hold released.
5. Destroy dailies past expiry that are fully covered by a surviving keeper — never the
   replication base, a keeper, or a held snapshot.

## Quick start

```bash
# 1. Configure
cp zfs-hold-tight.example.json zfs-hold-tight.json   # then edit datasets + retention

# 2. Build (native-AOT single binary; needs the .NET 10+ SDK + clang)
dotnet publish zfs-hold-tight.cs -r linux-x64 -c Release -o bin/

# 3. Dry-run (default: shows what would be pruned, deletes nothing)
./bin/zfs-hold-tight

# 4. Validate / create hold protection
./bin/zfs-hold-tight --doctor
./bin/zfs-hold-tight --fix

# 5. Prune for real (typically from cron)
./bin/zfs-hold-tight --prune
```

The binary looks for `zfs-hold-tight.json` next to itself (override with
`ZFS_HOLD_TIGHT_CONFIG`). It runs `zfs` from `PATH` and must run as a user able to
`zfs destroy`/`hold` the target datasets (usually root).

## Commands

| Command | What it does |
|---|---|
| *(none)* | Dry-run: report what would be pruned. **Deletes nothing.** |
| `--prune` | Prune + tag keepers + create monthly holds + ping healthchecks. |
| `--prune --force` | Bypass the per-run delete cap (for a reviewed bulk cleanup). |
| `-v`, `--verbose` | Log each snapshot decision. |
| `--doctor` | Report hold status: monthlies protected ✓ / missing ✗ / unexpected. |
| `--check-holds` | Flag holds on snapshots older than the longest retention. |
| `--fix` | Create any missing `lander:monthly` holds on monthly keepers. |
| `--hold-adhoc <snap>` | Defensive manual `lander:adhoc` hold on one snapshot. |
| `--hold-latest-tier <daily\|weekly\|monthly>` | Hold the newest snapshot of a tier. |
| `--hold-latest` | Shorthand for `--hold-latest-tier daily`. |
| `--dataset <name>` | Limit `--doctor`/`--fix`/`--prune` to one dataset. |
| `--no-hc` | Skip healthchecks pings for this run. |

Exit codes: `0` ok · `1` trouble (a dataset HELD or a destroy failed) · `2` bad
config/arguments · `3` another run holds the lock.

## Configuration

See [`zfs-hold-tight.example.json`](zfs-hold-tight.example.json). Top-level keys:

| Key | Default | Purpose |
|---|---|---|
| `logDir` | `/mnt/archive/tools/logs` | Daily log files (auto-created). |
| `healthchecksUrl` | *(unset → pings disabled)* | healthchecks.io base ping URL. |
| `guardMaxDeletePerRun` | `5` | Per-run delete cap (circuit-breaker; `--force` overrides). |
| `guardRecentWindowDays` | `7` | Require an unbroken daily window this many days back. |
| `guardMaxDailyAgeHours` | `26` | HOLD if the newest daily is older than this. |
| `retentionPolicies` | — | Named `{dailyKeepDays, weeklyKeepDays, monthlyKeepDays}` profiles. |
| `datasets` | — | `dataset → policy` map. Datasets must start with `archive/`. |

Environment overrides: `ZFS_HOLD_TIGHT_CONFIG`, `ZFS_HOLD_TIGHT_LOG_DIR`,
`ZFS_HOLD_TIGHT_HC_URL`.

> **Note:** the pool prefix (`archive/`) and the property/hold namespace (`lander:`) are
> compiled-in defaults. Edit the constants in `zfs-hold-tight.cs` to match your pool.

## Safety guards

Any failing guard HOLDs **that** dataset (healthy datasets still prune); a global fault
HOLDs everything. See [docs/SAFETY.md](docs/SAFETY.md) for the full contract.

1. **Clock sane** — no snapshot dated in the future.
2. **Newest daily fresh** — ≤ `guardMaxDailyAgeHours` (26h) old.
3. **Recent window complete** — no gaps in the last `guardRecentWindowDays` (7) days.
4. **Volume circuit-breaker** — never delete more than `guardMaxDeletePerRun` without `--force`.
5. **Population floor** — fewer dailies than the keep count ⇒ nothing to prune (noted).

Per-snapshot, it never deletes the replication base (newest daily), a week/month keeper,
an uncovered daily, or any held snapshot.

## Testing

```bash
tests/run-tests.sh                       # or: tests/run-tests.sh path/to/zfs-hold-tight.cs
```

The suite drives the tool against a fake `zfs` (`tests/fake-zfs`) — no real pool needed —
and asserts the core invariant (*no week/month keeper is ever destroyed*) plus every
guard, the hold lifecycle, the run-lock, and input validation. Requires the .NET SDK.

## License

[MIT](LICENSE).
