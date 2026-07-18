#!/usr/bin/env bash
# push-summary-git.sh — read `zfs-hold-tight --summary` markdown on stdin, commit it to a
# dedicated private git repo and push, via an SSH deploy key scoped to that ONE repo.
#
# Why git+deploy-key over a GitHub gist+PAT: a gist-scoped PAT is account-wide — there is
# no way to restrict it to just the one gist this tool manages, so a leaked token could
# read/write every gist on the account. A deploy key is scoped by GitHub itself to exactly
# one repository, needs no rotation (unlike a PAT, which expires), and needs no token file
# at all — just a standard SSH private key, the same pattern this box already uses
# elsewhere (the replication pull key, the temporary admin maintenance key). This script
# never contains a SECRET — no token, no key material, only a path to the key file. The
# default repo URL below does name this deployment's own account/repo, but that's not
# sensitive: a git remote's owner/repo is public information to anyone with read access to
# the repo anyway, unlike a credential. Override SUMMARY_REPO_URL for a different account.
#
# The repo is self-cloning: if $SUMMARY_REPO_DIR isn't already a git checkout, this clones
# it first. One file in the repo is overwritten each run (latest status at a glance via
# the file itself); git's own commit history is the free daily audit trail (`git log`).
#
# This is a read-only-to-backups visibility helper. A failed push has NO effect on backup
# integrity or retention — it only means the report didn't update. Failures are printed to
# stderr and exit nonzero; nothing here pings healthchecks or blocks the caller.
#
# Usage: zfs-hold-tight --summary | push-summary-git.sh
# Env:   SUMMARY_REPO_URL   (default: ssh://git@ssh.github.com:443/richlander/zfs-hold-tight-logs.git)
#        SUMMARY_REPO_DIR   (default: /mnt/archive/tools/state/summary-repo)
#        SUMMARY_SSH_KEY    (default: /mnt/archive/tools/state/zfs-hold-tight-deploy-key)
#        SUMMARY_FILE       (default: SUMMARY.md)
set -uo pipefail

# The default URL uses GitHub's SSH-over-443 endpoint (ssh.github.com:443), not the
# standard git@github.com:... shorthand (which is always port 22): outbound port 22 is
# blocked/dropped on this network (confirmed via `nc -zv github.com 22` timing out, vs
# `nc -zv ssh.github.com 443` succeeding instantly) — a silently-dropped SYN makes ssh hang
# indefinitely waiting for a TCP handshake that never completes, rather than failing fast.
# GitHub documents this exact endpoint for networks that block/restrict outbound 22:
# https://docs.github.com/en/authentication/troubleshooting-ssh/using-ssh-over-the-https-port
REPO_URL="${SUMMARY_REPO_URL:-ssh://git@ssh.github.com:443/richlander/zfs-hold-tight-logs.git}"
REPO_DIR="${SUMMARY_REPO_DIR:-/mnt/archive/tools/state/summary-repo}"
SSH_KEY="${SUMMARY_SSH_KEY:-/mnt/archive/tools/state/zfs-hold-tight-deploy-key}"
FILE="${SUMMARY_FILE:-SUMMARY.md}"

# Read stdin FIRST, before any other check that might exit early: nightly.sh runs
# `"$BIN" --summary | "$PUSH"` as a pipeline — if this script exited before ever reading
# stdin (e.g. on the SSH-key check below), the upstream `--summary` process would get
# SIGPIPE trying to write into a reader that's already gone, muddying which side actually
# failed. Draining stdin unconditionally first means `--summary` always gets to finish
# writing and exit cleanly regardless of what this script decides to do next.
content="$(cat)"

if [ ! -r "$SSH_KEY" ]; then
    echo "push-summary-git: cannot read deploy key $SSH_KEY" >&2
    exit 1
fi

[ -n "$content" ] || { echo "push-summary-git: no input on stdin, nothing to push" >&2; exit 1; }

# IdentitiesOnly: only ever offer THIS key, never fall back to an ssh-agent key or another
# identity file — this key is scoped to exactly one repo, so that's the only thing it
# should ever be used to authenticate. accept-new: trust github.com's host key on first
# contact and pin it thereafter (no interactive prompt in a cron context, still protects
# against a MITM on any LATER connection). $SSH_KEY is single-quoted WITHIN the string:
# GIT_SSH_COMMAND is itself re-parsed by a shell downstream (inside git), so an unquoted
# path containing a space would get word-split into bogus extra -i flags.
export GIT_SSH_COMMAND="ssh -i '$SSH_KEY' -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new"

# Self-clone on first run (or if the checkout was ever removed) rather than requiring a
# separate manual clone step during setup. $REPO_DIR merely EXISTING isn't proof it's
# THIS tool's own checkout — a prior run killed mid-clone (cron timeout, OOM, lost
# network) can leave anything behind: no .git at all yet, a partial .git, or (mid-commit)
# a stale .git/index.lock; separately, $REPO_DIR could be misconfigured (fat-fingered
# SUMMARY_REPO_DIR) to point at some OTHER, unrelated directory or valid git repo
# entirely. These get different treatment: OUR OWN corrupt state (nothing here but a
# possibly-partial clone) is safe to delete and re-clone; anything that ISN'T provably
# just that — a valid-but-mismatched repo, OR a non-git directory holding unexpected
# content — is NOT ours to delete. Refuse instead, so a misconfigured path can never
# silently destroy someone's unrelated data or push into someone else's repo. `-e`, not
# `-d`, on the outer guard: a stray non-directory file left at $REPO_DIR must also be
# caught, not just a bad directory (git clone refuses to write into ANY existing,
# non-empty path either way).
if [ -e "$REPO_DIR" ]; then
    # Checked against the actual OUTPUT ("true"), not just the exit code: for a BARE repo
    # (e.g. an operator's stray `git clone --bare` at this path while debugging), `git
    # rev-parse --is-inside-work-tree` exits 0 but prints "false" — an exit-code-only check
    # would treat that as a valid checkout and skip repair, only to fail later with a vague
    # "git add failed" instead of a clear diagnostic. A bare repo's own contents (HEAD,
    # objects/, refs/, ...) don't match the "just this tool's state" allowlist below
    # either, so this correctly REFUSES rather than auto-repairing it — same reasoning as
    # any other unexpected content, just reached via this branch instead.
    if [ "$(git -C "$REPO_DIR" rev-parse --is-inside-work-tree 2>/dev/null)" = "true" ]; then
        if [ "$(git -C "$REPO_DIR" remote get-url origin 2>/dev/null)" != "$REPO_URL" ]; then
            echo "push-summary-git: $REPO_DIR is a valid checkout, but its 'origin' doesn't match \$SUMMARY_REPO_URL — refusing to push into a possibly-unrelated repo. Check SUMMARY_REPO_DIR/SUMMARY_REPO_URL, or remove $REPO_DIR yourself to let this script re-clone." >&2
            exit 1
        fi
    else
        # Not a valid checkout — but only auto-delete if it plausibly holds nothing but
        # THIS tool's own (possibly partial) state. Anything else here (an operator's
        # unrelated files at a fat-fingered SUMMARY_REPO_DIR path) must never be silently
        # destroyed, however inconvenient that makes recovering from OUR OWN corruption.
        unexpected="$(find "$REPO_DIR" -mindepth 1 -maxdepth 1 ! -name .git ! -name "$FILE" 2>/dev/null)"
        if [ -n "$unexpected" ]; then
            echo "push-summary-git: $REPO_DIR exists, isn't a valid checkout, AND contains content this script didn't put there — refusing to delete it automatically. Check SUMMARY_REPO_DIR, or remove it yourself to let this script re-clone." >&2
            exit 1
        fi
        echo "push-summary-git: $REPO_DIR exists but isn't a valid checkout — re-cloning" >&2
        rm -rf "$REPO_DIR"
    fi
fi
if [ ! -d "$REPO_DIR" ]; then
    if ! git clone "$REPO_URL" "$REPO_DIR" 2>&1; then
        echo "push-summary-git: clone of the summary repo failed" >&2
        exit 1
    fi
fi

# A stale lock from a run killed mid-commit (same failure class as the repair above, but
# rev-parse alone doesn't catch it — the checkout itself is still perfectly valid, just
# holding a lock nothing will ever release). Gated on BOTH age AND no live git process
# against this checkout, not just age alone: docs/TRUENAS.md tells operators to run this
# script manually while debugging a failed push, which COULD overlap a still-running cron
# invocation genuinely holding this lock, and wall-clock age alone is defeatable by a
# forward clock jump (e.g. an NTP correction shortly after boot, before time is fully
# synced) — a live-process check doesn't depend on the clock at all. Age still guards the
# common case cheaply (a real commit of one small file finishes in well under a second, so
# anything past 60s is overwhelmingly likely orphaned) before bothering to check pgrep.
lock_file="$REPO_DIR/.git/index.lock"
if [ -f "$lock_file" ]; then
    lock_age=$(( $(date +%s) - $(stat -c %Y "$lock_file" 2>/dev/null || echo 0) ))
    if [ "$lock_age" -gt 60 ] && ! pgrep -f "git.*$REPO_DIR" >/dev/null 2>&1; then
        echo "push-summary-git: removing stale index.lock (${lock_age}s old, no live git process found)" >&2
        rm -f "$lock_file"
    fi
fi

# NOTE: an earlier version of this script tried to `git fetch` + `git reset --hard
# origin/<branch>` HERE, before writing new content, on the theory that this would keep
# the "did anything change" comparison honest against the true remote state. Reverted
# after review found it silently discards a PRIOR run's locally-committed-but-not-yet-
# pushed commit whenever that run's own push had failed (e.g. a network blip) — the reset
# rewinds local HEAD to match origin's still-stale tip, permanently losing that commit
# and the audit-trail entry it represented, with no error. That scenario is exactly what
# the "always attempt push even when there's nothing new to commit" fall-through below
# exists to recover from; an upfront destructive reset defeated it. Remote divergence
# (e.g. a commit added via GitHub's own web UI) is instead reconciled safely at PUSH time
# only, via `fetch` + `push --force-with-lease` right before the push itself (see below) —
# this repo holds nothing but one regenerated-every-run file, so overwriting a divergent
# remote tip there is safe; discarding OUR OWN not-yet-pushed commit here would not have
# been.

# Checked, not a bare redirect: an unwritable/full state filesystem would otherwise leave
# $FILE holding STALE content from a previous run, and everything downstream (git add,
# git diff --cached --quiet) would then see "nothing changed" and report a healthy no-op —
# silently hiding the real failure to write.
if ! printf '%s' "$content" > "$REPO_DIR/$FILE"; then
    echo "push-summary-git: failed to write $REPO_DIR/$FILE" >&2
    exit 1
fi

if ! git -C "$REPO_DIR" add "$FILE"; then
    echo "push-summary-git: git add failed" >&2
    exit 1
fi

# Nothing changed since last run (identical table) — skip an empty commit rather than
# erroring; `git commit` exits nonzero on "nothing to commit" and that's not a failure.
# Still fall through to `git push` below either way, NOT `exit 0` here: a PRIOR run could
# have committed successfully but failed to push (network blip) — that commit is sitting
# in local HEAD with nothing new to stage, so skipping the push on a "no new content" day
# would leave it stuck unpushed indefinitely with no signal (this is log-only, not wired
# to healthchecks). `git push` is a safe, fast no-op when already up to date.
if git -C "$REPO_DIR" diff --cached --quiet; then
    echo "push-summary-git: no content change since last run, nothing new to commit"
else
    if ! git -C "$REPO_DIR" -c user.name="zfs-hold-tight" -c user.email="zfs-hold-tight@localhost" \
            commit -q -m "summary $(date -u +%F)"; then
        echo "push-summary-git: git commit failed" >&2
        exit 1
    fi
fi

# Fetch immediately before push, then push --force-with-lease rather than a plain push:
# this repo holds nothing but this one regenerated-every-run file, entirely owned by this
# tool, so there is nothing to merge or preserve if the remote ever diverges (e.g. a
# README added via GitHub's own "create repo" web UI, or any other manual commit) — OUR
# content should just win. A plain `git push` would instead reject that as non-fast-
# forward FOREVER, every single night, since local HEAD can never fast-forward past a
# commit it doesn't have. `--force-with-lease` (not a bare `--force`) still refuses if the
# remote changed in a way this run doesn't know about — the `fetch` immediately before it
# is what lets the lease see the ACTUAL current remote state (as close to push-time as
# possible) so a genuine divergence still overwrites cleanly instead of refusing. Fetch
# failure here is only logged, not separately fatal: if it's a real, persistent network
# problem, the push right after fails for the same reason, and THAT failure is checked
# and reported below.
git -C "$REPO_DIR" fetch -q origin 2>&1 | sed 's/^/push-summary-git: fetch: /' >&2

# No `if var="$(cmd)"; then ...` dance needed here (unlike the old gist script's curl
# call): this script has no `set -e`, only `set -uo pipefail`, and this isn't a command
# substitution assignment either — a plain nonzero exit here can't abort anything.
# Capture $? on its OWN line, not inside `if ! git push; then ... $? ...`: `!` inverts the
# exit status the `if` sees, but `$?` inside that then-branch reflects the INVERTED
# result (always 0), not git push's real code — verified: `if ! false; then echo $?; fi`
# prints 0. Running the command plainly first, THEN branching on the real $?, avoids that.
git -C "$REPO_DIR" push --force-with-lease -q
push_rc=$?
if [ "$push_rc" -ne 0 ]; then
    echo "push-summary-git: git push failed (rc=$push_rc)" >&2
    exit 1
fi

echo "pushed to the summary repo"
