# Building for and running on TrueNAS SCALE

`zfs-hold-tight` is built for TrueNAS SCALE acting as a pull-replication backup target.
This covers building a compatible binary, putting it somewhere it can actually run, and
scheduling it.

## 0. Replication settings this assumes — verify these FIRST

`zfs-hold-tight`'s ransomware value rests on one property: **it is the *only* thing that
deletes snapshots on the backup, and source-side deletions never reach it.** A wrong
replication setting defeats that before this tool ever runs. On each replication task that
feeds your `archive/*` datasets, verify:

| Setting | Required value | Why |
|---|---|---|
| **Retention policy** | **`Custom`**, lifetime ≥ your longest `*KeepDays` (e.g. 5y) — **never `Same as Source`** | `Same as Source` makes source pruning/`zfs destroy` **propagate** to the backup — the exact attack this design defends against. `Custom` keeps snapshots on the backup's own clock. Set it long enough that the *replication task itself* never deletes inside your retention window — `zfs-hold-tight` owns the thinning. |
| **Direction** | **Pull** (TrueNAS connects out and runs `zfs send` on the source) | TrueNAS must never accept an inbound `zfs receive`/push. A stolen source credential then can't write to the backup. |
| **Source replication user** | read-only `zfs send` / `hold` only (`zfs allow`) | Can't be used to delete or write source or destination. |
| **Periodic Snapshot Tasks on `archive/*`** | **none** | zettarepl's pre-retention receive destroys any destination snapshot newer than the last common one, so locally-created snapshots don't survive — and retention is `zfs-hold-tight`'s job, not theirs. |
| **`allow_from_scratch`** | **off** | Re-seeds (which discard destination history) must be deliberate, not automatic. |
| **Snapshot families replicated** | one (the `_daily` family) | Replicating multiple Sanoid families risks same-second name collisions that wedge replication; `zfs-hold-tight` derives weekly/monthly locally from the dailies. |

With those set, the only destination-side deleter is `zfs-hold-tight`, and an attacker who
compromises the source and runs `zfs destroy -r` cannot remove history already on the
backup.

## 1. Build a binary that will run on TrueNAS

Native AOT produces a normal dynamically-linked ELF that links against the **build host's
glibc**. glibc is forward-compatible but not backward: a binary built against glibc *X*
runs on glibc ≥ *X*, and fails to start on anything older (`... version 'GLIBC_2.3x' not
found`). So you must build on a glibc **no newer than the target's** — building on a
modern dev box (e.g. Ubuntu 24.04, glibc 2.39) yields a binary that won't start on
TrueNAS.

TrueNAS SCALE is Debian-based, and **23.10 "Cobia" and every later release are Debian 12
(glibc 2.36)** (22.12 "Bluefin" and earlier were Debian 11 / glibc 2.31). Microsoft's AOT
SDK image is itself Debian 12-based with clang + zlib already installed, so it is the
natural build environment — no cross-compile, no toolchain setup:

```bash
docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0-aot \
  dotnet publish zfs-hold-tight.cs -r linux-x64 -c Release -o out
```

Confirm the binary's highest required symbol is ≤ the target's glibc (2.36 on Debian 12):

```bash
objdump -T out/zfs-hold-tight | grep -oE 'GLIBC_[0-9.]+' | sort -V | tail -1
```

**Compatibility: TrueNAS SCALE 23.10 (Cobia) and later.** Earlier, Debian 11-based
releases would need a Debian 11 build (swap the image for a `bullseye`-based one).

## 2. Put the binary where it can execute — not on the OS

The TrueNAS SCALE OS is a **read-only appliance image**; you cannot place or run your own
binaries on it. Run the tool from a **dataset on a storage pool**, and that dataset must
allow execution (`exec=on`, the default for data datasets — boot-pool/system datasets do
not qualify).

```bash
# Once: a dedicated dataset for tooling (UI: Datasets → Add Dataset, leave Exec enabled)
zfs create archive/tools                       # exec=on by default
zfs get exec archive/tools                      # confirm: should be 'on'

# Deploy the binary and your real config there, root-owned and executable:
install -m 0755 -o root -g root out/zfs-hold-tight /mnt/archive/tools/zfs-hold-tight
cp zfs-hold-tight.json /mnt/archive/tools/      # your real config, NOT the example
```

> **Deploy by hand.** In a pull-only, ransomware-resistant design the backup box must not
> auto-sync executables from anywhere — that would be a push vector. Copy it deliberately.

The binary looks for `zfs-hold-tight.json` next to itself (override with
`ZFS_HOLD_TIGHT_CONFIG`). Logs go to the configured `logDir`. (The datasets it prunes are
the `archive/*` targets in your config — independent of where the binary itself lives.)

## 3. First run — staged, escalating only on green

It deletes snapshots, so walk it up. Run as **root** (it needs `zfs destroy` / `hold`):

```bash
cd /mnt/archive/tools
./zfs-hold-tight             # dry-run: what WOULD be pruned (deletes nothing)
./zfs-hold-tight --doctor    # hold status of monthly keepers
./zfs-hold-tight --fix       # create any missing monthly holds (non-destructive)
./zfs-hold-tight --doctor    # confirm
./zfs-hold-tight --prune     # prune for real
```

On the **first** real prune against a pre-existing backlog of untagged dailies, the
**cold-start floor** guarantees a reviewed `--force` for any nonzero backlog, not just a
large one: `guardMaxDeletePerRun` (default `minNewerHolds × 7` — 14 for the default
`minNewerHolds` of 2, more for a deeper-tail policy) is sized for a *routine* weekly
batch on a dataset this tool already manages, so on its own it wouldn't reliably gate a
first prune. Keepers are *computed*, not held — `minNewerHolds` (default 2) counts
candidate weekly/monthly keepers derived from the snapshot dates themselves (no `zfs
hold` needs to have ever been placed), so a cold backlog can have `doomed.Count > 0` from
its very first run, well before any hold exists. Whether that count clears the cap on its
own depends on how the backlog's dates land against ISO-week/month boundaries, not on a
fixed day count — comfortably under 14 for roughly 2 weeks of backlog (at the default
`minNewerHolds`), but it's been observed to exceed 14 in as little as ~18 days depending
on alignment, so don't rely on any specific day count as a safe cutoff. Either way, a
backlog short enough to stay under the cap would otherwise clear on the very first
`--prune` with no confirmation. The cold-start floor closes that gap unconditionally: a
dataset that has never been established requires `--force` for *any* nonzero doomed
count — so the very first real `--prune` against this backlog will `WORRY`+`SKIP` with
`cold start: would delete N` for any pre-existing backlog with something to destroy,
regardless of how it compares to the cap. That's the circuit-breaker doing its job, not
an error — review the dry-run output, then do the one-time backlog cleanup with `--prune
--force`. A dataset establishes the first time `--prune` completes for it — with no
`--force` needed if there was nothing doomed yet (nothing destructive to review), or via
`--force` the first time there was. Every unforced run after that is governed by the
routine `guardMaxDeletePerRun` instead.

Once running continuously (not a cold start), steady state is different: a weekly keeper
only becomes permanent when its ISO-week closes, so the dailies it stops covering become
doomed together — a ~6-7 snapshot batch once every 7 days, not a smooth 0–1/day trickle.
`--prune` alone is enough; the 14 cap has headroom for one routine week's batch plus one
missed run before it needs attention.

## 4. Schedule it

Add a cron job (UI: **System Settings → Advanced → Cron Jobs**), command run **as root**:

```
/mnt/archive/tools/zfs-hold-tight --prune
```

Schedule it *after* the day's snapshots have replicated. If the backup machine is only
powered on for a wake window (e.g. 04:45–07:00 local), the cron must fire inside that
window. With `healthchecksUrl` set, the tool pings healthchecks.io on success and
`/fail` (with diagnostics) on any `SKIP` or destroy failure, so a stalled feed or a missed
run is visible rather than silent.

If you also want the daily `--summary` report (see below) pushed automatically, point
cron at `scripts/nightly.sh` instead — it runs `--prune` then `--summary | push-...`
unconditionally, in that order, with no separate timing-guess cron entry needed:

```
/mnt/archive/tools/nightly.sh
```

### Optional: push the daily summary to a git repo

`--summary [--since YYYY-MM-DD]` prints a compact per-dataset markdown table (see
`README.md`'s Commands table) — handy to have pushed somewhere reviewable without SSHing
in. `scripts/push-summary-git.sh` reads that markdown on stdin and commits+pushes it to a
**dedicated private git repo**, overwriting one file each run (one stable place to look —
`git log` in that repo is the free daily history, no separate archiving needed). It needs:

1. **A dedicated private repo** (e.g. `<you>/zfs-hold-tight-logs`) holding nothing else —
   its only purpose is receiving this report, so there's nothing else to lose if the
   credential below is ever compromised.
2. **An SSH deploy key scoped to that ONE repo**, not a personal-access token: a gist- or
   repo-scoped PAT is account-wide (a `gist`-scoped token can read/write *every* gist on
   the account; there's no way to restrict it to just one) or would need explicit
   per-repository fine-grained scoping *plus* rotation before it expires. A deploy key has
   neither problem — GitHub itself restricts it to exactly the one repo it's added to, it
   doesn't expire, and it's the same SSH-key pattern this box's threat model already uses
   elsewhere (the replication pull key, the temporary admin maintenance key):
   ```bash
   ssh-keygen -t ed25519 -N "" -C "zfs-hold-tight-logs-deploy-key" -f ./deploy-key
   gh repo deploy-key add ./deploy-key.pub -R <you>/zfs-hold-tight-logs \
     --title "TrueNAS nightly summary push" --allow-write
   ```
3. **The private half staged root-owned, mode 600**, outside `truenas_admin`'s home — e.g.
   `/mnt/archive/tools/state/zfs-hold-tight-deploy-key` (same root-only dataset as the
   binary, assuming `logDir` in your config is under `.../state/` — check your own config
   before assuming this path exists; `install` doesn't create missing parent directories):
   ```bash
   sudo mkdir -p "$(dirname /mnt/archive/tools/state/zfs-hold-tight-deploy-key)"
   sudo install -m 600 -o root -g root ./deploy-key /mnt/archive/tools/state/zfs-hold-tight-deploy-key
   ```
   The script never contains a *secret* — no token, no key material, only a path to where
   the key is stored (`SUMMARY_SSH_KEY`, defaults to the path above). It does default to
   naming this deployment's own account/repo in `SUMMARY_REPO_URL`, which is not sensitive
   (a git remote's owner/repo, unlike a credential) but is still an identity string —
   override `SUMMARY_REPO_URL` for a different account.
4. **Both scripts staged alongside the binary** (see `zfs-hold-tight-deploy.md` in the
   orientation repo for the site-specific staging steps):
   ```bash
   sudo install -m 755 -o root -g root scripts/nightly.sh              /mnt/archive/tools/nightly.sh
   sudo install -m 755 -o root -g root scripts/push-summary-git.sh     /mnt/archive/tools/push-summary-git.sh
   ```
   `push-summary-git.sh` self-clones `$SUMMARY_REPO_DIR` (default
   `/mnt/archive/tools/state/summary-repo`) on its first run — no separate manual `git
   clone` step needed.

A failed push has no effect on backup integrity or retention — it only means the report
didn't update that day. It's deliberately not wired into healthchecks.io: this is a
visibility convenience, not a monitored safety signal. If it breaks, `nightly.sh` prints
why to its own stderr — but that is **not** the zfs-hold-tight log file: the log file
(`tail -40 .../logs/zfs-hold-tight-$(date -u +%F).log`) only captures the C# binary's own
output, not `nightly.sh`'s `echo ... >&2` lines. If the push stops working, debug it
directly — **don't** rerun `nightly.sh` itself just to see the message; it also
unconditionally runs `--prune` first, so that would trigger a real, unscheduled mutation
as a side effect of diagnosing a read-only visibility feature:
```bash
/mnt/archive/tools/zfs-hold-tight --summary | /mnt/archive/tools/push-summary-git.sh
```
Or check however TrueNAS surfaces the cron job's own output (its standard cron job
history/mail handling — not covered here).

## Migrating from an older build / a differently-named binary

`zfs-hold-tight` only recomputes keepers from snapshot dates; its `<ns>:*` tags
(`<ns>` = `propertyNamespace`) are audit annotations. To swap a binary in place, drop in
the new one and update the cron command to point at it. Existing `<ns>:monthly` holds
remain valid; run `--doctor` to confirm and `--fix` to backfill any missing ones.

If you are migrating from a build that used a different namespace, set
`propertyNamespace` in the config to the **old** value so the tool keeps managing the
holds/properties it already created — otherwise those old-namespace holds are treated as
foreign (left in place, their snapshots never pruned) and the tool starts a fresh set
under the new namespace.
