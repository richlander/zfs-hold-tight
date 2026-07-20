#!/usr/bin/env bash
# migrate-namespace.sh — one-time migration of zfs-hold-tight's managed properties/holds
# from one propertyNamespace to another (e.g. lander -> zht, to stop embedding an
# operator's name in ZFS properties -- see propertyNamespace's own doc comment: "so the
# source carries no site identity").
#
# NOT a mode of zfs-hold-tight itself: this is a one-time operation, not something the
# tool needs permanent CLI surface for. Run it once, verify, done.
#
# Detection is driven by TWO sources, not just <old_ns>:tier -- a dataset that gets
# WORRY'd (guard tripped) still runs "protection only ever increases" defensive
# hold-placement under WHATEVER propertyNamespace is currently configured, even though
# WORRY skips Promote (the tag-writing step) entirely. So a run against a stale/wrong
# config can recreate <old_ns>:monthly/<old_ns>:weekly HOLDS on already-migrated
# snapshots with no accompanying <old_ns>:tier property -- a real scenario, not a
# hypothetical (this is exactly what happened once during this tool's own deployment:
# a config swap step was missed, and the next --prune re-created 120 stray old holds
# across every dataset, only 11 of which also got <old_ns>:tier back). Detecting via
# <old_ns>:tier alone would silently miss the other 109.
#
# Safety model, mirroring the tool's own:
#   - Dry-run by default. Nothing mutates without --apply.
#   - Add-then-release: the NEW hold + tags are placed and VERIFIED before the OLD hold
#     is released and the OLD properties are inherited away. There is never a moment
#     where a keeper snapshot has zero effective hold protection.
#   - Idempotent: re-running after an interruption resumes at exactly the right step
#     instead of redoing completed work or losing track of a partial migration.
#   - Only tier/keep_until (the tool's own managed keeper metadata) and the
#     monthly/weekly holds derived from it are touched. Adhoc holds (<old_ns>:adhoc)
#     need no migration at all -- the tool treats any hold under a namespace it isn't
#     currently configured for as a foreign/defensive hold and leaves it alone
#     regardless of the tag's name, which is exactly correct for a manual defensive
#     hold either way. <old_ns>:worry breadcrumbs are left as harmless historical
#     residue -- nothing reads them going forward under the new namespace.
#
# Usage: migrate-namespace.sh <old_ns> <new_ns> <pool_prefix> [--apply]
#   e.g.: migrate-namespace.sh lander zht archive           # dry-run (default)
#         migrate-namespace.sh lander zht archive --apply   # actually migrate
#
# Env: ZFS_BIN (default: zfs) -- absolute path recommended, same hardening reasoning as
# zfs-hold-tight.cs's own zfsPath.
set -uo pipefail

OLD_NS="${1:-}"; NEW_NS="${2:-}"; POOL="${3:-}"
APPLY=0
for a in "$@"; do [ "$a" = "--apply" ] && APPLY=1; done
ZFS="${ZFS_BIN:-zfs}"

if [ -z "$OLD_NS" ] || [ -z "$NEW_NS" ] || [ -z "$POOL" ]; then
  echo "Usage: $0 <old_ns> <new_ns> <pool_prefix> [--apply]" >&2
  exit 2
fi
if [ "$OLD_NS" = "$NEW_NS" ]; then
  echo "old_ns and new_ns must differ (got '$OLD_NS' for both)" >&2
  exit 2
fi

echo "=== migrate-namespace: ${OLD_NS}:* -> ${NEW_NS}:* under ${POOL} $([ "$APPLY" = 1 ] && echo '(APPLY)' || echo '(DRY RUN)') ==="

migrated=0; already_ok=0; needs_review=0; failed=0

# ---- Build the candidate list from BOTH sources, deduplicated ----
# Source 1: snapshots with a real <old_ns>:tier value.
tier_candidates="$("$ZFS" get -Hp -o name,value -t snapshot -r "${OLD_NS}:tier" "$POOL" 2>/dev/null | awk -F'\t' '$2!="-"{print $1}')"
# Source 2: snapshots holding <old_ns>:monthly or <old_ns>:weekly, regardless of
# whether they still carry the tier property. One `zfs holds` call per snapshot in the
# pool -- fine for a one-time migration script, not something run routinely.
hold_candidates=""
while read -r snap; do
  [ -z "$snap" ] && continue
  tag="$("$ZFS" holds -H "$snap" 2>/dev/null | awk -F'\t' -v m="${OLD_NS}:monthly" -v w="${OLD_NS}:weekly" \
    '$2==m{print "monthly"; found=1} $2==w{print "weekly"; found=1} END{if(!found) print ""}')"
  [ -n "$tag" ] && hold_candidates="${hold_candidates}${snap}"$'\n'
done < <("$ZFS" list -Hp -o name -t snapshot -r "$POOL" 2>/dev/null)

all_candidates="$(printf '%s\n%s\n' "$tier_candidates" "$hold_candidates" | sed '/^$/d' | sort -u)"

while read -r snap; do
  [ -z "$snap" ] && continue

  tier_prop="$("$ZFS" get -Hp -o value "${OLD_NS}:tier" "$snap" 2>/dev/null)"
  old_monthly_held="$("$ZFS" holds -H "$snap" 2>/dev/null | awk -F'\t' -v t="${OLD_NS}:monthly" '$2==t{print "yes"}')"
  old_weekly_held="$("$ZFS" holds -H "$snap" 2>/dev/null | awk -F'\t' -v t="${OLD_NS}:weekly" '$2==t{print "yes"}')"

  # Resolve tier: prefer the property if present and valid; otherwise derive it
  # directly from WHICH old hold tag is present (the tag name IS the tier).
  tier=""
  if [ "$tier_prop" = "monthly" ] || [ "$tier_prop" = "weekly" ]; then
    tier="$tier_prop"
  elif [ "$old_monthly_held" = "yes" ]; then
    tier="monthly"
  elif [ "$old_weekly_held" = "yes" ]; then
    tier="weekly"
  fi
  if [ -z "$tier" ]; then
    echo "  REVIEW $snap: unexpected ${OLD_NS}:tier value '$tier_prop' and no recognizable old hold -- not touching"
    needs_review=$((needs_review + 1))
    continue
  fi

  new_tier_now="$("$ZFS" get -Hp -o value "${NEW_NS}:tier" "$snap" 2>/dev/null)"
  new_hold_now="$("$ZFS" holds -H "$snap" 2>/dev/null | awk -F'\t' -v t="${NEW_NS}:${tier}" '$2==t{print "yes"}')"
  old_hold_now="$("$ZFS" holds -H "$snap" 2>/dev/null | awk -F'\t' -v t="${OLD_NS}:${tier}" '$2==t{print "yes"}')"

  if [ "$tier_prop" != "monthly" ] && [ "$tier_prop" != "weekly" ]; then
    # ---- Stray-hold case: no old tier property, just a leftover old hold. The tag
    # name already told us the tier, so there's no keep_until to recover -- this case
    # can ONLY be resolved by releasing the old hold, never by placing a fresh new one
    # (we have no keep_until to set), so it requires the new hold to already exist. ----
    if [ "$new_tier_now" = "$tier" ] && [ "$new_hold_now" = "yes" ]; then
      echo "  ${snap}: stray ${OLD_NS}:${tier} hold with no ${OLD_NS}:tier property (already protected by ${NEW_NS}:${tier}) -> release stray hold"
      if [ "$APPLY" = 1 ]; then
        if "$ZFS" release "${OLD_NS}:${tier}" "$snap" 2>&1; then
          echo "    done"
          migrated=$((migrated + 1))
        else
          echo "    FAILED to release stray ${OLD_NS}:${tier} on $snap"
          failed=$((failed + 1))
        fi
      fi
    else
      echo "  REVIEW $snap: holds ${OLD_NS}:${tier} but has no ${OLD_NS}:tier property AND no matching ${NEW_NS}:${tier} hold yet -- cannot safely derive keep_until to migrate; review manually"
      needs_review=$((needs_review + 1))
    fi
    continue
  fi

  # ---- Full case: a real <old_ns>:tier property exists. ----
  keep_until="$("$ZFS" get -Hp -o value "${OLD_NS}:keep_until" "$snap" 2>/dev/null)"
  if [ -z "$keep_until" ] || [ "$keep_until" = "-" ]; then
    echo "  REVIEW $snap: tier=$tier but no ${OLD_NS}:keep_until value -- not touching"
    needs_review=$((needs_review + 1))
    continue
  fi

  # Idempotency, two distinct resume points -- a fully-done snapshot never becomes a
  # candidate at all (both its old tier property AND old hold are gone), so what we
  # might see here is only ever a PARTIAL prior attempt:
  new_side_done=0
  if [ "$new_tier_now" = "$tier" ] && [ "$new_hold_now" = "yes" ]; then
    new_side_done=1
    if [ "$old_hold_now" != "yes" ]; then
      echo "  OK     $snap: already migrated (${NEW_NS}:tier=$tier, ${NEW_NS}:${tier} held, ${OLD_NS}:${tier} hold gone)"
      already_ok=$((already_ok + 1))
      continue
    fi
    echo "  ${snap}: new ${NEW_NS}:tier/hold already in place from a prior interrupted run -- resuming at release+inherit"
  else
    echo "  ${snap}: ${OLD_NS}:tier=${tier} keep_until=${keep_until} -> ${NEW_NS}:tier=${tier} keep_until=${keep_until}, hold ${NEW_NS}:${tier}"
  fi
  if [ "$APPLY" = 0 ]; then
    continue
  fi

  if [ "$new_side_done" = 0 ]; then
    # 1. NEW tags first.
    if ! "$ZFS" set "${NEW_NS}:tier=${tier}" "${NEW_NS}:keep_until=${keep_until}" "$snap" 2>&1; then
      echo "    FAILED to set ${NEW_NS}:tier/keep_until on $snap -- old hold left in place"
      failed=$((failed + 1))
      continue
    fi
    # 2. NEW hold.
    if ! "$ZFS" hold "${NEW_NS}:${tier}" "$snap" 2>&1; then
      echo "    FAILED to hold ${NEW_NS}:${tier} on $snap -- old hold left in place"
      failed=$((failed + 1))
      continue
    fi
    # 3. VERIFY the new hold actually landed before touching anything old.
    verified="$("$ZFS" holds -H "$snap" 2>/dev/null | awk -F'\t' -v t="${NEW_NS}:${tier}" '$2==t{print "yes"}')"
    if [ "$verified" != "yes" ]; then
      echo "    FAILED to verify ${NEW_NS}:${tier} hold on $snap after placing it -- old hold left in place"
      failed=$((failed + 1))
      continue
    fi
  fi
  # 4. Only now release the OLD hold and inherit away the OLD properties. If release
  #    fails, deliberately do NOT inherit tier/keep_until away: that property is one of
  #    the two things this script uses to find this snapshot again, so erasing it here
  #    would orphan the un-released old hold with no way for a re-run to ever find and
  #    retry it. Leaving both old tier and old hold in place means the next run's
  #    idempotency check (new_side_done=1, old_hold_now=yes) resumes exactly here.
  if ! "$ZFS" release "${OLD_NS}:${tier}" "$snap" 2>&1; then
    echo "    WARNING: new hold+tags in place, but failed to release ${OLD_NS}:${tier} on $snap -- both holds now present, re-run this script to retry"
    needs_review=$((needs_review + 1))
    continue
  fi
  # These two are best-effort: the hold (the only thing that can make a snapshot
  # un-prunable) is already released at this point, so a failure here just leaves a
  # harmless stale property behind -- not worth a third resume-state, but worth
  # printing so it doesn't go completely unnoticed.
  "$ZFS" inherit "${OLD_NS}:tier" "$snap" 2>&1 || echo "    NOTE: ${OLD_NS}:tier didn't clear on $snap (harmless -- hold already released) — inherit manually if you want it tidy"
  "$ZFS" inherit "${OLD_NS}:keep_until" "$snap" 2>&1 || echo "    NOTE: ${OLD_NS}:keep_until didn't clear on $snap (harmless -- hold already released)"
  echo "    done"
  migrated=$((migrated + 1))
done <<< "$all_candidates"

echo "=== migrate-namespace: migrated=$migrated already_ok=$already_ok needs_review=$needs_review failed=$failed $([ "$APPLY" = 0 ] && echo '(DRY RUN -- nothing was actually changed)') ==="
[ "$failed" = 0 ] && [ "$needs_review" = 0 ]
