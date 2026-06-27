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

On the **first** real prune the backlog of old dailies will exceed `guardMaxDeletePerRun`
(default 5) and the tool will `WORRY` + `SKIP` with `would delete N > cap` — that is the
circuit-breaker, not an error. Review the dry-run output, then do the one-time backlog
cleanup with `--prune --force`. After that, steady state is 0–1 deletions/day and `--prune`
alone is enough.

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
