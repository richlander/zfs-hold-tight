#!/usr/bin/env bash
# Integration tests for zfs-hold-tight using a fake `zfs` (no real pool needed).
# Usage: ./run-tests.sh [path-to-zfs-hold-tight.cs]
#
# All fixtures are anchored relative to `now` so the suite is deterministic at any
# wall-clock hour: the freshest snapshot is NOW-2h, and every snapshot shares one
# time-of-day (subtracting whole days preserves it), so names are reconstructable.
set -u
HERE="$(cd "$(dirname "$0")" && pwd)"
TARGET="${1:-$HERE/../zfs-hold-tight.cs}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin"; ln -sf "$HERE/fake-zfs" "$WORK/bin/zfs"
export PATH="$WORK/bin:$PATH"
PASS=0; FAIL=0
ok()  { echo "  PASS: $1"; PASS=$((PASS+1)); }
bad() { echo "  FAIL: $1"; FAIL=$((FAIL+1)); }

NOW=$(date -u +%s)
ANCHOR=$((NOW-7200))                              # newest snapshot is 2h old
TIME=$(date -u -d "@$ANCHOR" +%H:%M:%S)
ADATE=$(date -u -d "@$ANCHOR" +%Y-%m-%d)
snapname() { echo "archive/home/rich@autosnap_$1_${TIME}_daily"; }   # $1=YYYY-MM-DD

# A fixture date that is provably a PLAIN daily — neither an ISO-week keeper (a Sunday, the
# newest day of its week) nor a month keeper (a month-end, the newest day of its month) — while
# still inside the 30d daily window with a later same-week & same-month daily present. The
# demotion tests (T13/T26) need this: a fixed "-5 days" offset silently lands on a Sunday or a
# month boundary some weeks, and the tool then CORRECTLY tags the snapshot weekly/monthly rather
# than clearing it, so the test — not the tool — breaks. Computing it keeps the suite
# deterministic across the whole calendar, not just across the hour of day.
nonkeeper_date() {
  local off cand dow m nm
  for off in $(seq 8 20); do
    cand=$(date -u -d "$ADATE -$off days" +%Y-%m-%d)
    dow=$(date -u -d "$cand" +%u)                 # 1=Mon .. 7=Sun
    m=$(date -u -d "$cand" +%m); nm=$(date -u -d "$cand +1 day" +%m)
    [ "$dow" = 7 ] && continue                    # Sunday    -> ISO-week keeper
    [ "$m" != "$nm" ] && continue                 # month-end -> month keeper
    echo "$cand"; return 0
  done
  date -u -d "$ADATE -10 days" +%Y-%m-%d          # fallback (unreachable in practice)
}
DEMOTED_DATE="$(nonkeeper_date)"

# A newest date for the small INVARIANT fixtures (T34/T36/T37): the most recent Wednesday with
# day-of-month >= 5. Wednesday guarantees Mon/Tue/Wed share one ISO week (so within-week days are
# non-keepers), and dom>=5 keeps a 5-day fixture inside one calendar month. Computed (not a fixed
# offset) so the keeper structure is deterministic across the whole calendar.
pick_N() {
  local off cand dow dom
  for off in $(seq 0 40); do
    cand=$(date -u -d "$ADATE -$off days" +%Y-%m-%d)
    dow=$(date -u -d "$cand" +%u); dom=$(date -u -d "$cand" +%d)
    [ "$dow" = 3 ] && [ "$((10#$dom))" -ge 5 ] && { echo "$cand"; return; }
  done
  date -u -d "$ADATE -7 days" +%Y-%m-%d
}
INV_N="$(pick_N)"
inv_day() { date -u -d "$INV_N -$1 days" +%Y-%m-%d; }   # $1 = days before INV_N

# inv_config <minNewerHolds|-> : write an invariant-test config.
# Relaxes freshness + window guards so a mid-week INV_N can be "newest", and lifts the
# delete cap so the tiny doomed set is not circuit-broken. "-" omits the key (compiled
# default). dailyKeepDays is pinned to 1 (not the real default of 30): these fixtures are
# only a few days old, so a realistic dailyKeepDays would make the calendar floor (see
# zfs-hold-tight.cs's doomed-daily loop) protect everything regardless of hold coverage,
# masking the two-holds mechanic these tests exist to isolate. The floor itself has its
# own dedicated tests (T55+) against the long-lived $WORK/healthy fixture instead.
inv_config() {
  local mnh="$1" line=""
  [ "$mnh" != "-" ] && line="$line \"minNewerHolds\": $mnh,"
  cat > "$WORK/cfg_inv.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", $line
  "guardMaxDeletePerRun": 50, "guardRecentWindowDays": 3, "guardMaxDailyAgeHours": 720,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 1, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
}
# build the 5-day invariant fixture INV_N-4 .. INV_N (all at TIME) into $1
inv_feed() { local f="$1" i; : > "$f"; for i in 4 3 2 1 0; do printf '%s\t-\t-\n' "$(snapname "$(inv_day "$i")")"; done > "$f"; }
# run with the invariant config
run_inv() {
  local snaps="$1"; shift
  rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
  cp "$snaps" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
  OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_inv.json" \
         ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc "$@" 2>/dev/null)"
  RC=$?; OPS="$WORK/fz/ops.log"
}

# minNewerHolds is pinned to 2 here (not left to the compiled default) so every
# frontier-dependent date computation below (T4/T4b's gap-boundary math, T45-51's
# Continuity scope, etc. — all independently derived and calendar-verified for N=2) stays
# valid regardless of what the compiled default is. T-default below covers the default
# itself; T36/T36b cover tuning it.
cat > "$WORK/config.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "guardMaxDeletePerRun": 5, "guardRecentWindowDays": 7,
  "minNewerHolds": 2,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF

# gen_feed <start-date>: dailies from start-date..ANCHOR (all at TIME), newest = NOW-2h
gen_feed() {
  local cur; cur=$(date -u -d "$1" +%Y-%m-%d)
  while [[ "$cur" < "$ADATE" || "$cur" == "$ADATE" ]]; do
    printf '%s\t-\t-\n' "$(snapname "$cur")"
    cur=$(date -u -d "$cur +1 day" +%Y-%m-%d)
  done
}
# gen_window <anchor-epoch>: 10-day complete window ending at an exact-timestamp newest
gen_window() {
  local a="$1" i
  for i in $(seq 9 -1 1); do printf '%s\t-\t-\n' "$(snapname "$(date -u -d "@$((a-i*86400))" +%Y-%m-%d)")"; done
  printf 'archive/home/rich@autosnap_%s_daily\t-\t-\n' "$(date -u -d "@$a" +%Y-%m-%d_%H:%M:%S)"
}
# run_tool <snaps-file> [args...]: sets OUT, RC, OPS
run_tool() {
  local snaps="$1"; shift
  rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
  cp "$snaps" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
  OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
         ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc "$@" 2>/dev/null)"
  RC=$?; OPS="$WORK/fz/ops.log"
}
# establish_oldest <src-file> <dst-file>: copy src to dst, pre-tagging its OLDEST entry as
# an already-held monthly keeper — simulates "this dataset has prior history" for tests
# exercising behavior that's gated on being established (routine volume circuit-breaker,
# the cold-start floor's non-blocked path), without needing a real prior --force run.
establish_oldest() {
  local src="$1" dst="$2" oldest
  oldest="$(sort "$src" | head -1 | cut -f1)"
  awk -F'\t' -v OFS='\t' -v s="$oldest" '$1==s{$2=9999999999;$3="monthly"} {print}' "$src" > "$dst"
}

echo "== Target: $TARGET =="
gen_feed 2025-06-01 > "$WORK/healthy"
# An "established" variant (one entry pre-tagged) for tests exercising the ROUTINE volume
# circuit-breaker specifically — the cold-start floor (unestablished + nonzero doomed)
# would otherwise preempt it, since $WORK/healthy itself is all tier=-.
establish_oldest "$WORK/healthy" "$WORK/healthy_established"

# ---- T1: healthy feed, dry-run, big backlog -> circuit-breaker HOLD, 0 destroys ----
run_tool "$WORK/healthy_established"
{ echo "$OUT" | grep -qE "> cap.*keepers held, daily tail to" && [ "$(grep -c '^destroy' "$OPS")" = 0 ]; } \
  && ok "T1 healthy dry-run SKIP on circuit-breaker (worry line shows keepers held + tail), no destroys" || bad "T1 (out: $(echo "$OUT"|tail -1))"

# ---- T2: prune --force -> keeper invariant: no in-retention week/month keeper destroyed
#          (the fixture spans ~1y, all keepers within the 5y window; T19 covers expiry) ----
run_tool "$WORK/healthy" --prune --force
grep '^destroy' "$OPS" | awk '{print $2}' | sort > "$WORK/destroyed"
awk -F'\t' '{print $1}' "$WORK/healthy" | sed -E 's/.*autosnap_([0-9-]+)_.*/\1/' > "$WORK/dates"
awk '{m=substr($1,1,7); last[m]=$1} END{for(k in last)print last[k]}' "$WORK/dates" > "$WORK/mk"
while read -r d; do echo "$(date -u -d "$d" +%G-%V) $d"; done < "$WORK/dates" \
  | awk '{last[$1]=$2} END{for(k in last)print last[k]}' > "$WORK/wk"
viol=0
while read -r kd; do grep -qx "$(snapname "$kd")" "$WORK/destroyed" && { echo "    month-keeper destroyed: $kd"; viol=1; }; done < "$WORK/mk"
while read -r kd; do grep -qx "$(snapname "$kd")" "$WORK/destroyed" && { echo "    week-keeper destroyed: $kd"; viol=1; }; done < "$WORK/wk"
{ [ "$viol" = 0 ] && [ "$(wc -l < "$WORK/destroyed")" -gt 50 ]; } \
  && ok "T2 prune destroyed $(wc -l < "$WORK/destroyed"|tr -d ' '), no in-retention keeper destroyed" || bad "T2 viol=$viol destroyed=$(wc -l<"$WORK/destroyed")"
grep -qx "$(snapname "$ADATE")" "$WORK/destroyed" && bad "T2b newest/base destroyed!" || ok "T2b newest daily (base) preserved"
# tag-only-keepers: plain dailies must NOT be tagged; only ~70 keepers get a zht:tier= set
sets=$(grep -c 'zht:tier=' "$OPS"); sets=${sets:-0}
{ [ "$sets" -gt 0 ] && [ "$sets" -lt 120 ]; } && ok "T2c tag-only-keepers ($sets tier-sets, not ~388)" || bad "T2c tier-sets=$sets"
# finding 2: doomed plain dailies (no monthly hold) must NOT each get a redundant release
rels=$(grep -c '^release' "$OPS"); rels=${rels:-0}
[ "$rels" = 0 ] && ok "T2d no redundant release before destroying unheld dailies" || bad "T2d $rels redundant releases"

# ---- T56: dailyKeepDays calendar floor — even with full two-holds coverage (T2 already
#      proved 321 dailies clear that bar), nothing destroyed is younger than the configured
#      30-day floor. Reuses T2's own $WORK/destroyed rather than re-running the tool. ----
floor_boundary56=$(date -u -d "$ADATE -30 days" +%Y-%m-%d)
violating56=$(sed -E 's/.*autosnap_([0-9-]+)_.*/\1/' "$WORK/destroyed" | awk -v b="$floor_boundary56" '$1>b')
[ -z "$violating56" ] \
  && ok "T56 dailyKeepDays floor: no destroyed daily is younger than the configured 30-day floor" \
  || bad "T56 floor violated: $(echo "$violating56" | tr '\n' ';')"

# ---- T57: dailyKeepDays is genuinely load-bearing, not coincidental — lowering it from 30
#      to 5 must destroy dailies in the 6-30-day-back band that T56 just proved survive at
#      30, while still respecting its own (lower) 5-day cutoff. ----
cat > "$WORK/cfg_floor5.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "guardMaxDeletePerRun": 500, "guardRecentWindowDays": 7,
  "minNewerHolds": 2,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 5, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_floor5.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
floor5_boundary=$(date -u -d "$ADATE -5 days" +%Y-%m-%d)
destroyed57=$(grep '^destroy' "$WORK/fz/ops.log" | awk '{print $2}' | sed -E 's/.*autosnap_([0-9-]+)_.*/\1/')
newly_destroyed57=$(echo "$destroyed57" | awk -v lo="$floor_boundary56" -v hi="$floor5_boundary" '$1>lo && $1<=hi')
still_protected57=$(echo "$destroyed57" | awk -v hi="$floor5_boundary" '$1>hi')
{ [ -n "$newly_destroyed57" ] && [ -z "$still_protected57" ]; } \
  && ok "T57 dailyKeepDays is tunable: floor=5 destroys the 6-30-day band floor=30 protects, its own 5-day window still respected" \
  || bad "T57 (newly=$(echo "$newly_destroyed57"|tr '\n' ';'); still_protected_violations=$(echo "$still_protected57"|tr '\n' ';'))"

# ---- T3a: newest 30h old (>26h) -> HOLD on freshness ----
gen_window $((NOW-108000)) > "$WORK/stale"
run_tool "$WORK/stale" --force
{ echo "$OUT" | grep -qi "stale"; } && ok "T3a stale newest (30h) -> WORRY+SKIP" || bad "T3a (out: $(echo "$OUT"|tail -1))"

# ---- T3b: newest 10h old (<26h), complete window -> NOT held for freshness ----
gen_window $((NOW-36000)) > "$WORK/fresh"
run_tool "$WORK/fresh" --force
{ ! echo "$OUT" | grep -qi "SKIP"; } && ok "T3b fresh newest (10h) proceeds, no SKIP" || bad "T3b held a fresh feed (out: $(echo "$OUT"|tail -1))"

# ---- T4: gap in last 7d -> HOLD ----
# Must remove a day that's NEVER covered by the (now frontier-aware) guard 1 — i.e.
# always >= the hold frontier, regardless of which weekday the suite runs on. The
# frontier is at oldest "yesterday" (when today is a Monday, yesterday=last Sunday=the
# just-closed week's keeper) and newer on every other day of the week, so "yesterday" is
# the latest day that's provably never legitimately-pruned-and-therefore-excused.
gap=$(date -u -d "$ADATE -1 days" +%Y-%m-%d)
grep -v "$gap" "$WORK/healthy" > "$WORK/gap"
run_tool "$WORK/gap" --force
{ echo "$OUT" | grep -qi "gap"; } && ok "T4 gap in window -> WORRY+SKIP" || bad "T4 (out: $(echo "$OUT"|tail -1))"

# ---- T4b: the pruned_through boundary is exact, not off-by-one in either direction.
#      Guard 1 no longer derives its exclusion from the (policy-dependent) hold frontier
#      at all -- see docs/MODEL.md: re-deriving "was this gap legitimate" from the CURRENT
#      minNewerHolds/dailyKeepDays broke the instant either changed, because legitimacy of
#      a past decision was determined under whatever policy was in effect then, not today's.
#      Guard 1 now excuses a missing day iff it is at-or-before the persisted
#      <ns>:pruned_through watermark, a fact recorded by a past successful prune -- never
#      re-derived. This replaces the old frontier-boundary test with the new mechanism's
#      own boundary: a day AT the watermark is excused; the very next day is not. ----
pt_date=$(date -u -d "$ADATE -3 days" +%Y-%m-%d)      # well inside guardRecentWindowDays (7)
pt_epoch=$(date -u -d "$pt_date" +%s)
pt_next=$(date -u -d "$pt_date +1 day" +%Y-%m-%d)

rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
grep -v "$pt_date" "$WORK/healthy" > "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
printf 'archive/home/rich\tzht:pruned_through\t%s\n' "$pt_epoch" > "$WORK/fz/dsprops"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --force 2>/dev/null)"
{ ! echo "$OUT" | grep -qi "gap"; } \
  && ok "T4b pruned_through boundary: a missing day AT the watermark is excused, not flagged" \
  || bad "T4b-at (pt=$pt_date, out: $(echo "$OUT"|tail -1))"

rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
grep -v "$pt_next" "$WORK/healthy" > "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
printf 'archive/home/rich\tzht:pruned_through\t%s\n' "$pt_epoch" > "$WORK/fz/dsprops"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --force 2>/dev/null)"
{ echo "$OUT" | grep -qi "gap"; } \
  && ok "T4b pruned_through boundary: the day AFTER the watermark is still a genuine gap" \
  || bad "T4b-after (pt_next=$pt_next, out: $(echo "$OUT"|tail -1))"

# ---- T58: regression test for the exact incident this session found — raising
#      minNewerHolds after some history is already legitimately pruned must NEVER
#      retroactively turn an already-certified gap into a guard-1 WORRY. Missing the
#      last 5 days before newest (simulating dailies a PRIOR healthy run already
#      destroyed), with pruned_through already certified through newest: guard 1 must
#      stay clean no matter what minNewerHolds is configured to NOW, including the real
#      production spread (2, 3, 24) -- this is exactly the PolicyIndependence property
#      docs/model/ZfsHoldTight.tla checks in the model, verified here against the real
#      implementation instead. ----
cp "$WORK/healthy" "$WORK/t58base"
for i in 1 2 3 4 5; do
  d=$(date -u -d "$ADATE -$i days" +%Y-%m-%d)
  grep -v "$d" "$WORK/t58base" > "$WORK/t58base.tmp"; mv "$WORK/t58base.tmp" "$WORK/t58base"
done
pt58_epoch=$(date -u -d "$ADATE" +%s)
for mnh in 2 3 24; do
  cat > "$WORK/cfg_t58.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": $mnh,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
  rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
  cp "$WORK/t58base" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
  printf 'archive/home/rich\tzht:pruned_through\t%s\n' "$pt58_epoch" > "$WORK/fz/dsprops"
  OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_t58.json" \
         ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --force 2>/dev/null)"
  { ! echo "$OUT" | grep -qi "gap"; } \
    && ok "T58 minNewerHolds=$mnh: already-certified gap stays excused (no retroactive WORRY)" \
    || bad "T58 minNewerHolds=$mnh (out: $(echo "$OUT"|tail -1))"
done

# ---- T59: a healthy --prune actually advances pruned_through to today (the mechanism
#      itself, not just its downstream effect on guard 1) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
pt_written=$(awk -F'\t' -v d="archive/home/rich" -v p="zht:pruned_through" '$1==d && $2==p{print $3}' "$WORK/fz/dsprops" 2>/dev/null)
pt_written_date=$(date -u -d "@${pt_written:-0}" +%Y-%m-%d 2>/dev/null)
# Compare against a date captured fresh right here, NOT the script-start $ADATE: the
# tool stamps pruned_through with its OWN DateTime.UtcNow at whatever moment this
# specific test happens to run (potentially minutes into a long suite), so within the
# ~2-hour window after UTC midnight, a bash `date` call at script start and one made
# now can legitimately disagree by a day even though nothing is wrong (same class of
# flakiness T24b's comment already documents for log filenames). Accept either.
real_today=$(date -u +%Y-%m-%d)
{ [ "$pt_written_date" = "$ADATE" ] || [ "$pt_written_date" = "$real_today" ]; } \
  && ok "T59 healthy --prune advances pruned_through to today ($pt_written_date)" \
  || bad "T59 (pt_written date=$pt_written_date, expected=$ADATE or $real_today)"

# ---- T59b: a WORRY (guard-tripped) run must NOT advance pruned_through — protection
#      only ever increases, and a run we didn't trust must never get certified. ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/gap" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ [ ! -s "$WORK/fz/dsprops" ] || ! grep -q "zht:pruned_through" "$WORK/fz/dsprops" 2>/dev/null; } \
  && ok "T59b a WORRY run does not advance pruned_through" \
  || bad "T59b (dsprops: $(cat "$WORK/fz/dsprops" 2>/dev/null | tr '\n' ';'))"

# ==== pool-wide health latch (docs/MODEL.md "Cross-dataset shock") ====
# health_of <dsprops-file>: reads the pool-level zht:health value (or empty if unset).
health_of() { awk -F'\t' -v d="archive" -v p="zht:health" '$1==d && $2==p{print $3}' "$1" 2>/dev/null; }
# health_reason_of <dsprops-file>: reads the persisted zht:health_reason value (or empty).
health_reason_of() { awk -F'\t' -v d="archive" -v p="zht:health_reason" '$1==d && $2==p{print $3}' "$1" 2>/dev/null; }

# ---- T60: the latch does NOT auto-heal. A dataset shocks (guard 4/5), latching
#      health=unhealthy; a SECOND run against a completely clean feed for the SAME
#      dataset must still freeze -- the latch is a fact about a past detection, not a
#      live re-check that clears itself the moment things look fine again. ----
nlines60="$(gen_window $((NOW-7200)) | wc -l)"
gen_window $((NOW-7200)) | awk -F'\t' -v n="$nlines60" \
  'BEGIN{OFS="\t"} {print $1,$2,$3,(NR==n?104857600:1073741824)}' > "$WORK/t60shock"   # newest shrinks 90%
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/t60shock" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
  ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1
health60a="$(health_of "$WORK/fz/dsprops")"

: > "$WORK/fz/ops.log"; cp "$WORK/healthy" "$WORK/fz/snaps"   # now a clean, dense feed -- same dataset
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ [ "$health60a" = "unhealthy" ] && echo "$OUT" | grep -qi "archive: WORRY - already latched unhealthy" \
  && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T60 health latch does not auto-heal: a second, individually-clean run still freezes" \
  || bad "T60 (health_after_shock=$health60a, out: $(echo "$OUT" | grep -i worry))"

# ---- T61: --reset-health is the ONLY way to clear it; a subsequent --prune then
#      proceeds normally again (reusing the still-latched $WORK/fz from T60). ----
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --reset-health 2>/dev/null)"
health61="$(health_of "$WORK/fz/dsprops")"
{ echo "$OUT" | grep -qi "health" && [ "$health61" = "healthy" ]; } \
  && ok "T61a --reset-health clears the latch (unhealthy -> healthy)" \
  || bad "T61a (health=$health61, out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

: > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ ! echo "$OUT" | grep -qi "archive: WORRY"; } \
  && ok "T61b after reset, a subsequent --prune proceeds normally (no pool freeze)" \
  || bad "T61b (out: $(echo "$OUT" | grep -i worry))"

# ---- T62: --reset-health is a safe no-op when health isn't currently unhealthy ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --reset-health 2>/dev/null)"; RC=$?
{ [ "$RC" = 0 ] && echo "$OUT" | grep -qi "nothing to reset"; } \
  && ok "T62 --reset-health is a safe no-op when not currently unhealthy" \
  || bad "T62 (rc=$RC, out: $(echo "$OUT" | tail -2 | tr '\n' '|'))"

# ---- T63: --force does NOT clear the health latch -- only --reset-health may. Proves
#      the two are genuinely separate mechanisms; an operator's habitual --force reflex
#      must never double as reviewing a possible mass-deletion/encryption event. ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
printf 'archive\tzht:health\tunhealthy\n' > "$WORK/fz/dsprops"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ echo "$OUT" | grep -qi "archive: WORRY - already latched unhealthy" && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T63 --force does not clear the health latch; still frozen" \
  || bad "T63 (out: $(echo "$OUT" | grep -i worry))"

# ---- T64: --dataset filtering a run to ONE dataset must not blind the pool-wide
#      pre-pass to a shock on a DIFFERENT, non-selected dataset -- the pre-pass always
#      scans every configured dataset regardless of --dataset. ----
cat > "$WORK/cfg_t64.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": 2,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline", "archive/scratch": "baseline" } }
EOF
n64="$(gen_window $((NOW-7200)) | wc -l)"
gen_window $((NOW-7200)) | awk -F'\t' 'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824,10485760}' > "$WORK/t64"
gen_window $((NOW-7200)) | sed 's#archive/home/rich#archive/scratch#' \
  | awk -F'\t' -v n="$n64" 'BEGIN{OFS="\t"} {print $1,$2,$3,(NR==n?20971520:104857600),5242880}' >> "$WORK/t64"
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/t64" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_t64.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force --dataset archive/home/rich 2>/dev/null)"
{ echo "$OUT" | grep "archive/home/rich:" | grep -qi "SKIP" \
  && ! echo "$OUT" | grep -q "^archive/scratch:" \
  && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T64 --dataset filter doesn't blind the pool pre-pass to a shock on another dataset" \
  || bad "T64 (out: $(echo "$OUT" | tr '\n' '|'))"

# ---- T65: a clean --prune with no prior health state explicitly writes health=healthy
#      -- not left implicit/absent (mirrors T59's pruned_through check). ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
  ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1
health65="$(health_of "$WORK/fz/dsprops")"
[ "$health65" = "healthy" ] \
  && ok "T65 a clean --prune with no prior health state explicitly writes health=healthy" \
  || bad "T65 (health=$health65)"

# ---- T70: a content shock persists a free-text reason (<ns>:health_reason) -- not just
#      the healthy/unhealthy enum -- so an operator reviewing later (via --summary or
#      --doctor) can see WHAT tripped it without needing to log into the machine and grep
#      the day's log. ----
nlines70="$(gen_window $((NOW-7200)) | wc -l)"
gen_window $((NOW-7200)) | awk -F'\t' -v n="$nlines70" \
  'BEGIN{OFS="\t"} {print $1,$2,$3,(NR==n?104857600:1073741824)}' > "$WORK/t70shock"   # newest shrinks 90%
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/t70shock" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
  ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1
reason70="$(health_reason_of "$WORK/fz/dsprops")"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --summary 2>/dev/null)"
OUT2="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --doctor 2>/dev/null)"
{ echo "$reason70" | grep -qi "shrank" \
  && echo "$OUT" | grep -q "archive: WORRY -.*shrank" \
  && echo "$OUT2" | grep -q "archive: WORRY -.*shrank"; } \
  && ok "T70 content shock persists a reason, surfaced by --summary and --doctor without reading the log" \
  || bad "T70 (reason=$reason70, summary: $(echo "$OUT"|tail -3|tr '\n' '|'), doctor: $(echo "$OUT2"|head -3|tr '\n' '|'))"

# ---- T71: --reset-health prints the persisted reason before clearing it, and the
#      reason property is actually gone afterward (not left as a stale explanation for a
#      now-resolved incident). ----
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --reset-health 2>/dev/null)"
reason71="$(health_reason_of "$WORK/fz/dsprops")"
{ echo "$OUT" | grep -qi "Reason for the latch:.*shrank" && [ -z "$reason71" ]; } \
  && ok "T71 --reset-health shows the reason before clearing it" \
  || bad "T71 (out: $(echo "$OUT"|head -2|tr '\n' '|'), reason_after=$reason71)"

# ---- T72: --summary's two sections -- "## Snapshots" / "## Health" headers always
#      present, and the Health line always states the pool's state explicitly (unlike
#      the console log's silent-when-healthy WORRY convention -- this file is read
#      directly by a human, not grepped). ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
  ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --summary 2>/dev/null)"
{ echo "$OUT" | grep -qx "## Snapshots" \
  && echo "$OUT" | grep -qx "## Health" \
  && echo "$OUT" | grep -qx "archive: healthy"; } \
  && ok "T72 --summary always shows Snapshots + Health sections, explicit 'healthy' state" \
  || bad "T72 (out: $(echo "$OUT" | tr '\n' '|'))"

# ---- T73: before any --prune has ever run (health never set), --summary reports the
#      pool as "starting", not silently omitted or misreported as "healthy". ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --summary 2>/dev/null)"
echo "$OUT" | grep -qx "archive: starting" \
  && ok "T73 --summary reports pool state \"starting\" before any --prune has run" \
  || bad "T73 (out: $(echo "$OUT" | tr '\n' '|'))"

# ==== pruned_through bootstrap for an already-established dataset (docs/MODEL.md /
#      zfs-hold-tight.cs's prunedThrough comment): a dataset already managed before this
#      property existed must not have its entire pre-existing daily tail reported as an
#      unexplained gap just because the watermark is new -- the real incident this
#      session hit deploying the feature. Bootstrap only applies when everEstablished
#      (real prior <ns>:tier history); a genuinely fresh dataset gets no such pass. ====
gap66=$(date -u -d "$ADATE -1 days" +%Y-%m-%d)
grep -v "$gap66" "$WORK/healthy" > "$WORK/gap66"
establish_oldest "$WORK/gap66" "$WORK/gap66_established"

# ---- T66: established + never-set pruned_through + a real historical gap -> bootstrap
#      excuses it, no false WORRY (contrast with T67 below: same gap, NOT established). ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/gap66_established" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --force 2>/dev/null)"
{ ! echo "$OUT" | grep -qi "gap"; } \
  && ok "T66 established dataset, never-set pruned_through: bootstrap excuses the pre-existing gap (no false WORRY)" \
  || bad "T66 (out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

# ---- T67: same gap, NOT established (no prior <ns>:tier anywhere) -> bootstrap must
#      NOT apply; still a real, flagged gap. Proves T66's silence is the bootstrap
#      specifically, not the guard becoming permissive in general. ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/gap66" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --force 2>/dev/null)"
{ echo "$OUT" | grep -qi "gap"; } \
  && ok "T67 NOT established, never-set pruned_through: the same gap is still flagged (no bootstrap)" \
  || bad "T67 (out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

# ---- T68: the bootstrap value actually gets PERSISTED as today's date on a healthy
#      --prune (not just a transient in-memory pass for this one run's guard check). ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/gap66_established" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
  ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1
pt68=$(awk -F'\t' -v d="archive/home/rich" -v p="zht:pruned_through" '$1==d && $2==p{print $3}' "$WORK/fz/dsprops" 2>/dev/null)
pt68_date=$(date -u -d "@${pt68:-0}" +%Y-%m-%d 2>/dev/null)
real_today68=$(date -u +%Y-%m-%d)
{ [ "$pt68_date" = "$ADATE" ] || [ "$pt68_date" = "$real_today68" ]; } \
  && ok "T68 the bootstrap value is actually persisted as today's pruned_through ($pt68_date)" \
  || bad "T68 (pt68_date=$pt68_date, expected=$ADATE or $real_today68)"

# ---- T69: --summary shows the same bootstrap behavior -- Continuity is clean ("✓"),
#      not a break, for the same established+never-set-pruned_through+historical-gap
#      case as T66. ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/gap66_established" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --summary 2>/dev/null)"
{ echo "$OUT" | grep "archive/home/rich" | grep -q '✓'; } \
  && ok "T69 --summary: established dataset with never-set pruned_through shows clean Continuity" \
  || bad "T69 (out: $(echo "$OUT" | grep 'archive/home/rich'))"

# ---- T5: future-dated snapshot -> HOLD ----
cp "$WORK/healthy" "$WORK/future"
printf '%s\t-\t-\n' "$(snapname "$(date -u -d "$ADATE +2 days" +%Y-%m-%d)")" >> "$WORK/future"
run_tool "$WORK/future" --force
{ echo "$OUT" | grep -qi "future"; } && ok "T5 future clock -> WORRY+SKIP" || bad "T5 (out: $(echo "$OUT"|tail -1))"

# ---- T6: malformed date must NOT crash (no SIGABRT=134) ----
cp "$WORK/healthy" "$WORK/bad"
printf 'archive/home/rich@autosnap_2026-13-45_%s_daily\t-\t-\n' "$TIME" >> "$WORK/bad"
run_tool "$WORK/bad" --force
[ "$RC" != 134 ] && ok "T6 malformed date no crash (rc=$RC)" || bad "T6 crashed (rc=$RC)"

# ---- T7: metadata (zfs set) failure -> HOLD, 0 destroys ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=set ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ echo "$OUT" | grep -qi "SKIP" && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T7 metadata failure -> SKIP, 0 destroys" || bad "T7 (out: $(echo "$OUT"|tail -1))"

# ---- T8: invalid tier rejected ----
run_tool "$WORK/healthy" --hold-latest-tier garbage
[ "$RC" = 2 ] && ok "T8 invalid tier rejected (rc=$RC)" || bad "T8 (rc=$RC)"

# ---- T9: --check-holds runs ----
run_tool "$WORK/healthy" --check-holds
{ { [ "$RC" = 0 ] || [ "$RC" = 1 ]; } && echo "$OUT" | grep -qi "check-holds"; } \
  && ok "T9 --check-holds wired" || bad "T9 (rc=$RC)"

# ---- T10: adhoc-held expired snapshot skipped, not destroy-failed (guard 14) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"
held="$(snapname 2025-06-15)"                      # old, not a keeper, expired
printf '%s\tzht:adhoc\t-\n' "$held" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ ! grep -qx "destroy $held" "$WORK/fz/ops.log"; } \
  && ok "T10 adhoc-held expired snap not destroyed (guard 14)" || bad "T10 held snap destroyed"

# ---- T11: --hold-adhoc rejects target outside archive/ allowlist ----
run_tool "$WORK/healthy" --hold-adhoc "tank/secret@autosnap_${ADATE}_${TIME}_daily"
[ "$RC" = 2 ] && ok "T11 hold-adhoc rejects non-archive/ target (rc=$RC)" || bad "T11 (rc=$RC)"

# ---- T12: run-lock blocks a concurrent prune (second exits rc=3) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
env FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_SLEEP=2 ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" \
    dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1 &
P1=$!; sleep 1
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" \
    dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1; RC2=$?
wait $P1
[ "$RC2" = 3 ] && ok "T12 second concurrent prune blocked by run-lock (rc=3)" || bad "T12 (rc2=$RC2)"

# ---- T14: sudden data shrinkage (newest daily references far less) -> HOLD ----
# 4-col fixture (name, keep_until, tier, referenced[bytes]); baseline 1GiB, newest 100MiB (~90% drop)
nlines="$(gen_window $((NOW-7200)) | wc -l)"
gen_window $((NOW-7200)) | awk -F'\t' -v n="$nlines" \
  'BEGIN{OFS="\t"} {print $1,$2,$3,(NR==n?104857600:1073741824)}' > "$WORK/shrink"
run_tool "$WORK/shrink" --force
{ echo "$OUT" | grep -qi "shrank\|shrink\|deletion"; } && ok "T14 data shrinkage -> WORRY+SKIP" || bad "T14 (out: $(echo "$OUT"|tail -1))"

# ---- T15: stable referenced (no shrink) -> shrink guard does NOT false-positive ----
gen_window $((NOW-7200)) | awk -F'\t' 'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824}' > "$WORK/stable"
run_tool "$WORK/stable" --force
{ ! echo "$OUT" | grep -qi "shrank\|shrink"; } && ok "T15 stable referenced: no false shrink-SKIP" || bad "T15 false positive (out: $(echo "$OUT"|tail -1))"

# ---- T16: churn spike (newest daily rewrites most of the dataset) -> HOLD ----
# 5-col fixture (..., referenced, written); referenced stable 1GiB, newest written 900MiB (~88% churn)
nlines="$(gen_window $((NOW-7200)) | wc -l)"
gen_window $((NOW-7200)) | awk -F'\t' -v n="$nlines" \
  'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824,(NR==n?943718400:10485760)}' > "$WORK/churn"
run_tool "$WORK/churn" --force
{ echo "$OUT" | grep -qi "churn\|rewrite\|encryption"; } && ok "T16 churn spike -> WORRY+SKIP" || bad "T16 (out: $(echo "$OUT"|tail -1))"

# ---- T17: normal churn -> churn guard does NOT false-positive ----
gen_window $((NOW-7200)) | awk -F'\t' 'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824,10485760}' > "$WORK/lowchurn"
run_tool "$WORK/lowchurn" --force
{ ! echo "$OUT" | grep -qi "churn"; } && ok "T17 normal churn: no false churn-SKIP" || bad "T17 false positive (out: $(echo "$OUT"|tail -1))"

# ---- T18: cross-dataset freeze — a small (100MiB) dataset wiped 80% freezes the ENTIRE
#      pool, not just itself. This reverses what this test used to check ("per-dataset
#      isolation," the old default): docs/MODEL.md "Cross-dataset shock" argues isolation
#      is the wrong default for a ransomware threat model -- an attacker on the source
#      machine plausibly hit every dataset, not just the one loud enough to trip its own
#      threshold, so any single shock must distrust the WHOLE pool, including an
#      otherwise-healthy large dataset. ----
cat > "$WORK/cfg2.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": 2,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline", "archive/scratch": "baseline" } }
EOF
n2="$(gen_window $((NOW-7200)) | wc -l)"
# healthy 1GiB dataset
gen_window $((NOW-7200)) | awk -F'\t' 'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824,10485760}' > "$WORK/d2"
# small 100MiB dataset whose newest daily references only 20MiB (80% drop)
gen_window $((NOW-7200)) | sed 's#archive/home/rich#archive/scratch#' \
  | awk -F'\t' -v n="$n2" 'BEGIN{OFS="\t"} {print $1,$2,$3,(NR==n?20971520:104857600),5242880}' >> "$WORK/d2"
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/d2" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg2.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --force 2>/dev/null)"
{ echo "$OUT" | grep "archive/scratch:" | grep -qi "shrank" \
  && echo "$OUT" | grep "archive/home/rich:" | grep -qi "SKIP" \
  && echo "$OUT" | grep -qi "2 SKIPPED"; } \
  && ok "T18 cross-dataset freeze: shock on one dataset freezes the entire pool, not just itself" \
  || bad "T18 (out: $(echo "$OUT" | grep -E 'scratch|home/rich|SKIP|WORRY' | tr '\n' '|'))"

# ---- T19: an expired week/month keeper (past retention) ages out, not kept forever ----
old=$(date -u -d "$ADATE -2200 days" +%Y-%m-%d); oldsnap="$(snapname "$old")"   # ~6y old, sole rep of its week+month
{ gen_window $((NOW-7200)); printf '%s\t-\t-\n' "$oldsnap"; } > "$WORK/expired"
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/expired" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ grep -qx "destroy $oldsnap" "$WORK/fz/ops.log"; } \
  && ok "T19 expired keeper aged out (pruned, not immortal)" || bad "T19 expired keeper not pruned"

# ---- T20: --dataset with no value is a hard error (never silently all-datasets) ----
run_tool "$WORK/healthy" --prune --dataset
[ "$RC" = 2 ] && ok "T20 --dataset without value rejected (rc=2)" || bad "T20 (rc=$RC)"

# ---- T21: unknown --dataset rejected (not a silent no-op for --doctor/--fix/--hold-*) ----
run_tool "$WORK/healthy" --doctor --dataset archive/does-not-exist
[ "$RC" = 2 ] && ok "T21 unknown --dataset rejected in --doctor (rc=2)" || bad "T21 (rc=$RC)"

# ---- T22: `zfs holds` failure SKIPs the dataset (can't verify guard 14), 0 destroys ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=holds ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ echo "$OUT" | grep -qi "holds" && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T22 zfs holds failure -> SKIP, 0 destroys" || bad "T22 (out: $(echo "$OUT"|tail -1))"

# ---- T23: --check-holds flags a hold older than its dataset's retention (per-policy) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"
old6="$(snapname "$(date -u -d "$ADATE -2200 days" +%Y-%m-%d)")"   # ~6y old > 1825d retention
printf '%s\tzht:monthly\t-\n' "$old6" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --check-holds 2>/dev/null)"; RC=$?
{ [ "$RC" = 1 ] && echo "$OUT" | grep "archive/home/rich:" | grep -qi "exceeds its .*retention"; } \
  && ok "T23 --check-holds flags hold past its policy retention (names dataset)" || bad "T23 (rc=$RC, out: $(echo "$OUT"|tail -1))"

# ---- T24: --doctor returns nonzero when zfs list fails (no false "validated") ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=list ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --doctor 2>/dev/null)"; RC=$?
[ "$RC" = 1 ] && ok "T24 --doctor nonzero on zfs list failure" || bad "T24 (rc=$RC)"

# ---- T24b: --doctor's zfs-list failure detail must reach the PERSISTED LOG FILE, not
#      just the console. ListDailies is a top-level local function; if it logged via a
#      Log() resolved at ITS OWN declaration site (the outer top-level scope) instead of
#      forwarding warnings back to the caller's own Log(), the detail would print to
#      Console fine but never reach whichever function's FlushLog(lines) actually persists
#      the log file — since --doctor keeps its own separate `lines` list.
#      Log filename found via `ls -t` (most recently modified), not by independently
#      recomputing `date -u +%F` in bash: the C# process stamps the filename with ITS OWN
#      `now` at process start, so a separate bash `date` call afterward could disagree by
#      one day if the run straddles UTC midnight — narrow, but a real source of flaky CI. ----
logfile24b="$(ls -t "$WORK"/logs/zfs-hold-tight-*.log 2>/dev/null | head -1)"
grep -q "ERROR: zfs list archive/home/rich failed:" "$logfile24b" \
  && ok "T24b --doctor's zfs-list failure detail reaches the log file (not console-only)" \
  || bad "T24b (logfile: $(cat "$logfile24b" 2>/dev/null | tr '\n' '|'))"

# ---- T25: --fix returns nonzero when a hold fails (no false "repaired") ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=hold ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --fix 2>/dev/null)"; RC=$?
[ "$RC" = 1 ] && ok "T25 --fix nonzero on hold failure" || bad "T25 (rc=$RC)"

# ---- T26: on demotion, if metadata cleanup fails the monthly hold is NOT released
#          (snapshot stays protected) and the dataset HOLDs (finding 3 ordering) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
recent="$DEMOTED_DATE"; demoted="$(snapname "$recent")"
awk -F'\t' -v d="$demoted" 'BEGIN{OFS="\t"} $1==d{$3="monthly"} {print}' "$WORK/healthy" > "$WORK/fz/snaps"
printf '%s\tzht:monthly\t-\n' "$demoted" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=inherit ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ ! grep -qx "release zht:monthly $demoted" "$WORK/fz/ops.log" && echo "$OUT" | grep -qi "SKIP"; } \
  && ok "T26 demotion: inherit-fail keeps the hold + SKIPs dataset" || bad "T26 (out: $(echo "$OUT"|tail -1))"

# ---- T28: a capped --prune (no --force) still HOLDs/tags keepers - protection only ever
#          increases, even when destroys are capped (reversed 2026-07-08: hold-tagging must
#          not be gated behind the volume circuit-breaker) - but performs zero destroys ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy_established" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune 2>/dev/null)"
destroys=$(grep -cE '^destroy' "$WORK/fz/ops.log"); destroys=${destroys:-0}
holds=$(grep -cE '^hold' "$WORK/fz/ops.log"); holds=${holds:-0}
{ echo "$OUT" | grep -qi "cap" && [ "$destroys" = 0 ] && [ "$holds" -gt 0 ]; } \
  && ok "T28 capped --prune still holds/tags keepers, zero destroys" || bad "T28 destroys=$destroys holds=$holds (out: $(echo "$OUT"|tail -1))"

# ---- T27: monthly keepers already held are NOT re-held every prune (finding 3) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/ops.log"
: > "$WORK/fz/holds"
while read -r kd; do printf '%s\tzht:monthly\t-\n' "$(snapname "$kd")" >> "$WORK/fz/holds"; done < "$WORK/mk"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
[ "$(grep -c '^hold zht:monthly' "$WORK/fz/ops.log")" = 0 ] \
  && ok "T27 already-held monthlies not re-held" || bad "T27 $(grep -c '^hold zht:monthly' "$WORK/fz/ops.log") redundant holds"

# ---- T29: protect-on-worry — a guard worry (shrink) still HOLDs + 0 destroys, but
#          proactively holds the monthly keeper (the suspect) and tags the worry ----
nlines="$(gen_window $((NOW-7200)) | wc -l)"
gen_window $((NOW-7200)) | awk -F'\t' -v n="$nlines" \
  'BEGIN{OFS="\t"} {print $1,$2,$3,(NR==n?104857600:1073741824)}' > "$WORK/worry"
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/worry" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ echo "$OUT" | grep -q 'WORRY' && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ] \
  && grep -q '^hold zht:monthly' "$WORK/fz/ops.log" && grep -q '^set zht:worry' "$WORK/fz/ops.log"; } \
  && ok "T29 protect-on-worry: WORRY + 0 destroys, but holds keeper + tags worry" \
  || bad "T29 (ops: $(grep -cE '^(hold|set zht:worry|destroy)' "$WORK/fz/ops.log") matches)"

# ---- T30: a weekly keeper that still carries a monthly hold gets it released (vestigial fix) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"
wkonly="$(snapname 2026-06-21)"   # Sunday (W25 keeper) but NOT June's newest -> weekly, not monthly
printf '%s\tzht:monthly\t-\n' "$wkonly" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ grep -qx "release zht:monthly $wkonly" "$WORK/fz/ops.log" && ! grep -qx "destroy $wkonly" "$WORK/fz/ops.log" \
  && echo "$OUT" | grep -q "^  HOLD:REMOVE $wkonly"; } \
  && ok "T30 weekly keeper's stray monthly hold released (held ⇔ monthly)" \
  || bad "T30 (ops: $(grep "$wkonly" "$WORK/fz/ops.log" | tr '\n' ';'))"

# ---- T31: per-dataset line reports the new model (keeper held-counts + the daily-tail frontier) ----
run_tool "$WORK/healthy" --prune --force
echo "$OUT" | grep -qE 'monthly \+ [0-9]+ weekly held; daily tail: >=[0-9]+ newer holds, to [0-9]{4}-[0-9]{2}-[0-9]{2}' \
  && ok "T31 summary line shows keeper held-counts + daily-tail frontier" || bad "T31 (out: $(echo "$OUT"|grep -i tail || echo none))"

# ---- T32: --version prints a version and exits 0 (works without config) ----
run_tool "$WORK/healthy" --version
{ [ "$RC" = 0 ] && echo "$OUT" | grep -qiE 'zfs-hold-tight [0-9]+\.'; } \
  && ok "T32 --version prints version, exit 0" || bad "T32 (rc=$RC, out: $OUT)"

# ---- T33: log carries distinct, greppable signal tokens ----
run_tool "$WORK/healthy" --prune --force
H=$(echo "$OUT" | grep -c '^  HOLD:ADD '); M=$(echo "$OUT" | grep -cE '^  META:(ADD|UPDATE|REMOVE) '); D=$(echo "$OUT" | grep -c '^  SNAP:DESTROY ')
{ [ "$H" -gt 0 ] && [ "$M" -gt 0 ] && [ "$D" -gt 0 ]; } \
  && ok "T33 distinct log tokens HOLD:ADD/META:*/SNAP:DESTROY ($H/$M/$D)" || bad "T33 H=$H M=$M D=$D"

# ---- T13: a snapshot no longer a keeper gets its keeper metadata + hold CLEANED UP (demotion):
#          <ns>:tier is inherited away and the <ns>:monthly hold released. (Under the two-holds
#          invariant this mid-week daily is legitimately below the frontier, so whether it is then
#          pruned is calendar-dependent; T13 asserts only the cleanup, T26 covers the fail-safe.) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
recent="$DEMOTED_DATE"; demoted="$(snapname "$recent")"
# stamp it as a stale monthly (col3=monthly) with a zht:monthly hold, though it is NOT the month keeper
awk -F'\t' -v d="$demoted" 'BEGIN{OFS="\t"} $1==d{$3="monthly"} {print}' "$WORK/healthy" > "$WORK/fz/snaps"
printf '%s\tzht:monthly\t-\n' "$demoted" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ grep -qx "release zht:monthly $demoted" "$WORK/fz/ops.log" \
  && grep -qx "inherit zht:tier $demoted" "$WORK/fz/ops.log" \
  && ! echo "$OUT" | grep -qi "SKIP"; } \
  && ok "T13 demoted monthly: hold released + tags inherited (metadata cleaned up)" \
  || bad "T13 (ops: $(grep "$demoted" "$WORK/fz/ops.log" | tr '\n' ';'))"

# ==== two-holds INVARIANT tests ====
# Fixture: INV_N-4(Sat) INV_N-3(Sun) INV_N-2(Mon) INV_N-1(Tue) INV_N(Wed).
#   ISO weeks: {N-2,N-1,N}->keeper N ; {N-4,N-3}->keeper N-3.  Month keeper: N.
#   Held keepers newest-first: [N, N-3].  With minNewerHolds=2 (fixed here, independent of
#   whatever the compiled default is — see T-default below for that), frontier = N-3.
#   => N-4 has TWO newer held keepers (N, N-3) -> DROPPED.
#      N-2/N-1 have ONE newer held keeper (N)  -> KEPT.

# ---- T34: core invariant — exactly ONE newer held keeper keeps a daily, TWO drops it ----
inv_config 2                                   # pinned N=2, not the compiled default
inv_feed "$WORK/inv"
run_inv "$WORK/inv" --prune --force
d4="$(snapname "$(inv_day 4)")"; d3="$(snapname "$(inv_day 3)")"
d2="$(snapname "$(inv_day 2)")"; d1="$(snapname "$(inv_day 1)")"
{ grep -qx "destroy $d4" "$OPS" \
  && ! grep -qx "destroy $d2" "$OPS" && ! grep -qx "destroy $d1" "$OPS" \
  && ! grep -qx "destroy $d3" "$OPS"; } \
  && ok "T34 invariant: 2 newer keepers drop a daily, 1 newer keeper keeps it" \
  || bad "T34 (destroys: $(grep '^destroy' "$OPS" | tr '\n' ';'))"

# ---- T35: weekly keepers now receive a real zfs hold (<ns>:weekly) ----
{ grep -qx "hold zht:weekly $d3" "$OPS" && ! grep -qx "destroy $d3" "$OPS"; } \
  && ok "T35 weekly keeper gets a zfs hold (zht:weekly), not destroyed" \
  || bad "T35 (ops for $d3: $(grep "$d3" "$OPS" | tr '\n' ';'))"

# ---- T36: minNewerHolds tunes the frontier — deeper = longer daily tail ----
inv_config 3                                   # need THREE newer held keepers; only two exist
run_inv "$WORK/inv" --prune --force
drop3=$(grep -c '^destroy' "$OPS"); drop3=${drop3:-0}
inv_config 1                                   # ONE newer held keeper suffices
run_inv "$WORK/inv" --prune --force
{ [ "$drop3" = 0 ] && grep -qx "destroy $d4" "$OPS" \
  && grep -qx "destroy $d2" "$OPS" && grep -qx "destroy $d1" "$OPS"; } \
  && ok "T36 minNewerHolds tunes the frontier (=3 keeps all dailies; =1 drops N-1/N-2/N-4)" \
  || bad "T36 (drop@3=$drop3, destroys@1: $(grep '^destroy' "$OPS" | tr '\n' ';'))"

# ---- T36b: a retention policy may override the global minNewerHolds ----
cat > "$WORK/cfg_inv.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": 2,
  "guardMaxDeletePerRun": 50, "guardRecentWindowDays": 3, "guardMaxDailyAgeHours": 720,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 1, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825, "minNewerHolds": 1 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
run_inv "$WORK/inv" --prune --force
{ grep -qx "destroy $d4" "$OPS" && grep -qx "destroy $d2" "$OPS" && grep -qx "destroy $d1" "$OPS"; } \
  && ok "T36b per-policy minNewerHolds override (policy=1 drops tail despite global=2)" \
  || bad "T36b (destroys: $(grep '^destroy' "$OPS" | tr '\n' ';'))"

# ---- T55: the COMPILED DEFAULT of minNewerHolds is 3, not 2 — a config that OMITS the key
#      entirely, against the SAME 5-day invariant fixture T34/T36 use, must behave like
#      T36's explicit `inv_config 3` (0 destroys: only ONE newer held keeper (N) exists
#      relative to N-4/N-2/N-1, one short of three). Bumped from 2 because the two-holds
#      invariant alone lets a whole ISO-week's worth of plain dailies sit at "exactly 1
#      newer hold" simultaneously and cross to "doomed" together the instant the next
#      week's keeper appears (see the "week-long cliff" note above the destroy loop in
#      zfs-hold-tight.cs) — one more required hold buys a full extra week of buffer. ----
inv_config -                                   # omit the key entirely -> compiled default
run_inv "$WORK/inv" --prune --force
defaultdrops=$(grep -c '^destroy' "$OPS"); defaultdrops=${defaultdrops:-0}
[ "$defaultdrops" = 0 ] \
  && ok "T55 compiled default minNewerHolds is 3 (0 destroys on the invariant fixture, matching inv_config 3)" \
  || bad "T55 (destroys=$defaultdrops: $(grep '^destroy' "$OPS" | tr '\n' ';'))"

# ---- T37: on the healthy feed, EVERY destroyed non-keeper has >=2 surviving newer keepers
#          (the tool never violates the two-holds invariant) ----
run_tool "$WORK/healthy" --prune --force
grep '^destroy' "$OPS" | awk '{print $2}' | sed -E 's/.*autosnap_([0-9-]+)_.*/\1/' | sort -u > "$WORK/ddates"
cat "$WORK/mk" "$WORK/wk" | sort -u > "$WORK/keeperdates"   # $WORK/mk,$WORK/wk built in T2 (all keepers survive)
inv_viol=0
while read -r dd; do
  cnt=$(awk -v d="$dd" '$1>d{c++} END{print c+0}' "$WORK/keeperdates")
  [ "$cnt" -lt 2 ] && { echo "    $dd has only $cnt newer keepers"; inv_viol=1; }
done < "$WORK/ddates"
{ [ "$inv_viol" = 0 ] && [ -s "$WORK/ddates" ]; } \
  && ok "T37 no invariant violation: every destroyed daily has >=2 surviving newer keepers" \
  || bad "T37 inv_viol=$inv_viol destroyed=$(wc -l < "$WORK/ddates")"

# ---- T38: weekly->monthly tier change SWAPS the hold (adds monthly, then releases weekly;
#          never transiently unheld, no stray double-hold after convergence) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"
newestsnap="$(snapname "$ADATE")"                 # the newest = this month's keeper (monthly)
printf '%s\tzht:weekly\t-\n' "$newestsnap" > "$WORK/fz/holds"   # but currently wrongly held as weekly
: > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
addln=$(grep -n "^hold zht:monthly $newestsnap" "$WORK/fz/ops.log" | head -1 | cut -d: -f1)
relln=$(grep -n "^release zht:weekly $newestsnap" "$WORK/fz/ops.log" | head -1 | cut -d: -f1)
{ [ -n "$addln" ] && [ -n "$relln" ] && [ "$addln" -lt "$relln" ] \
  && grep -qP "^$(printf '%s' "$newestsnap" | sed 's/[.[\*^$]/\\&/g')\tzht:monthly\t" "$WORK/fz/holds" \
  && ! grep -qP "^$(printf '%s' "$newestsnap" | sed 's/[.[\*^$]/\\&/g')\tzht:weekly\t" "$WORK/fz/holds"; } \
  && ok "T38 weekly->monthly swaps the hold add-then-release (no unheld gap, no double-hold)" \
  || bad "T38 (add@$addln rel@$relln, final holds: $(grep "$newestsnap" "$WORK/fz/holds" | tr '\n' ';'))"

# ---- T39/T40/T41/T42: cold-start floor — a dataset with no prior <ns>:tier (never
#      established) requires --force for ANY nonzero doomed count, regardless of the
#      configured guardMaxDeletePerRun (14, sized for a routine weekly batch on an
#      established dataset), and Promote must NOT run while blocked (no "tier=" writes in
#      ops.log) — that's the mechanism that stops a blocked run from self-clearing the
#      floor. Once established, the SAME backlog proceeds under the routine cap with no
#      --force needed. guardRecentWindowDays is widened to 10 here (vs. the 7 default)
#      specifically so T42's regression check (does a prior prune's own destroys avoid
#      falsely tripping guard 1) reliably has overlap to detect on every weekday — the
#      hold frontier can land up to 6 days back depending on which day "today" is, and a
#      7-day window doesn't always reach that far, making the check pass vacuously on
#      Sat/Sun anchors regardless of whether the guard-1 fix is actually working. ----
# dailyKeepDays pinned to 1 (not the real default of 30): this whole T39-T44 block uses
# ~13-21 day fixtures well inside a realistic floor, which would zero out the doomed count
# these tests exist to produce. minNewerHolds pinned to 2 for the same reason as
# config.json above (these tests aren't about tuning either knob).
cat > "$WORK/cfg_cold.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "guardMaxDeletePerRun": 14, "guardRecentWindowDays": 10,
  "minNewerHolds": 2,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 1, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
gen_feed "$(date -u -d "$ADATE -20 days" +%Y-%m-%d)" > "$WORK/cold"   # ~21 raw days, all tier=-
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/cold" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_cold.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune 2>/dev/null)"
{ echo "$OUT" | grep -qi "cold start" \
  && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ] \
  && [ "$(grep -c 'hold ' "$WORK/fz/ops.log")" -gt 0 ] \
  && [ "$(grep -c 'tier=' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T39 cold-start floor: unforced prune on never-processed dataset WORRYs, holds keepers, 0 destroys, Promote skipped (no tier writes)" \
  || bad "T39 (out: $(echo "$OUT" | grep -i "cold start\|WORRY" | tr '\n' '|'); destroys=$(grep -c '^destroy' "$WORK/fz/ops.log"); tier-writes=$(grep -c 'tier=' "$WORK/fz/ops.log"))"

rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/cold" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_cold.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ ! echo "$OUT" | grep -qi "cold start" \
  && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" -gt 0 ] \
  && [ "$(grep -c 'tier=' "$WORK/fz/ops.log")" -gt 0 ]; } \
  && ok "T40 cold-start floor: --force bypasses it, Promote runs (tier written), real destroys happen" \
  || bad "T40 (out: $(echo "$OUT" | grep -i "cold start\|WORRY" | tr '\n' '|'); destroys=$(grep -c '^destroy' "$WORK/fz/ops.log"); tier-writes=$(grep -c 'tier=' "$WORK/fz/ops.log"))"

# T41: an ALREADY-established dataset (one snapshot pre-tagged, simulating a prior
# completed prune) with a SMALL backlog proceeds under the routine cap (14) with NO
# --force needed, and actually destroys something — proving the floor is specifically
# about "never established," not just "backlog is nonzero." Uses its own short (~13-day)
# fixture, NOT $WORK/cold (~21 days): a 21-day backlog's doomed count varies with which
# weekday "today" is (11-17 depending on ISO-week alignment) and can exceed 14, in which
# case even an established dataset legitimately gets volume-capped (0 destroys) — that's
# correct guard-6 behavior, but it would make this test pass vacuously (no "cold start"
# text appears either way) without proving what it claims. ~13 days keeps doomed
# comfortably under 14 regardless of weekday, and asserting destroys>0 makes the test
# fail loudly instead of passing vacuously if that assumption ever breaks.
gen_feed "$(date -u -d "$ADATE -13 days" +%Y-%m-%d)" > "$WORK/small"
establish_oldest "$WORK/small" "$WORK/small_established"
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/small_established" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_cold.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune 2>/dev/null)"
{ ! echo "$OUT" | grep -qi "cold start" \
  && ! echo "$OUT" | grep -qi "> cap" \
  && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" -gt 0 ]; } \
  && ok "T41 cold-start floor: an already-established dataset is NOT blocked by the floor, small backlog proceeds and destroys without --force" \
  || bad "T41 (out: $(echo "$OUT" | grep -i "cold start\|WORRY" | tr '\n' '|'); destroys=$(grep -c '^destroy' "$WORK/fz/ops.log"))"

# T42: the genuine multi-run lifecycle T39/T40/T41 approximate via pre-tagged fixtures —
# T40's forced run against the SAME fixture dir (not wiped), then a second, unforced
# run: does establishment actually carry over via persisted <ns>:tier (exercising the
# tests/fake-zfs `set`-persistence this diff added), or does it only work because T41's
# fixture was hand-tagged? Reuses the small (~13-day) backlog so the routine cap can't
# confound the result.
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/small" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_cold.json" \
  ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1
first_destroys=$(grep -c '^destroy' "$WORK/fz/ops.log")
: > "$WORK/fz/ops.log"   # clear ops.log only — snaps/holds carry forward, as a real second invocation would see
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_cold.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune 2>/dev/null)"
{ [ "$first_destroys" -gt 0 ] && ! echo "$OUT" | grep -qi "cold start" && ! echo "$OUT" | grep -qi "gap in last"; } \
  && ok "T42 establishment persists across separate invocations (not just within one run) via real <ns>:tier writes, and the prior run's own destroys don't trip guard 1's gap check" \
  || bad "T42 (first_destroys=$first_destroys, second out: $(echo "$OUT" | grep -i "cold start\|gap\|WORRY" | tr '\n' '|'))"

# ---- T44: the COMPILED DEFAULT of guardMaxDeletePerRun is 14, not just a value every
#      test config happens to set explicitly. Uses a config that OMITS the key entirely
#      (unlike every other test config here), against the established ~21-day "$WORK/cold"
#      backlog, whose doomed count (11-17, see T41's comment) straddles 14 depending on
#      weekday — either branch below rules out the OLD default of 5: if it destroys
#      without a cap trip, a backlog >5 got through uncapped; if it DOES cap (doomed
#      15-17), the message must name "cap 14" specifically, which also pins the exact
#      value (the no-cap-trip branch only proves >5, not =14). ----
establish_oldest "$WORK/cold" "$WORK/cold_established"
cat > "$WORK/cfg_nocap.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "guardRecentWindowDays": 10, "minNewerHolds": 2,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 1, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/cold_established" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_nocap.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune 2>/dev/null)"
destroys=$(grep -c '^destroy' "$WORK/fz/ops.log")
if echo "$OUT" | grep -qi "> cap"; then
  { echo "$OUT" | grep -q "> cap 14"; } \
    && ok "T44 default guardMaxDeletePerRun is 14 (cap trip named it explicitly)" \
    || bad "T44 (cap message didn't name 14: $(echo "$OUT" | grep -i "> cap"))"
else
  { [ "$destroys" -gt 5 ]; } \
    && ok "T44 default guardMaxDeletePerRun is 14 (backlog >5 proceeded uncapped, ruling out the old default of 5)" \
    || bad "T44 (destroys=$destroys, out: $(echo "$OUT" | tail -1))"
fi

# ---- T45/T46/T47/T48: --summary markdown table ----
# T45: dense, gap-free feed -> header line + a clean single-row table (Continuity "✓").
run_tool "$WORK/healthy" --summary
{ echo "$OUT" | grep -q '^| Dataset | Monthlies | Weeklies | Dailies | Start | End | Continuity |$' \
  && echo "$OUT" | grep -q "| archive/home/rich |" \
  && echo "$OUT" | grep -q "| $ADATE | ✓ |"; } \
  && ok "T45 --summary: gap-free feed -> header + clean row" \
  || bad "T45 (out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

# T46: a gap inside the daily tail (yesterday — always within the frontier, see T4's
# reasoning, which this reuses) must show up in Continuity, not be silently excused.
gap46=$(date -u -d "$ADATE -1 days" +%Y-%m-%d)
grep -v "$gap46" "$WORK/healthy" > "$WORK/gap46"
run_tool "$WORK/gap46" --summary
echo "$OUT" | grep -q "$gap46" \
  && ok "T46 --summary: gap in daily tail shown in Continuity" \
  || bad "T46 (gap=$gap46, out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

# T47: a gap 100 days back sits well beyond Summary's bounded fallback scan (no
# pruned_through fixture is set here, so it falls back to guardRecentWindowDays=7 back
# from newest — see the scanStart comment in Summary() — so 100 gives a wide safety
# margin) -> excused, same "✓" as a genuinely gap-free feed. Same narrow scope as guard 1.
gap47=$(date -u -d "$ADATE -100 days" +%Y-%m-%d)
grep -v "$gap47" "$WORK/healthy" > "$WORK/gap47"
run_tool "$WORK/gap47" --summary
{ ! echo "$OUT" | grep -q "$gap47" && echo "$OUT" | grep -q '✓'; } \
  && ok "T47 --summary: gap beyond the frontier is excused, not flagged" \
  || bad "T47 (gap=$gap47, out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

# T48: --since narrows the reported window — Start reflects the requested date (not the
# feed's true oldest, 2025-06-01).
since48=$(date -u -d "$ADATE -20 days" +%Y-%m-%d)
run_tool "$WORK/healthy" --summary --since "$since48"
echo "$OUT" | grep -q "| $since48 | $ADATE |" \
  && ok "T48 --summary --since narrows Start to the requested date" \
  || bad "T48 (since=$since48, out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

# T49: --since is only clamped to a LOWER bound (oldest); a --since AFTER the newest
# snapshot must not silently produce a Start>End row with a false "✓" Continuity (an
# entirely out-of-range/empty window looking identical to a genuinely gap-free one) — it
# must be reported explicitly instead.
future49=$(date -u -d "$ADATE +30 days" +%Y-%m-%d)
run_tool "$WORK/healthy" --summary --since "$future49"
{ echo "$OUT" | grep -q "after the newest snapshot" && ! echo "$OUT" | grep -q "| $future49 | $ADATE | ✓ |"; } \
  && ok "T49 --summary --since after newest is reported, not silently \"✓\"" \
  || bad "T49 (since=$future49, out: $(echo "$OUT" | tail -3 | tr '\n' '|'))"

# T50: a zfs-list failure during --summary must be reported ONLY as this row's own
# "ERROR: zfs list failed (see log)" cell on stdout — not ALSO as a separate, untabulated
# "ERROR: zfs list <ds> failed: ..." line, which would break the piped markdown's table
# contiguity (nightly.sh pipes --summary's stdout straight to the pushed summary file) —
# but the full
# detail must still land in the day's LOG FILE (not just silently discarded).
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=list ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --summary 2>/dev/null)"
logfile="$(ls -t "$WORK"/logs/zfs-hold-tight-*.log 2>/dev/null | head -1)"
{ echo "$OUT" | grep -q "| archive/home/rich | — | — | — | — | — | ERROR: zfs list failed (see log) |" \
  && ! echo "$OUT" | grep -qE '^ERROR: zfs list ' \
  && grep -q "ERROR: zfs list archive/home/rich failed:" "$logfile"; } \
  && ok "T50 --summary: zfs-list failure stays inside the table (no stray line) but full detail reaches the log file" \
  || bad "T50 (out: $(echo "$OUT" | tr '\n' '|'); logfile: $(cat "$logfile" 2>/dev/null | tr '\n' '|'))"

# T51: an unparseable-snapshot-date warning (a regex-shaped but out-of-range date, see T6's
# $WORK/bad fixture) during --summary must be noted compactly in the Continuity cell — not
# silently dropped (the risk quiet:true reintroduced for this warning type specifically,
# distinct from T50's zfs-list-failure case) and not a stray untabulated console line
# either; full detail must still reach the day's log file.
run_tool "$WORK/bad" --summary
logfile51="$(ls -t "$WORK"/logs/zfs-hold-tight-*.log 2>/dev/null | head -1)"
{ echo "$OUT" | grep -q "snapshot(s) skipped, see log" \
  && ! echo "$OUT" | grep -qE '^  WARNING: unparseable' \
  && grep -q "WARNING: unparseable snapshot date, skipping:.*2026-13-45" "$logfile51"; } \
  && ok "T51 --summary: unparseable-date warning noted in Continuity, full detail in log" \
  || bad "T51 (out: $(echo "$OUT" | tail -3 | tr '\n' '|'); logfile: $(grep -c unparseable "$logfile51" 2>/dev/null))"

# ---- T52: the hold frontier must never be anchored on a keeper that is ITSELF destroyed
#      in the same run. If it were, a plain daily could be doomed because a keeper "N
#      positions newer" than it still existed at the START of the run's frontier
#      computation — but if that SAME keeper is independently past its own weekly/monthly
#      retention, this run would destroy the keeper too, retroactively leaving the daily
#      with fewer than minNewerHolds SURVIVING newer keepers. Reproduced with short
#      weekly/monthly retention (10d) relative to minNewerHolds=3, so the 3rd-newest
#      keeper is reliably already past its own retention (~14-21 days back at ~weekly
#      cadence) by the time it would otherwise anchor the frontier. Not reachable with
#      this tool's shipped 5-year retention defaults — this guards the general case. ----
cfg52="$WORK/cfg_expiring_frontier.json"
cat > "$cfg52" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": 3,
  "guardMaxDeletePerRun": 200, "guardRecentWindowDays": 3, "guardMaxDailyAgeHours": 720,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 10, "monthlyKeepDays": 10 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
gen_feed "$(date -u -d "$ADATE -45 days" +%Y-%m-%d)" > "$WORK/expiring_frontier"
establish_oldest "$WORK/expiring_frontier" "$WORK/expiring_frontier_established"

rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
cp "$WORK/expiring_frontier_established" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$cfg52" ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" \
  dotnet run "$TARGET" -- --no-hc --prune --force >/dev/null 2>&1

# All structurally-elected keeper dates (newest-per-ISO-week ∪ newest-per-month) — same
# awk pattern T2 already uses (line ~144-146) to compute $WORK/mk / $WORK/wk.
awk -F'\t' '{print $1}' "$WORK/expiring_frontier" | sed -E 's/.*autosnap_([0-9-]+)_.*/\1/' > "$WORK/dates52"
awk '{m=substr($1,1,7); last[m]=$1} END{for(k in last)print last[k]}' "$WORK/dates52" > "$WORK/mk52"
while read -r d; do echo "$(date -u -d "$d" +%G-%V) $d"; done < "$WORK/dates52" \
  | awk '{last[$1]=$2} END{for(k in last)print last[k]}' > "$WORK/wk52"
cat "$WORK/mk52" "$WORK/wk52" | sort -u > "$WORK/allkeeperdates52"

grep '^destroy' "$WORK/fz/ops.log" | awk '{print $2}' | sed -E 's/.*autosnap_([0-9-]+)_.*/\1/' | sort -u > "$WORK/destroyed52"
comm -23 <(sort -u "$WORK/allkeeperdates52") "$WORK/destroyed52" > "$WORK/survivingkeepers52"

inv_viol52=0
while read -r dd; do
  grep -qx "$dd" "$WORK/allkeeperdates52" && continue    # keepers expire by date, not this rule
  cnt=$(awk -v d="$dd" '$1>d{c++} END{print c+0}' "$WORK/survivingkeepers52")
  [ "$cnt" -lt 3 ] && { echo "    $dd has only $cnt SURVIVING newer keepers"; inv_viol52=1; }
done < "$WORK/destroyed52"
{ [ "$inv_viol52" = 0 ] && [ -s "$WORK/destroyed52" ]; } \
  && ok "T52 frontier never anchored on a keeper destroyed the same run (surviving-keeper invariant holds)" \
  || bad "T52 inv_viol=$inv_viol52 destroyed=$(wc -l < "$WORK/destroyed52" 2>/dev/null)"

# ---- T54: minNewerHolds is upper-bounded (1-10000), rejected with a clean FATAL + exit 2,
#      not silently allowed through as arbitrary user input — cheap insurance against any
#      arithmetic that uses this value (kept even though guardMaxDeletePerRun is no longer
#      derived FROM it; see the "Considered and rejected" note in zfs-hold-tight.cs).
#      Covers both validation sites: the per-policy check and the global top-level check. ----
cat > "$WORK/cfg_huge_policy.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs",
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825, "minNewerHolds": 700000000 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
OUT="$(ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_huge_policy.json" ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" \
       dotnet run "$TARGET" -- --no-hc 2>&1)"; RC=$?
{ [ "$RC" = 2 ] && echo "$OUT" | grep -qi "minNewerHolds must be 1-10000"; } \
  && ok "T54a huge per-policy minNewerHolds rejected (no overflow), exit 2" \
  || bad "T54a (rc=$RC, out: $OUT)"

cat > "$WORK/cfg_huge_global.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": 700000000,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
OUT="$(ZFS_HOLD_TIGHT_CONFIG="$WORK/cfg_huge_global.json" ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" \
       dotnet run "$TARGET" -- --no-hc 2>&1)"; RC=$?
{ [ "$RC" = 2 ] && echo "$OUT" | grep -qi "minNewerHolds must be 1-10000"; } \
  && ok "T54b huge global minNewerHolds rejected (no overflow), exit 2" \
  || bad "T54b (rc=$RC, out: $OUT)"

echo "== $PASS passed, $FAIL failed =="
exit $([ "$FAIL" = 0 ] && echo 0 || echo 1)
