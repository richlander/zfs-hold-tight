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

cat > "$WORK/config.json" <<EOF
{ "logDir": "$WORK/logs", "guardMaxDeletePerRun": 5, "guardRecentWindowDays": 7,
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
  && ok "T1 healthy dry-run HELD on circuit-breaker, no destroys" || bad "T1 (out: $(echo "$OUT"|tail -1))"

# ---- T2: prune --force -> keeper invariant: no week/month keeper destroyed ----
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
  && ok "T2 prune destroyed $(wc -l < "$WORK/destroyed"|tr -d ' '), no keeper destroyed" || bad "T2 viol=$viol destroyed=$(wc -l<"$WORK/destroyed")"
grep -qx "$(snapname "$ADATE")" "$WORK/destroyed" && bad "T2b newest/base destroyed!" || ok "T2b newest daily (base) preserved"
# tag-only-keepers: plain dailies must NOT be tagged; only ~70 keepers get lander:tier
sets=$(grep -c 'set lander:tier' "$OPS"); sets=${sets:-0}
{ [ "$sets" -gt 0 ] && [ "$sets" -lt 120 ]; } && ok "T2c tag-only-keepers ($sets tier-sets, not ~388)" || bad "T2c tier-sets=$sets"

# ---- T3a: newest 30h old (>26h) -> HOLD on freshness ----
gen_window $((NOW-108000)) > "$WORK/stale"
run_tool "$WORK/stale" --force
{ echo "$OUT" | grep -qi "stale"; } && ok "T3a stale newest (30h) -> HOLD" || bad "T3a (out: $(echo "$OUT"|tail -1))"

# ---- T3b: newest 10h old (<26h), complete window -> NOT held for freshness ----
gen_window $((NOW-36000)) > "$WORK/fresh"
run_tool "$WORK/fresh" --force
{ ! echo "$OUT" | grep -qi "HELD"; } && ok "T3b fresh newest (10h) proceeds, no HOLD" || bad "T3b held a fresh feed (out: $(echo "$OUT"|tail -1))"

# ---- T4: gap in last 7d -> HOLD ----
gap=$(date -u -d "$ADATE -3 days" +%Y-%m-%d)
grep -v "$gap" "$WORK/healthy" > "$WORK/gap"
run_tool "$WORK/gap" --force
{ echo "$OUT" | grep -qi "gap"; } && ok "T4 gap in window -> HOLD" || bad "T4 (out: $(echo "$OUT"|tail -1))"

# ---- T5: future-dated snapshot -> HOLD ----
cp "$WORK/healthy" "$WORK/future"
printf '%s\t-\t-\n' "$(snapname "$(date -u -d "$ADATE +2 days" +%Y-%m-%d)")" >> "$WORK/future"
run_tool "$WORK/future" --force
{ echo "$OUT" | grep -qi "future"; } && ok "T5 future clock -> HOLD" || bad "T5 (out: $(echo "$OUT"|tail -1))"

# ---- T6: malformed date must NOT crash (no SIGABRT=134) ----
cp "$WORK/healthy" "$WORK/bad"
printf 'archive/home/rich@autosnap_2026-13-45_%s_daily\t-\t-\n' "$TIME" >> "$WORK/bad"
run_tool "$WORK/bad" --force
[ "$RC" != 134 ] && ok "T6 malformed date no crash (rc=$RC)" || bad "T6 crashed (rc=$RC)"

# ---- T7: metadata (zfs set) failure -> HOLD, 0 destroys ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=set ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ echo "$OUT" | grep -qi "HELD" && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T7 metadata failure -> HOLD, 0 destroys" || bad "T7 (out: $(echo "$OUT"|tail -1))"

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
printf '%s\tlander:adhoc\t-\n' "$held" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
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
{ echo "$OUT" | grep -qi "shrank\|shrink\|deletion"; } && ok "T14 data shrinkage -> HOLD" || bad "T14 (out: $(echo "$OUT"|tail -1))"

# ---- T15: stable referenced (no shrink) -> shrink guard does NOT false-positive ----
gen_window $((NOW-7200)) | awk -F'\t' 'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824}' > "$WORK/stable"
run_tool "$WORK/stable" --force
{ ! echo "$OUT" | grep -qi "shrank\|shrink"; } && ok "T15 stable referenced: no false shrink-HOLD" || bad "T15 false positive (out: $(echo "$OUT"|tail -1))"

# ---- T16: churn spike (newest daily rewrites most of the dataset) -> HOLD ----
# 5-col fixture (..., referenced, written); referenced stable 1GiB, newest written 900MiB (~88% churn)
nlines="$(gen_window $((NOW-7200)) | wc -l)"
gen_window $((NOW-7200)) | awk -F'\t' -v n="$nlines" \
  'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824,(NR==n?943718400:10485760)}' > "$WORK/churn"
run_tool "$WORK/churn" --force
{ echo "$OUT" | grep -qi "churn\|rewrite\|encryption"; } && ok "T16 churn spike -> HOLD" || bad "T16 (out: $(echo "$OUT"|tail -1))"

# ---- T17: normal churn -> churn guard does NOT false-positive ----
gen_window $((NOW-7200)) | awk -F'\t' 'BEGIN{OFS="\t"} {print $1,$2,$3,1073741824,10485760}' > "$WORK/lowchurn"
run_tool "$WORK/lowchurn" --force
{ ! echo "$OUT" | grep -qi "churn"; } && ok "T17 normal churn: no false churn-HOLD" || bad "T17 false positive (out: $(echo "$OUT"|tail -1))"

# ---- T18: per-dataset isolation — a small (100MiB) dataset wiped 80% is flagged on its
#          own, while a healthy large dataset prunes normally (only the small one HELD) ----
cat > "$WORK/cfg2.json" <<EOF
{ "logDir": "$WORK/logs", "retentionPolicies": { "baseline": { "dailyKeepDays": 30, "weeklyKeepDays": 1825, "monthlyKeepDays": 1825 } },
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
{ echo "$OUT" | grep "archive/scratch:" | grep -qi "shrank\|HOLD" \
  && ! { echo "$OUT" | grep "archive/home/rich:" | grep -qi "HOLD"; } \
  && echo "$OUT" | grep -qi "1 HELD"; } \
  && ok "T18 per-dataset isolation: 100MiB dataset wiped 80% flagged alone" \
  || bad "T18 (out: $(echo "$OUT" | grep -E 'scratch|home/rich|HELD' | tr '\n' '|'))"

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

# ---- T22: `zfs holds` failure HOLDs the dataset (can't verify guard 12), 0 destroys ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"; cp "$WORK/healthy" "$WORK/fz/snaps"; : > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" FAKE_ZFS_FAIL=holds ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ echo "$OUT" | grep -qi "holds" && [ "$(grep -c '^destroy' "$WORK/fz/ops.log")" = 0 ]; } \
  && ok "T22 zfs holds failure -> HOLD, 0 destroys" || bad "T22 (out: $(echo "$OUT"|tail -1))"

# ---- T13: demoted monthly keeper -> hold released + tags inherited, not destroyed ----
rm -rf "$WORK/fz"; mkdir -p "$WORK/fz" "$WORK/logs"
recent=$(date -u -d "$ADATE -5 days" +%Y-%m-%d); demoted="$(snapname "$recent")"
# stamp it as a stale monthly (col3=monthly) with a lander:monthly hold, though it is NOT the month keeper
awk -F'\t' -v d="$demoted" 'BEGIN{OFS="\t"} $1==d{$3="monthly"} {print}' "$WORK/healthy" > "$WORK/fz/snaps"
printf '%s\tlander:monthly\t-\n' "$demoted" > "$WORK/fz/holds"; : > "$WORK/fz/ops.log"
OUT="$(FAKE_ZFS_DIR="$WORK/fz" ZFS_HOLD_TIGHT_CONFIG="$WORK/config.json" \
       ZFS_HOLD_TIGHT_LOG_DIR="$WORK/logs" dotnet run "$TARGET" -- --no-hc --prune --force 2>/dev/null)"
{ grep -qx "release lander:monthly $demoted" "$WORK/fz/ops.log" \
  && grep -qx "inherit lander:tier $demoted" "$WORK/fz/ops.log" \
  && ! grep -qx "destroy $demoted" "$WORK/fz/ops.log"; } \
  && ok "T13 demoted monthly: hold released + tags inherited, kept" \
  || bad "T13 (ops: $(grep "$demoted" "$WORK/fz/ops.log" | tr '\n' ';'))"

echo "== $PASS passed, $FAIL failed =="
exit $([ "$FAIL" = 0 ] && echo 0 || echo 1)
