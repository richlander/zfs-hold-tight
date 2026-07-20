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
4. Tag keepers with `<ns>:tier` + `<ns>:keep_until`; `zfs hold <ns>:monthly` the monthlies
   and `<ns>:weekly` the weekly-only keepers (`<ns>` = `propertyNamespace`, default `zht`).
   Plain dailies stay untagged. Superseded keepers are demoted: tags inherited away, stale
   managed holds released.
5. Destroy plain dailies only after at least `minNewerHolds` newer held keepers exist
   (default `3`) **and** the daily is itself older than `dailyKeepDays` (default `30`) — a
   calendar floor that bounds how much daily-granularity history a single ISO-week close
   can collapse in one run. Never delete the replication base, a keeper within retention,
   or a held snapshot.

## Quick start

```bash
# 1. Configure
cp zfs-hold-tight.example.json zfs-hold-tight.json   # then edit datasets + retention

# 2. Build a native-AOT single binary (see "Building" — deploying to TrueNAS needs a
#    glibc-matched container build)
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
`ZFS_HOLD_TIGHT_CONFIG`). It runs `zfs` from a resolved **absolute path** (`zfsPath` or a
standard location — see [Hardening](#configuration)), not the inherited `PATH`, and must
run as a user able to `zfs destroy`/`hold` the target datasets (usually root).

## Commands

| Command | What it does |
|---|---|
| *(none)* | Dry-run: report what would be pruned. **Deletes nothing.** |
| `--prune` | Prune + tag keepers + create monthly/weekly keeper holds + ping healthchecks. |
| `--prune --force` | Bypass the per-run delete cap (for a reviewed bulk cleanup). |
| `-v`, `--verbose` | Log each snapshot decision. |
| `--doctor` | Report keeper hold status: protected ✓ / missing ✗ / unexpected. |
| `--summary [--since YYYY-MM-DD]` | One-line-per-dataset markdown table (monthlies/weeklies/dailies retained, date range, feed continuity) — for pasting into a gist/PR/chat, not a replacement for `--doctor`'s per-keeper detail. `--since` narrows the window; omitted, it defaults to the oldest snapshot actually retained. |
| `--check-holds` | Flag holds on snapshots older than their dataset's own retention. |
| `--fix` | Create missing managed holds on monthly and weekly keepers. |
| `--hold-adhoc <snap>` | Defensive manual `<ns>:adhoc` hold on one snapshot. |
| `--hold-latest-tier <daily\|weekly\|monthly>` | Hold the newest snapshot of a tier. |
| `--hold-latest` | Shorthand for `--hold-latest-tier daily`. |
| `--dataset <name>` | Limit `--doctor`/`--summary`/`--fix`/`--prune` to one dataset. |
| `--no-hc` | Skip healthchecks pings for this run. |
| `--version` | Print the tool version and exit. |

`--summary`'s Continuity column uses the same narrow scope as guard 1 (see
[Safety guards](#safety-guards) below): it flags a missing day only within the current
daily tail (back to the persisted `<ns>:pruned_through` watermark — a fact about past
`--prune` runs, not a recomputation from current policy; see `docs/MODEL.md`), and never
flags a day already certified there or gone through normal GFS thinning. It's a
feed-health signal scoped to this tool's own pruning decision, not a replication-health
check — see `orientation/health-check.md`'s replication query for that.

Exit codes: `0` ok · `1` trouble (a dataset `SKIP`ped or a destroy failed) · `2` bad
config/arguments · `3` another run holds the lock.

## Configuration

See [`zfs-hold-tight.example.json`](zfs-hold-tight.example.json). Top-level keys:

| Key | Default | Purpose |
|---|---|---|
| `logDir` | `/mnt/archive/tools/logs` | Daily log files (auto-created). |
| `healthchecksUrl` | *(unset → pings disabled)* | healthchecks.io base ping URL. |
| `guardMaxDeletePerRun` | `14` | Per-run delete cap (circuit-breaker; `--force` overrides). 2x the 7-day ISO-week cadence — sized for the case where the coverage frontier binds and a weekly keeper's dailies become doomed as a ~6-7 batch once a week; when `dailyKeepDays` binds instead (the shipped `baseline` policy), destruction smooths to ~1/day and never comes close to this cap. |
| `guardRecentWindowDays` | `7` | Require an unbroken daily window this many days back. |
| `guardMaxDailyAgeHours` | `26` | `WORRY`+skip if the newest daily is older than this. |
| `guardMaxDailyShrinkPct` | `33` | `WORRY`+skip if the newest daily references this % less data than the recent baseline (mass-**deletion** guard; `0` disables). |
| `guardMaxDailyChurnPct` | `50` | `WORRY`+skip if the newest daily rewrites more than this % of the dataset's data in one day (mass-**rewrite / encryption** guard; `0` disables). |
| `propertyNamespace` | `zht` | Prefix for the tool's ZFS user-properties + hold tags (`<ns>:keep_until`, `<ns>:tier`, `<ns>:monthly`, `<ns>:weekly`, `<ns>:adhoc`, `<ns>:worry`, `<ns>:pruned_through`). |
| `zfsPath` | auto (`/usr/sbin/zfs`…) | Absolute path to the `zfs` binary. Resolved from standard locations; set explicitly to avoid trusting the (root cron's) `PATH`. |
| `curlPath` | auto (`/usr/bin/curl`…) | Absolute path to `curl` (healthchecks only). |
| `minNewerHolds` | `3` | Two-holds invariant (misnomer kept for compatibility — default is now 3): a plain daily drops only after this many newer held keepers (weekly/monthly grandfathers) exist. A held keeper only ever advances into the 2nd-newest slot at an ISO-week close, so at `minNewerHolds=2` a whole week's worth of plain dailies sit at "exactly 1 newer hold" and cross to "doomed" together the instant the next week's keeper appears; 3 buys a full extra week of buffer. |
| `retentionPolicies` | — | Named `{dailyKeepDays, weeklyKeepDays, monthlyKeepDays}` profiles; policies may override `minNewerHolds`. **`dailyKeepDays` is a real calendar floor**: a plain daily is destroyed only once it clears *both* the two-holds coverage invariant above *and* is older than `dailyKeepDays` — the floor bounds how much daily-granularity history a single ISO-week close can collapse in one run (see [Safety guards](#safety-guards) and `docs/SAFETY.md`'s "Detection vs. recovery"). |
| `datasets` | — | `dataset → policy` map. Datasets must start with `archive/`. |

Environment overrides: `ZFS_HOLD_TIGHT_CONFIG`, `ZFS_HOLD_TIGHT_LOG_DIR`,
`ZFS_HOLD_TIGHT_HC_URL`.

**Guards are evaluated independently per dataset** — a small dataset is judged on its own
recent baseline, so an 80% wipe of a 100 MB dataset trips its guard regardless of how much
other data exists. The `guard*` values above are global **defaults**; any of them may be
**overridden inside a retention policy** (e.g. tighten the content guards for write-once
media, or disable churn for a scratch dataset). Per-policy + a one-dataset policy ⇒
effectively per-dataset thresholds when you need them.

> **Note:** the property/hold namespace is set by `propertyNamespace` (default `zht`). The
> pool prefix (`archive/`) is still a compiled-in constant in `zfs-hold-tight.cs` — edit it
> to match your pool.

> **Hardening:** the tool runs `zfs` from an absolute path (resolved, or `zfsPath`) rather
> than the inherited `PATH`, so a writable earlier `PATH` element can't hijack it when run
> as root. As defense-in-depth, also pin a minimal `PATH` in the cron entry
> (e.g. `PATH=/usr/sbin:/usr/bin`), and leave `healthchecksUrl` unset on hardened hosts if
> you don't need pings (avoids invoking `curl` at all).

## Safety guards

Any failing guard HOLDs **that** dataset (healthy datasets still prune); a global fault
HOLDs everything. See [docs/SAFETY.md](docs/SAFETY.md) for the full contract.

1. **Clock sane** — no snapshot dated in the future.
2. **Newest daily fresh** — ≤ `guardMaxDailyAgeHours` (26h) old.
3. **Recent window complete** — no gaps in the last `guardRecentWindowDays` (7) days, other
   than days already legitimately pruned per the two-holds invariant (those aren't gaps).
   Scoped to this tool's own pruning safety, not replication health in general — see
   docs/SAFETY.md guard 1.
4. **No sudden data shrinkage** — newest daily references ≥ `guardMaxDailyShrinkPct` (33%) less than the recent baseline ⇒ HOLD (mass-**deletion** guard).
5. **No excessive churn** — newest daily rewrites > `guardMaxDailyChurnPct` (50%) of the dataset in one day ⇒ HOLD (mass-**rewrite / encryption** guard).
6. **Volume circuit-breaker** — never delete more than `guardMaxDeletePerRun` without `--force`.
7. **Cold-start floor** — a dataset that has never been established requires `--force` for
   *any* nonzero doomed count, regardless of `guardMaxDeletePerRun`; it establishes on its
   first completed `--prune` (no `--force` needed if nothing was doomed yet).
8. **Population floor** — fewer held keepers than the derived destroy depth ⇒ nothing to prune (noted).

Per-snapshot, it never deletes the replication base (newest daily), a week/month keeper
**while within its retention window**, a plain daily with fewer than `minNewerHolds`
newer held keepers, a plain daily younger than `dailyKeepDays`, or any foreign/defensive
held snapshot. A keeper *past* its retention ages out (so your recovery window is bounded
by `weeklyKeepDays` / `monthlyKeepDays`, not infinite).

> **On a worry, protection only ever increases.** When a guard HOLDs a dataset, the tool
> still proactively `zfs hold`s every keeper (`<ns>:monthly` / `<ns>:weekly`, including
> the suspect newest where applicable). It never releases a hold or deletes anything on a
> worried run. Guards 1-5 additionally record a `<ns>:worry=<date> <reason>` breadcrumb
> on the tip; guards 6/7 (volume circuit-breaker, cold-start floor) surface only via the
> `WORRY` log line and the healthchecks `/fail` ping, not a snapshot property.

> **`--force` removes the per-run delete throttle entirely** — use it deliberately for a
> reviewed bulk cleanup (e.g. a first run against a large backlog), then return to plain
> `--prune`. Steady state does a ~6-7 delete batch once every 7 days (when a weekly
> keeper's ISO-week closes) *if* `dailyKeepDays` is shorter than the coverage frontier's
> reach; with the shipped `baseline` policy (`dailyKeepDays=30` vs. a ~3-week frontier at
> the default `minNewerHolds=3`), the calendar floor binds instead and it's closer to a
> smooth 0–1/day trickle — plus a handful of metadata writes daily either way. A first run
> after introducing the tool (or after a long gap) does more — it tags each keeper and
> clears tags off any previously-tagged dailies — but still one
> `zfs list`/`holds` per dataset, not per snapshot.

## Reading the logs

Every run appends to `${logDir}/zfs-hold-tight-YYYY-MM-DD.log` (and stdout), with a header
that records the exact build: `=== zfs-hold-tight 1.0.0+<sha> <ts>Z  mode=PRUNE ===`. Each
action is a **distinct, greppable token** (the snapshot's full name follows it):

Tokens follow a `RESOURCE:ACTION` grammar so each line names what changed and how. The
snapshot's full name follows the token.

**Mutation tokens** — routine, countable, the steady-state bookkeeping:

| token | meaning | grep |
|---|---|---|
| `HOLD:ADD` | a `zfs hold` was placed (monthly keeper protected) | `grep HOLD:ADD` |
| `HOLD:REMOVE` | a hold was released (demotion / weekly-downgrade / before destroy) | `grep HOLD:REMOVE` |
| `META:ADD` | tier/keep_until tags written on a newly-tagged keeper | `grep META:ADD` |
| `META:UPDATE` | an existing keeper's tag changed (e.g. weekly→monthly promotion), or the dataset's `<ns>:pruned_through` watermark advanced | `grep META:UPDATE` |
| `META:REMOVE` | tags inherited away on demotion | `grep META:REMOVE` |
| `SNAP:DESTROY` | a snapshot was **pruned** (irreversible) | `grep SNAP:DESTROY` |

**Exception tokens** — bare, single-word; a dataset `SKIP` verdict is always preceded by its
reason (`WORRY` or `ERROR`), and a failed individual op carries a `FAILED` suffix:

| token | meaning | grep |
|---|---|---|
| `WORRY` | a guard tripped (stale/gap/clock/shrink/churn/over-cap) — keepers protected, nothing deleted | `grep WORRY` |
| `ERROR` | a `zfs` verification call (list/holds) failed — cannot proceed safely | `grep ERROR` |
| `FAILED` | an individual op (hold/set/inherit/destroy) returned nonzero | `grep FAILED` |
| `SKIP` | the dataset was withheld this run (verdict; see the `WORRY`/`ERROR` above it) | `grep SKIP` |

So "did anything get deleted this week?" → `grep SNAP:DESTROY logs/*.log`; "did anything go
wrong?" → `grep -E 'WORRY|ERROR|FAILED|SKIP' logs/*.log` (**empty = all clear**); "which build
ran?" → `grep '^===' logs/*.log`. Invariants worth knowing: `HOLD:ADD == META:ADD/UPDATE` on
monthlies, `HOLD:REMOVE == META:REMOVE` on demotions, and `SNAP:DESTROY` count matches the
`PRUNED: kept N, destroyed X/Y` summary.

## Building

A native-AOT build produces a single self-contained binary with instant startup and no
runtime dependency. It needs the **.NET 10+ SDK** plus **clang** and zlib headers.

```bash
dotnet publish zfs-hold-tight.cs -r linux-x64 -c Release -o bin/    # -> bin/zfs-hold-tight
```

`--version` reports `1.0.0+<git-sha>` — the SDK reads the committed `HEAD` from `.git`
and embeds it (no extra flags). **Commit before building** a binary you intend to deploy,
or the embedded SHA points at the parent commit, not the code you built.

**Deploying to an older target (e.g. TrueNAS): build in a glibc-matched container.** AOT
emits a normal dynamically-linked binary that links the *build host's* glibc, and glibc
is forward- but not backward-compatible — a binary built on glibc 2.39 won't start on an
older system. Build inside Microsoft's AOT SDK image (Debian 12-based, glibc 2.36, with
clang preinstalled) so the result runs on any glibc ≥ 2.36:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0-aot \
  dotnet publish zfs-hold-tight.cs -r linux-x64 -c Release -o out
objdump -T out/zfs-hold-tight | grep -oE 'GLIBC_[0-9.]+' | sort -V | tail -1   # must be ≤ target glibc
```

## Running on TrueNAS

TrueNAS SCALE is the primary target. The OS is a read-only appliance image, so the binary
must run from a **pool dataset with `exec=on`**, not the OS — and is deployed by hand
(pull-only threat model). Build compatibility is **TrueNAS SCALE 23.10 "Cobia" and later**
(Debian 12 / glibc 2.36). Full guide — dataset setup, staged first run, cron in the wake
window: **[docs/TRUENAS.md](docs/TRUENAS.md)**.

## Testing

```bash
tests/run-tests.sh                       # or: tests/run-tests.sh path/to/zfs-hold-tight.cs
```

The suite drives the tool against a fake `zfs` (`tests/fake-zfs`) — no real pool needed —
and asserts the core invariant (*no week/month keeper is destroyed while within retention*)
plus every
guard, the hold lifecycle, the run-lock, and input validation. Requires the .NET SDK.

## License

[MIT](LICENSE).
