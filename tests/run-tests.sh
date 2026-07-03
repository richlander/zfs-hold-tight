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

# inv_config <holdDepth|-> <destroyMultiplier|-> : write an invariant-test config.
# Relaxes freshness + window guards so a mid-week INV_N can be "newest", and lifts the
# delete cap so the tiny doomed set is not circuit-broken. Defaults are holdDepth=1,
# destroyMultiplier=2, so the derived destroy depth is 2 newer held keepers.
inv_config() {
  local hd="$1" dm="$2" line=""
  [ "$hd" != "-" ] && line="$line \"holdDepth\": $hd,"
  [ "$dm" != "-" ] && line="$line \"destroyMultiplier\": $dm,"
  cat > "$WORK/cfg_inv.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", $line
  "guardMaxDeletePerRun": 50, "guardRecentWindowDays": 3, "guardMaxDailyAgeHours": 720,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
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

cat > "$WORK/config.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "guardMaxDeletePerRun": 5, "guardRecentWindowDays": 7,
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

echo "== Target: $TARGET =="
gen_feed 2025-06-01 > "$WORK/healthy"

# ---- T1: healthy feed, dry-run, big backlog -> circuit-breaker HOLD, 0 destroys ----
run_tool "$WORK/healthy"
{ echo "$OUT" | grep -q "cap" && [ "$(grep -c '^destroy' "$OPS")" = 0 ]; } \
  && ok "T1 healthy dry-run SKIP on circuit-breaker, no destroys" || bad "T1 (out: $(echo "$OUT"|tail -1))"

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

# ---- T3a: newest 30h old (>26h) -> HOLD on freshness ----
gen_window $((NOW-108000)) > "$WORK/stale"
run_tool "$WORK/stale" --force
{ echo "$OUT" | grep -qi "stale"; } && ok "T3a stale newest (30h) -> WORRY+SKIP" || bad "T3a (out: $(echo "$OUT"|tail -1))"

# ---- T3b: newest 10h old (<26h), complete window -> NOT held for freshness ----
gen_window $((NOW-36000)) > "$WORK/fresh"
run_tool "$WORK/fresh" --force
{ ! echo "$OUT" | grep -qi "SKIP"; } && ok "T3b fresh newest (10h) proceeds, no SKIP" || bad "T3b held a fresh feed (out: $(echo "$OUT"|tail -1))"

# ---- T4: gap in last 7d -> HOLD ----
gap=$(date -u -d "$ADATE -3 days" +%Y-%m-%d)
grep -v "$gap" "$WORK/healthy" > "$WORK/gap"
run_tool "$WORK/gap" --force
{ echo "$OUT" | grep -qi "gap"; } && ok "T4 gap in window -> WORRY+SKIP" || bad "T4 (out: $(echo "$OUT"|tail -1))"

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

# ---- T10: adhoc-held expired snapshot skipped, not destroy-failed (guard 12) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"
held="$(snapname 2025-06-15)"                      # old, not a keeper, expired
printf '%s\tzht:adhoc\t-\n' "$held" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ ! grep -qx "destroy $held" "$WORK/fz/ops.log"; } \
  && ok "T10 adhoc-held expired snap not destroyed (guard 12)" || bad "T10 held snap destroyed"

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

# ---- T18: per-dataset isolation — a small (100MiB) dataset wiped 80% is flagged on its
#          own, while a healthy large dataset prunes normally (only the small one SKIPped) ----
cat > "$WORK/cfg2.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
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
{ echo "$OUT" | grep "archive/scratch:" | grep -qi "shrank\|SKIP" \
  && ! { echo "$OUT" | grep "archive/home/rich:" | grep -qi "SKIP"; } \
  && echo "$OUT" | grep -qi "1 SKIPPED"; } \
  && ok "T18 per-dataset isolation: 100MiB dataset wiped 80% flagged alone" \
  || bad "T18 (out: $(echo "$OUT" | grep -E 'scratch|home/rich|SKIP' | tr '\n' '|'))"

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

# ---- T22: `zfs holds` failure SKIPs the dataset (can't verify guard 12), 0 destroys ----
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

# ---- T28: a capped --prune (no --force) HOLDs without mutating anything first (finding 1) ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune 2>/dev/null)"
muts=$(grep -cE '^(set|hold|inherit|release|destroy)' "$WORK/fz/ops.log"); muts=${muts:-0}
{ echo "$OUT" | grep -qi "cap" && [ "$muts" = 0 ]; } \
  && ok "T28 capped --prune SKIPs with zero metadata writes" || bad "T28 muts=$muts (out: $(echo "$OUT"|tail -1))"

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
echo "$OUT" | grep -qE 'monthly \+ [0-9]+ weekly held; daily tail: hold=[0-9]+, destroy>=[0-9]+ holds, to [0-9]{4}-[0-9]{2}-[0-9]{2}' \
  && ok "T31 summary line shows keeper held-counts + hold/destroy frontier" || bad "T31 (out: $(echo "$OUT"|grep -i tail || echo none))"

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
#   Held keepers newest-first: [N, N-3].  With holdDepth=1 and destroyMultiplier=2,
#   derived destroy depth = 2, frontier = N-3.
#   => N-4 has TWO newer held keepers (N, N-3) -> DROPPED.
#      N-2/N-1 have ONE newer held keeper (N)  -> KEPT.

# ---- T34: core invariant — exactly ONE newer held keeper keeps a daily, TWO drops it ----
inv_config - -                                 # default holdDepth=1, destroyMultiplier=2
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

# ---- T36: destroyMultiplier derives DESTROY from HOLD — higher multiplier deepens the tail ----
inv_config 1 3                                 # need THREE newer held keepers; only two exist
run_inv "$WORK/inv" --prune --force
drop3=$(grep -c '^destroy' "$OPS"); drop3=${drop3:-0}
inv_config 1 1                                 # ONE newer held keeper suffices
run_inv "$WORK/inv" --prune --force
{ [ "$drop3" = 0 ] && grep -qx "destroy $d4" "$OPS" \
  && grep -qx "destroy $d2" "$OPS" && grep -qx "destroy $d1" "$OPS"; } \
  && ok "T36 destroyMultiplier tunes the frontier (=3 keeps all dailies; =1 drops N-1/N-2/N-4)" \
  || bad "T36 (drop@3=$drop3, destroys@1: $(grep '^destroy' "$OPS" | tr '\n' ';'))"

# ---- T36b: legacy minNewerHolds remains a compatibility alias for derived destroy depth ----
cat > "$WORK/cfg_inv.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": 3,
  "guardMaxDeletePerRun": 50, "guardRecentWindowDays": 3, "guardMaxDailyAgeHours": 720,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
run_inv "$WORK/inv" --prune --force
drop_legacy=$(grep -c '^destroy' "$OPS"); drop_legacy=${drop_legacy:-0}
[ "$drop_legacy" = 0 ] \
  && ok "T36b legacy minNewerHolds=3 maps to destroy depth 3" \
  || bad "T36b (legacy destroys: $(grep '^destroy' "$OPS" | tr '\n' ';'))"


# ---- T36c: legacy minNewerHolds cannot be mixed with the new hold/destroy keys ----
cat > "$WORK/cfg_inv.json" <<EOF
{ "logDir": "$WORK/logs", "zfsPath": "$WORK/bin/zfs", "minNewerHolds": 2, "destroyMultiplier": 2,
  "guardMaxDeletePerRun": 50, "guardRecentWindowDays": 3, "guardMaxDailyAgeHours": 720,
  "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
  "datasets": { "archive/home/rich": "baseline" } }
EOF
run_inv "$WORK/inv" --prune --force
[ "$RC" = 2 ] \
  && ok "T36c mixing legacy minNewerHolds with destroyMultiplier is rejected" \
  || bad "T36c (rc=$RC, out: $(echo "$OUT" | tail -1))"

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

echo "== $PASS passed, $FAIL failed =="
exit $([ "$FAIL" = 0 ] && echo 0 || echo 1)
