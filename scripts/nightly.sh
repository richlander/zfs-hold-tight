#!/usr/bin/env bash
# nightly.sh — cron entry point: prune, then push the summary report, unconditionally.
#
# Deliberately NOT `--prune && --summary | push`: a fixed-offset second cron entry (e.g.
# "run at 06:20, 5 minutes after --prune") is a timing guess that can race ahead if
# --prune runs long, and `&&` would skip the summary/push entirely on a prune failure —
# exactly when seeing the report matters most. Running both unconditionally in one script
# gets exact ordering with no guessed gap, and the summary still gets pushed either way.
#
# The push step is a read-only visibility helper: its failure has no effect on backup
# integrity, doesn't ping healthchecks, and is captured only via this script's own exit
# status (log-only, not a monitored check — see docs/TRUENAS.md).
set -uo pipefail

BIN="${ZFS_HOLD_TIGHT_BIN:-/mnt/archive/tools/zfs-hold-tight}"
PUSH="${PUSH_SUMMARY_GIT:-/mnt/archive/tools/push-summary-git.sh}"

report_if_failed() { [ "$1" -ne 0 ] && echo "nightly.sh: $2 (rc=$1)${3:-}" >&2; return 0; }

"$BIN" --prune
prune_rc=$?
# A nonzero prune_rc can be a legitimate WORRY/SKIP (cold-start floor, volume cap trip —
# not an error, see docs/SAFETY.md) as easily as a real failure. Without this line, that
# case produced the exact same silent `exit 1` as a crashed summary/push below, and a
# generic cron-failure alert (or this script's own exit status) couldn't tell them apart —
# the healthchecks.io ping already carries the authoritative diagnosis (see health-check.md),
# this is just enough of a pointer that nightly.sh's own signal isn't a dead end on its own.
report_if_failed "$prune_rc" "zfs-hold-tight --prune exited nonzero" " — check healthchecks.io / the day's log for whether this is a real problem or an expected WORRY/SKIP"

"$BIN" --summary | "$PUSH"
# PIPESTATUS, not `$?`: under pipefail, `$?` after a pipeline is the exit status of
# whichever stage failed (not necessarily the last one) — so a plain `$?` here would
# misattribute a `--summary` failure (e.g. one dataset's zfs-list erroring) to the push
# script, sending an operator to debug deploy-key/repo plumbing for the wrong reason.
# Captured into a local array in ONE statement, not read from $PIPESTATUS across two
# separate assignment lines: bash resets $PIPESTATUS after every command it runs,
# including the first assignment statement itself, so a second line reading
# ${PIPESTATUS[1]} would find it already gone.
pipestatus=("${PIPESTATUS[@]}")
summary_rc=${pipestatus[0]}
push_rc=${pipestatus[1]}
report_if_failed "$summary_rc" "zfs-hold-tight --summary failed"
report_if_failed "$push_rc" "push-summary-git failed" " — see above"

# Exit nonzero if ANY step failed, not just --prune: this is what makes "captured only
# via this script's own exit status" (see header) actually true — a plain cron failure
# alert (e.g. cron's own MAILTO on nonzero exit) then still catches a broken repo push
# even though it's not a monitored healthchecks signal.
[ "$prune_rc" -ne 0 ] || [ "$summary_rc" -ne 0 ] || [ "$push_rc" -ne 0 ] && exit 1
exit 0
