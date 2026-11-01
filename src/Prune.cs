// ---- prune: the tool's one destructive mode (dry-run when `prune` is false) ----
static partial class Commands
{
    public static int Prune(Ctx c, bool prune, bool force, bool verbose, string? only, bool noHc, bool reconcileOnly = false)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }

        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode={(reconcileOnly ? "RECONCILE" : prune ? "PRUNE" : "dry-run")}{(force ? " --force" : "")} ===");
        if (prune && !noHc) Zfs.HcPing(c, c.HcUrl + "/start", null);

        int totalKeep = 0, totalDoom = 0, destroyAttempted = 0, destroySucceeded = 0, destroyFails = 0;
        var held = new List<string>();   // datasets held by a guard
        var metadataErrors = new List<string>();  // metadata/hold failures

        // ---- PRE-PASS: pool-wide health check (docs/DESIGN.md "Cross-dataset shock"). Lists
        //      EVERY dataset once — ignoring --dataset, a filtered run must not blind the
        //      pool-wide latch to a shock on a dataset it isn't touching this run — and checks
        //      guards 4/5 (content shock: mass deletion / mass rewrite-encryption) on each. If
        //      ransomware is on the source machine, it plausibly hit every dataset, not just the
        //      one loud enough to trip its own threshold, so ANY single shock distrusts the WHOLE
        //      pool, not just that dataset. Cached here so the main loop below doesn't re-list.
        var dsCache = new Dictionary<string, (int code, List<Snap> snaps, List<string> warnings)>();
        string? shockReason = null, shockDataset = null;
        Snap? shockTip = null;
        foreach (var (dsP, retP) in c.Retention)
        {
            var (codeP, snapsP, warningsP) = Zfs.ListDailies(c, dsP);
            dsCache[dsP] = (codeP, snapsP, warningsP);
            if (shockReason is not null) continue;   // already found one; still cache the rest
            if (codeP != 0) { shockReason = $"{dsP}: zfs list failed, cannot verify"; shockDataset = dsP; continue; }
            if (snapsP.Count == 0) continue;
            var contentShock = GuardChecks.ContentShockGuard(c, dsP, snapsP, c.GuardsByDataset[dsP]);
            if (contentShock is not null)
            { shockReason = $"{dsP}: {contentShock}"; shockDataset = dsP; shockTip = snapsP.MaxBy(s => s.Date); }
        }

        // health: "" (never set, shown as "starting") | "healthy" | "unhealthy". A read FAILURE
        // fails toward frozen (globalFreeze = true) -- unlike pruned_through's read failure,
        // which fails toward the merely-cautious direction, not being able to verify whether the
        // pool is ALREADY latched unhealthy must not be silently treated as "fine" (same
        // reasoning a `zfs holds` failure already SKIPs a dataset elsewhere in this tool).
        var (healthOk, health) = Zfs.LoadHealth(c);
        bool globalFreeze; string freezeReason;
        bool warningEvidenceFailed = false;
        if (!healthOk)
        {
            globalFreeze = true;
            freezeReason = "cannot verify pool health (zfs get failed) — failing toward frozen";
        }
        else if (health == "unhealthy")
        {
            globalFreeze = true;
            freezeReason = "already latched unhealthy — run --acknowledge-false-positive only for a confirmed false positive; attacks require external recovery";
        }
        else if (shockReason is not null)
        {
            globalFreeze = true;
            freezeReason = $"content shock detected — {shockReason}";
            if (prune) warningEvidenceFailed = Latched.BeginWarning(c, Log, shockDataset!, shockTip, shockReason) is null;
        }
        else
        {
            globalFreeze = false; freezeReason = "";
            // First-ever clean pass: "starting" -> "healthy". Never the reverse direction of
            // "unhealthy" -> anything -- that's --acknowledge-false-positive's job alone (see AcknowledgeFalsePositive).
            if (prune && health == "") { var (_, whMsg) = Zfs.WriteHealth(c, "healthy", null); Log(whMsg); }
        }
        // Reuses the tool's OWN existing WORRY grammar ({resource}: WORRY - {reason}) instead of
        // inventing a separate concept -- the pool is just another resource that can WORRY,
        // exactly like a dataset. This means the documented `grep -E 'WORRY|ERROR|FAILED|SKIP'`
        // "did anything go wrong?" workflow catches it automatically, with no separate marker to
        // know about. Silent when healthy, matching how a healthy dataset's own line never says
        // WORRY either.
        if (globalFreeze)
            Log($"{c.PoolDataset}: WORRY - {freezeReason}");

        string? incidentId = null;
        string incidentReason = freezeReason;
        if (prune && globalFreeze && !warningEvidenceFailed)
        {
            // Do not create an apparently reviewed incident when the health
            // state could not be read/persisted. Existing guard protection stays.
            var (latchedOk, latchedState) = Zfs.LoadHealth(c);
            if (latchedOk && latchedState == "unhealthy")
            {
                incidentId = Latched.Identity(c, Log);
                var (reasonOk, reason) = Zfs.LoadHealthReason(c);
                incidentReason = reasonOk && reason != "" ? reason : "original latch reason not recorded";
            }
        }

        foreach (var (ds, ret) in c.Retention)
        {
            if (only is not null && ds != only) continue;
            var (listCode, snaps, listWarnings) = dsCache[ds];
            foreach (var w in listWarnings) Log(w);
            if (listCode != 0)
            {
                // The ERROR detail was just logged above; emit the dataset verdict.
                Log($"{ds}: SKIP - zfs list failed (cannot verify feed health)");
                held.Add($"{ds}: zfs list error");
                continue;
            }
            if (snaps.Count == 0) { Log($"{ds}: no daily snapshots, skip"); continue; }
            var gd = c.GuardsByDataset[ds];   // this dataset's effective guard thresholds

            // ---- keepers + desired keep_until ----
            var newest = snaps.MaxBy(s => s.Date)!.Name;
            // keepers = weekKeeper ∪ monthKeeper: exactly the snapshots that carry a real hold
            // (weekly with <ns>:weekly, monthly with <ns>:monthly). A daily is held by none of them.
            // Two-holds invariant: a non-keeper daily is droppable only once at least minNewerHolds
            // held keepers are strictly NEWER than it. Equivalent clean cutoff: sort keeper dates DESC;
            // holdFrontier = the Nth-newest keeper's date; a non-keeper daily s is doomed iff
            // s.Date < holdFrontier. Fewer than N keepers ⇒ nothing droppable yet (frontier = MinValue).
            // The daily's own week+month grandfathers are strictly newer, so the default of 3 subsumes
            // the old week/month coverage test plus a buffer. A held keeper only ever advances into the
            // 2nd-newest slot at an ISO-week close, so with minNewerHolds=2 a whole week's worth of plain
            // dailies sit at "exactly 1 newer hold" simultaneously and cross to "doomed" together the
            // instant the next week's keeper appears — a one-run cliff, not a smooth trickle. minNewerHolds
            // additionally requires the daily to be older than dailyKeepDays (the calendar floor applied
            // below) before it's actually eligible, which is what keeps that cliff from ever reaching all
            // the way down to "0 dailies" — see docs/SAFETY.md "Detection vs. recovery".
            int destroyDepth = ret.MinNewerHolds;
            // Desired (a keeper's date-based expiry) comes from ComputeKeepersAndFrontier itself
            // now — the exact function it already uses internally to decide the frontier, not a
            // second hand-maintained copy that could silently drift from it.
            var (weekKeeper, monthKeeper, keeperNames, holdFrontier, Desired) = Utils.ComputeKeepersAndFrontier(snaps, ret, c.Now);

            // Current holds on this dataset — used to (a) honor guard 14 (never delete a held
            // snapshot) and (b) tell our managed <ns>:monthly hold from foreign/defensive ones.
            // A holds-list failure means we can't verify guard 14, so SKIP rather than guess.
            var holdMap = Zfs.LoadHolds(c, snaps.Select(s => s.Name).ToList());
            if (holdMap is null)
            {
                Log($"{ds}: ERROR - zfs holds failed (cannot verify holds)");
                Log($"{ds}: SKIP - cannot verify holds (no deletions)");
                held.Add($"{ds}: zfs holds error");
                continue;
            }
            bool IsForeignHeld(string name) =>
                holdMap.TryGetValue(name, out var tags) && tags.Any(t => t != c.TagMonthly && t != c.TagWeekly
                    && !snaps.Any(s => s.Name == name && Latched.VerifiedHold(c, s, t)));

            // Persisted watermark for guard 1 (see Ctx.PropPrunedThrough / Zfs.LoadPrunedThrough).
            // Unreadable always defaults to MinValue (the safe/conservative direction -- can't
            // verify, so trust nothing). Never-set is more nuanced: for a dataset this tool has
            // never established (no real <ns>:tier anywhere -- same signal the cold-start floor
            // below already uses), MinValue is still correct -- there's no history to trust yet.
            // But for a dataset that's ALREADY established and simply predates this property
            // (the first run after upgrading to a binary that tracks it), defaulting to MinValue
            // would flag its entire pre-existing, already-legitimate daily tail as an unexplained
            // gap -- a real incident, not hypothetical: this is exactly what happened deploying
            // this feature. Bootstrap those to `now` instead: a one-time, self-limiting default
            // (once a real run persists a value, this branch never applies again) that only ever
            // extends trust to a dataset this tool was already successfully managing, never to a
            // genuinely fresh one. Applied identically in dry-run and --prune so a dry-run's
            // guard-1 preview isn't needlessly alarming either; only --prune actually persists it.
            var (establishmentOk, everEstablished, establishmentRecorded) = Latched.Established(c, ds, snaps);
            if (prune && establishmentOk && !establishmentRecorded)
                establishmentOk = Latched.RecordEstablished(c, Log, ds, everEstablished);
            var prunedThrough = Zfs.LoadPrunedThrough(c, ds) ?? (everEstablished ? c.Now.Date : DateTime.MinValue);

            // Keeper/hold summary — hoisted above the worry paths so the WORRY lines and the normal
            // line all report the same numbers. keepersHeld counts computed keepers that ALREADY carry
            // a real <ns>:weekly / <ns>:monthly hold on disk; on a dry-run before the first prune the
            // weekly holds are still pending, so keepersHeld < keeperNames.Count — that gap is itself
            // the signal that a real run still has holds to place.
            int mCount = monthKeeper.Count;
            int wCount = weekKeeper.Count(w => !monthKeeper.Contains(w));
            int keepersHeld = keeperNames.Count(k =>
                holdMap.TryGetValue(k, out var kt) && (kt.Contains(c.TagMonthly) || kt.Contains(c.TagWeekly)));
            string tail = holdFrontier == DateTime.MinValue ? "—" : holdFrontier.ToString("yyyy-MM-dd");
            // Shared by every WORRY line below so they can never drift from each other.
            string keeperTailSuffix = $"{keepersHeld}/{keeperNames.Count} keepers held, daily tail to {tail} (rerun with --force if intended)";

            // Proactively hold every computed candidate keeper (monthly with <ns>:monthly, weekly
            // with <ns>:weekly) — shared by every "protection only ever increases" WORRY path.
            // Never releases or deletes; only ever adds.
            void HoldCandidateKeepersDefensively(string reasonLabel)
            {
                // Batched (like HoldMissing in --fix mode): a cold-start dataset can carry a
                // year+ of un-held keepers, and one zfs subprocess per keeper doesn't scale to that.
                void HoldMissingKeepers(string tag, IEnumerable<string> keepers)
                {
                    var toHold = keepers.Where(k => !(holdMap.TryGetValue(k, out var t) && t.Contains(tag))).ToList();
                    for (int i = 0; i < toHold.Count; i += 256)
                    {
                        var chunk = toHold.GetRange(i, Math.Min(256, toHold.Count - i));
                        var a = new List<string> { "hold", tag }; a.AddRange(chunk);
                        var (code, _, _) = Zfs.Exec(c, "zfs", a.ToArray());
                        if (code == 0) { foreach (var k in chunk) Log($"  HOLD:ADD {k} (protected during {reasonLabel})"); continue; }
                        // Chunk failed — retry one-by-one so the report names the actual bad snapshot(s)
                        // (the fast batched path stays the common case).
                        foreach (var k in chunk)
                        {
                            var (cc, _, ee) = Zfs.Exec(c, "zfs", "hold", tag, k);
                            // "already"/"exists" here means the failed batch call DID apply this one before
                            // erroring out on another target — it's a real hold placed just now, not a stale
                            // no-op, so it gets the same success log as HoldMissing's --fix retry does.
                            if (cc == 0 || ee.Contains("already") || ee.Contains("exists")) Log($"  HOLD:ADD {k} (protected during {reasonLabel})");
                            else
                                Log($"    HOLD:ADD {Utils.Short(k)} FAILED: {ee.Trim()}");
                        }
                    }
                }
                HoldMissingKeepers(c.TagMonthly, monthKeeper);
                HoldMissingKeepers(c.TagWeekly, weekKeeper.Where(w => !monthKeeper.Contains(w)));
            }

            // ---- run-level guards. On a worry, protection only ever INCREASES: still hold the
            //      current keepers (even the suspect newest). A verified pool latch now protects
            //      every daily with an independent incident hold before reconciling classification;
            //      other guard failures retain the add-only keeper path. Never delete on either. Combined
            //      with the pool-wide health latch (docs/DESIGN.md "Cross-dataset shock"): EITHER
            //      this dataset's own guard tripping OR the pool latch alone is enough to
            //      withhold it, and a dataset that tripped its OWN guard still gets its specific
            //      reason rather than just the generic pool message — only a dataset that's
            //      frozen purely as collateral (its own guards are clean) falls back to that.
            //      Either way, no destroy and no pruned_through advance while either is true
            //      (see FrozenWhileUnhealthyStep in docs/model/ZfsHoldTight.tla). ----
            bool incidentHistoryOk = Latched.ValidateHistory(c, Log, snaps, holdMap);
            bool acknowledgmentOk = globalFreeze || Latched.ValidateAcknowledgments(c, Log, holdMap);
            string? ownGuard = !establishmentOk ? "cannot verify/persist establishment state"
                : !incidentHistoryOk ? "unverified incident protection/history"
                : !acknowledgmentOk ? "incident not acknowledged as a false positive"
                : GuardChecks.HealthGuard(c, Log, snaps, ret, gd, prunedThrough);
            string? guard = ownGuard ?? (globalFreeze ? $"pool: {freezeReason}" : null);
            if (guard is not null)
            {
                Log($"{ds}: WORRY - {guard} ({keepersHeld}/{keeperNames.Count} keepers held)");
                if (prune)
                {
                    if (globalFreeze && establishmentOk && incidentId is not null)
                    {
                        if (!Latched.Protect(c, Log, snaps, holdMap, incidentId, incidentReason)
                            || !Latched.Reconcile(c, Log, snaps, monthKeeper, weekKeeper, Desired, holdMap))
                            metadataErrors.Add($"  {ds}: latched protection/metadata reconciliation failed");
                    }
                    else
                        HoldCandidateKeepersDefensively("worry");
                    if (ownGuard is not null)
                    {
                        var (wc, _, we) = Zfs.Exec(c, "zfs", "set", $"{c.Ns}:worry={c.Now:yyyy-MM-dd} {ownGuard}", newest);
                        if (wc == 0) Log($"  META:ADD {newest} {c.Ns}:worry={c.Now:yyyy-MM-dd} {ownGuard}");
                        else Log($"    META:ADD {Utils.Short(newest)} FAILED: {we.Trim()}");
                    }
                }
                Log($"{ds}: SKIP - guard tripped (keepers protected, no deletions)");
                held.Add($"{ds}: {guard}");
                continue;
            }

            // Guards 1-5 all passed: certify today as accounted-for, independent of whatever the
            // cold-start floor / volume cap / metadata writes below decide -- this is purely "was
            // the feed healthy," not "did we destroy anything" (see docs/DESIGN.md). Only in --prune
            // mode; a dry-run must never mutate state. A write failure here is logged but doesn't
            // SKIP the dataset -- worst case, guard 1 stays exactly as cautious as it is today.
            if (prune && !reconcileOnly)
            {
                var (pwc, _, pwe) = Zfs.Exec(c, "zfs", "set", $"{c.PropPrunedThrough}={((DateTimeOffset)c.Now).ToUnixTimeSeconds()}", ds);
                if (pwc != 0) Log($"    META:UPDATE {ds} {c.PropPrunedThrough} FAILED: {pwe.Trim()}");
                else Log($"  META:UPDATE {ds} {c.PropPrunedThrough}={c.Now:yyyy-MM-dd}");
            }

            // ---- PLAN (read-only): compute the doomed set; mutate nothing yet ----
            var doomed = new List<string>();
            foreach (var s in snaps.OrderBy(s => s.Date))
            {
                if (s.Name == newest) continue;                     // never the base (guard 9)
                if (IsForeignHeld(s.Name))                          // guard 14: held => never delete, skip cleanly
                { if (verbose) Log($"  {ds}: skip {Utils.Short(s.Name)} - held ({string.Join(",", holdMap[s.Name])})"); continue; }
                if (keeperNames.Contains(s.Name))
                {
                    // A held week/month keeper ages out only past its own grandfather retention
                    // (weekly/monthly keep-days); until then it is kept, invariant or not.
                    if (c.Now <= Desired(s)) continue;
                }
                else
                {
                    // Two-holds invariant: keep a non-keeper daily until the derived destroy depth
                    // of held keepers is strictly newer than it (its date drops below the frontier).
                    // The frontier subsumes the old week+month coverage test (its own grandfathers are
                    // strictly newer) plus a buffer, so no coverage hole can open early.
                    if (s.Date >= holdFrontier) continue;
                    // Calendar floor: even once covered, a plain daily survives until it is itself
                    // at least dailyKeepDays old. Coverage alone only guarantees a daily outlives
                    // SOMETHING newer — it says nothing about how much daily-granularity history
                    // exists at any given moment, which without this floor can swing from ~2 weeks
                    // down to a single day the instant an ISO-week closes (an entire week's plain
                    // dailies go from "1 newer hold, all kept" to "2 newer holds, all doomed" in the
                    // same run — see docs/SAFETY.md "Detection vs. recovery"). This floor bounds that
                    // swing to a guaranteed minimum, independent of hold cadence.
                    if (c.Now <= s.Date.AddDays(ret.DailyKeepDays)) continue;
                }
                doomed.Add(s.Name);
            }

            // ---- cold-start floor: the FIRST time this tool ever destroys anything on a dataset
            //      must be a reviewed, conscious decision — guardMaxDeletePerRun (14) is sized for
            //      a ROUTINE weekly batch on a dataset this tool already manages, not for "is it OK
            //      to destroy anything at all here for the first time." So this floor is not a
            //      smaller cap, it is a flat requirement: ANY doomed snapshot on a
            //      never-established dataset needs --force, regardless of count (see
            //      docs/SAFETY.md guard 6 "Accepted tradeoff" and guard 7). Explicit establishment
            //      is recorded before new metadata, so continued latched/metadata-only tagging
            //      cannot silently clear this floor. Legacy tier metadata supplies the one-time
            //      upgrade baseline. Ordinary blocked cold starts still protect keepers only;
            //      --reconcile can maintain classification without approving a first prune.
            //      A dataset with zero doomed snapshots (nothing
            //      destructive to decide on yet) establishes on its first plain --prune — Promote
            //      only ever tags/holds keepers, never destroys, so there is nothing to review.
            //      (everEstablished computed above, alongside prunedThrough's own bootstrap.)
            if (!everEstablished && doomed.Count > 0 && !force && !reconcileOnly)
            {
                Log($"{ds}: WORRY - cold start: would delete {doomed.Count} (dataset not yet established — no completed healthy prune; if --force was already used and this persists, Promote's metadata/hold writes may be failing rather than never having run); {keeperTailSuffix}");
                if (prune) HoldCandidateKeepersDefensively("cold start");
                Log($"{ds}: SKIP - cold-start floor (no deletions)");
                held.Add($"{ds}: cold start, needs --force");
                continue;
            }

            // ---- volume circuit-breaker: caps DESTROYS only. Keeper hold-tagging and demotion
            //      cleanup below still run on a capped dataset — "on a worry, protection only
            //      ever increases" (same principle as the HealthGuard branch above). Gating Promote
            //      behind this guard would leave newly-due weekly/monthly keepers unheld for as long
            //      as the dataset stays capped: a protection regression masquerading as a safety win.
            bool volumeCapped = doomed.Count > gd.MaxDeletePerRun && !force && !reconcileOnly;
            if (volumeCapped)
                Log($"{ds}: WORRY - would delete {doomed.Count} > cap {gd.MaxDeletePerRun}; {keeperTailSuffix}");

            // ---- tag keepers / clean up demotions (mutating modes only) ----
            if (prune)
            {
                if (!Latched.Reconcile(c, Log, snaps, monthKeeper, weekKeeper, Desired, holdMap))
                {
                    Log($"{ds}: SKIP - metadata/hold write failed (no deletions)");
                    held.Add($"{ds}: metadata/hold failures"); metadataErrors.Add(ds);
                    continue;
                }
            }

            if (volumeCapped)
            {
                Log($"{ds}: SKIP - volume circuit-breaker (no deletions)");
                held.Add($"{ds}: volume {doomed.Count}>cap"); continue;
            }

            // Only a healthy, established/approved, under-cap run can retire
            // incident holds. History remains even when all protection is released.
            if (prune && !reconcileOnly && !Latched.ReleaseAcknowledged(c, Log, snaps, holdMap))
            {
                Log($"{ds}: SKIP - latch hold release failed (no deletions)");
                held.Add($"{ds}: latch reconciliation failed"); metadataErrors.Add(ds); continue;
            }
            int priorDestroyFails = destroyFails;
            int kept = snaps.Count - doomed.Count;
            totalKeep += kept; totalDoom += doomed.Count;
            string verb = prune && !reconcileOnly ? "destroy" : "would destroy";
            string rng = doomed.Count > 0 ? $" [{Utils.Short(doomed[0])} .. {Utils.Short(doomed[^1])}]" : "";
            // keeper tag breakdown (mCount/wCount/tail hoisted above the worry paths) and the daily-tail
            // frontier: dailies newer than it survive (< destroyDepth held keepers ahead).
            Log($"{ds}: {snaps.Count} snaps -> keep {kept} ({mCount} monthly + {wCount} weekly held; daily tail: >={destroyDepth} newer holds, to {tail}), {verb} {doomed.Count}{rng}");
            foreach (var name in doomed)
            {
                if (!prune || reconcileOnly) { if (verbose) Log($"  WOULD-DESTROY {name}"); continue; }

                // Release ANY of our managed holds (<ns>:monthly and/or <ns>:weekly) before destroy,
                // but ONLY for snapshots that actually carry one (an expired keeper — the tag Promote
                // gave it this run — or one held at load time) — not the common plain-daily case.
                // (Guard 14 already excluded foreign-held snapshots.)
                var toRelease = new HashSet<string>();
                if (monthKeeper.Contains(name)) toRelease.Add(c.TagMonthly);           // Promote holds monthly (precedence)
                else if (weekKeeper.Contains(name)) toRelease.Add(c.TagWeekly);        // weekly-only keeper holds weekly
                if (holdMap.TryGetValue(name, out var ht))
                {
                    if (ht.Contains(c.TagMonthly)) toRelease.Add(c.TagMonthly);
                    if (ht.Contains(c.TagWeekly))  toRelease.Add(c.TagWeekly);
                }
                foreach (var tag in toRelease)
                {
                    var (rc, _, re) = Zfs.Exec(c, "zfs", "release", tag, name);
                    if (rc != 0 && !Zfs.IsMissingHold(re))
                        Log($"    HOLD:REMOVE {Utils.Short(name)} FAILED: {re.Trim()}");
                    else Log($"  HOLD:REMOVE {name} (expired keeper, before destroy)");
                }

                destroyAttempted++;
                var (code, _, err) = Zfs.Exec(c, "zfs", "destroy", name);
                if (code != 0) { destroyFails++; Log($"    SNAP:DESTROY {Utils.Short(name)} FAILED: {err.Trim()}"); }
                else { destroySucceeded++; Log($"  SNAP:DESTROY {name}"); }
            }
            if (prune && !reconcileOnly && !everEstablished && priorDestroyFails == destroyFails
                && !Latched.RecordEstablished(c, Log, ds, true))
                metadataErrors.Add($"{ds}: establishment completion write failed");
        }

        bool trouble = held.Count > 0 || metadataErrors.Count > 0 || (prune && destroyFails > 0);
        Log($"\n{(reconcileOnly ? "RECONCILED (no deletions)" : prune ? "PRUNED" : "DRY RUN")}: kept {totalKeep}, {(prune && !reconcileOnly ? $"destroyed {destroySucceeded}/{destroyAttempted}" : $"would destroy {totalDoom}")}"
            + (held.Count > 0 ? $", {held.Count} SKIPPED" : "")
            + (metadataErrors.Count > 0 ? $", {metadataErrors.Count} metadata errors" : "")
            + (prune && destroyFails > 0 ? $", {destroyFails} destroy-failures" : ""));
        if (metadataErrors.Count > 0) Log("Metadata errors:\n" + string.Join("\n", metadataErrors));
        Zfs.FlushLog(c, lines);
        if (prune && !noHc)
            Zfs.HcPing(c, trouble ? c.HcUrl + "/fail" : c.HcUrl!,   // !noHc guarantees HcUrl is set
                   trouble ? "zfs-hold-tight trouble:\n" + string.Join("\n", held) + (metadataErrors.Count > 0 ? "\nMetadata errors:\n" + string.Join("\n", metadataErrors) : "") + (destroyFails > 0 ? $"\n{destroyFails} destroy failures" : "")
                           : $"ok: kept {totalKeep}, destroyed {destroySucceeded}");
        return trouble ? 1 : 0;
    }
}
