using System.Globalization;
using System.Text.RegularExpressions;

static partial class Commands
{
    // ---- check holds exceeding retention ----
    public static int CheckHolds(Ctx c, bool noHc)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode=check-holds ===");

        // A held snapshot is "stale" once it is older than ITS dataset's longest retention
        // (max of weekly/monthly keep-days) — checked per dataset, since retention varies by
        // policy. A flat threshold would false-alarm on long policies and miss short ones.
        var dateRe = new Regex(@"@autosnap_(\d{4}-\d{2}-\d{2})");
        var stale = new List<string>();
        bool anyFail = false;
        foreach (var (ds, ret) in c.Retention)
        {
            var (code, snaps, listWarnings) = Zfs.ListDailies(c, ds);
            foreach (var w in listWarnings) Log(w);
            if (code != 0) { Log($"  WARNING: {ds}: zfs list failed"); anyFail = true; continue; }
            var holdMap = Zfs.LoadHolds(c, snaps.Select(s => s.Name).ToList());
            if (holdMap is null) { Log($"  WARNING: {ds}: zfs holds failed"); anyFail = true; continue; }
            foreach (var (snap, tags) in holdMap)
            {
                // Judge each hold against ITS tier's retention: a <ns>:weekly hold is stale past
                // weeklyKeepDays, a <ns>:monthly hold past monthlyKeepDays. Other holds (adhoc /
                // foreign) are judged against the longest retention, as before.
                int days = tags.Contains(c.TagMonthly) ? ret.MonthlyKeepDays
                         : tags.Contains(c.TagWeekly)  ? ret.WeeklyKeepDays
                         : Math.Max(ret.WeeklyKeepDays, ret.MonthlyKeepDays);
                var m = dateRe.Match(snap);
                if (!m.Success) continue;
                if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var snapDate))
                    continue;
                if (snapDate < c.Now.AddDays(-days))
                    stale.Add($"{ds}: hold on {Utils.Short(snap)} exceeds its {days}d retention");
            }
        }

        if (stale.Count == 0)
            Log(anyFail ? "no stale holds found (some datasets unchecked: zfs holds failed)" : "OK: no stale holds found");
        else
        {
            Log($"WARNING: {stale.Count} holds exceed their dataset retention:");
            foreach (var s in stale) Log($"  {s}");
        }

        bool trouble = stale.Count > 0 || anyFail;
        Zfs.FlushLog(c, lines);
        if (!noHc && trouble)
            Zfs.HcPing(c, c.HcUrl + "/fail", stale.Count > 0 ? "stale holds:\n" + string.Join("\n", stale) : "check-holds: a zfs holds call failed");
        return trouble ? 1 : 0;
    }

    // ---- on-demand hold ----
    public static int HoldAdhoc(Ctx c, string snap, bool noHc)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode=hold-adhoc ===");

        if (!snap.StartsWith(c.DatasetPrefix) || !snap.Contains('@'))
        { Log($"FAIL: snapshot '{snap}' must be under '{c.DatasetPrefix}' and name a snapshot (contain '@')"); Zfs.FlushLog(c, lines); return 2; }

        var (code, _, e) = Zfs.Exec(c, "zfs", "hold", c.TagAdhoc, snap);
        if (code != 0) { Log($"FAIL: {e.Trim()}"); Zfs.FlushLog(c, lines); return 1; }
        Log($"  HOLD:ADD {snap} ({c.TagAdhoc}, adhoc)");

        // Verify hold was created
        var (vc, vo, _) = Zfs.Exec(c, "zfs", "holds", snap);
        if (vc == 0 && vo.Contains(c.TagAdhoc))
            Log($"Verified: hold is active");
        else
            Log($"WARNING: hold may not be active (verification inconclusive)");

        Zfs.FlushLog(c, lines);
        if (!noHc)
            Zfs.HcPing(c, c.HcUrl + "/adhoc-hold", $"ok: held {snap}");
        return 0;
    }

    // ---- hold latest of tier ----
    public static int HoldLatestTier(Ctx c, string tier, string? datasetFilter, bool noHc)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode=hold-latest-tier tier={tier} ===");

        int errors = 0;
        foreach (var (ds, _) in c.Retention)
        {
            if (datasetFilter is not null && ds != datasetFilter) continue;
            var (code, snaps, listWarnings) = Zfs.ListDailies(c, ds);
            foreach (var w in listWarnings) Log(w);
            if (code != 0) { Log($"  ✗ {ds}: zfs list failed, skipping"); errors++; continue; }
            if (snaps.Count == 0) continue;

            // Find latest of requested tier
            Snap target;
            if (tier == "monthly")
                target = snaps.GroupBy(s => (s.Date.Year, s.Date.Month))
                             .Select(g => g.MaxBy(s => s.Date)!)
                             .MaxBy(s => s.Date)!;
            else if (tier == "weekly")
                target = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                             .Select(g => g.MaxBy(s => s.Date)!)
                             .MaxBy(s => s.Date)!;
            else // daily
                target = snaps.MaxBy(s => s.Date)!;

            var (code2, _, e) = Zfs.Exec(c, "zfs", "hold", c.TagAdhoc, target.Name);
            if (code2 == 0)
                Log($"  HOLD:ADD {target.Name} (adhoc, {tier})");
            else if (e.Contains("already") || e.Contains("exists"))
                Log($"  ✓ {ds}/{Utils.Short(target.Name)} ({tier}) - already held");
            else
            { Log($"  ✗ {ds}/{Utils.Short(target.Name)}: {e.Trim()}"); errors++; }
        }

        Zfs.FlushLog(c, lines);
        if (!noHc) Zfs.HcPing(c, errors > 0 ? c.HcUrl + "/fail" : c.HcUrl + "/hold-latest-" + tier,
                          errors > 0 ? $"hold-latest-{tier}: {errors} errors" : "ok");
        return errors > 0 ? 1 : 0;
    }

    // The supported recovery is an explicit assertion of a false positive.
    // Confirmed attacks remain latched and require separate recovery tools.
    public static int AcknowledgeFalsePositive(Ctx c, bool noHc)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        int Finish(int code)
        {
            Zfs.FlushLog(c, lines);
            // This is maintenance, never a successful-backup notification.
            if (code != 0 && !noHc) Zfs.HcPing(c, c.HcUrl + "/fail", "false-positive acknowledgment failed");
            return code;
        }
        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode=acknowledge-false-positive ===");
        var (ok, health) = Zfs.LoadHealth(c);
        if (!ok) { Log("FAIL: cannot verify latch state"); return Finish(1); }
        if (health != "unhealthy")
        {
            if (health is not ("" or "healthy")) { Log("FAIL: unknown health state"); return Finish(1); }
            Log("No active latch to acknowledge; no incident approved and no snapshots deleted");
            return Finish(0);
        }
        var (reasonOk, recordedReason) = Zfs.LoadHealthReason(c);
        if (!reasonOk) { Log("FAIL: cannot read incident reason"); return Finish(1); }
        string reason = recordedReason != "" ? recordedReason : "original latch reason not recorded";
        Log($"Operator asserts FALSE POSITIVE: {reason}");
        var id = Latched.Identity(c, Log);
        if (id is null) return Finish(1);
        var (evidenceOk, evidence) = Latched.ReadEvidence(c);
        if (evidence is not null && !c.Retention.ContainsKey(evidence.Dataset))
        { Log("FAIL: unresolved incident dataset is no longer configured; restore verification scope or use external recovery tools"); return Finish(1); }
        if (!evidenceOk || (evidence is not null && evidence.Id != id))
        { Log("FAIL: cannot bind acknowledgment to current warning evidence"); return Finish(1); }

        // Complete/repair partial incident writes while still frozen. A hold's
        // own ID supplies the linkage; older valid history cannot stand in for it.
        foreach (var (ds, ret) in c.Retention)
        {
            var (rc, snaps, warnings) = Zfs.ListDailies(c, ds);
            foreach (var warning in warnings) Log(warning);
            if (rc != 0) return Finish(1);
            var holds = Zfs.LoadHolds(c, snaps.Select(s => s.Name).ToList());
            if (holds is null) { Log($"FAIL: cannot verify {ds} holds"); return Finish(1); }
            var (estOk, established, recorded) = Latched.Established(c, ds, snaps);
            if (!estOk || (!recorded && !Latched.RecordEstablished(c, Log, ds, established))) return Finish(1);
            if (!Latched.Protect(c, Log, snaps, holds, id, reason)) return Finish(1);
            var (weekly, monthly, _, _, desired) = Utils.ComputeKeepersAndFrontier(snaps, ret, c.Now);
            if (!Latched.Reconcile(c, Log, snaps, monthly, weekly, desired, holds)) return Finish(1);
        }
        // Re-read every configured dataset after repair. New/unverified arrivals
        // or unknown legacy safety tags block acknowledgment; retry is safe.
        foreach (var (ds, _) in c.Retention)
        {
            var (rc, snaps, warnings) = Zfs.ListDailies(c, ds);
            foreach (var warning in warnings) Log(warning);
            if (rc != 0) return Finish(1);
            var holds = Zfs.LoadHolds(c, snaps.Select(s => s.Name).ToList());
            if (holds is null || !Latched.ValidateHistory(c, Log, snaps, holds)) return Finish(1);
            foreach (var s in snaps)
            {
                if (!holds.TryGetValue(s.Name, out var tags) || !tags.Contains(Latched.HoldTag(c, id)))
                { Log($"FAIL: {s.Name} arrived without verified current-incident protection; retry"); return Finish(1); }
                foreach (var tag in tags.Where(t => Latched.IsSafetyTag(c, t)))
                {
                    var priorId = Latched.HoldIncident(c, tag);
                    if (priorId is null || (priorId != id && !Latched.IsFalsePositive(c, priorId)))
                    { Log($"FAIL: {s.Name} has an unresolved or unverified incident hold {tag}"); return Finish(1); }
                }
            }
            // Suppress repeat detection only for the actual recorded warning,
            // never for unrelated or newly arrived tips on collateral datasets.
            if (evidence is not null && (evidence.Kind is "shrink" or "churn")
                && evidence.Dataset == ds && evidence.Snapshot is not null)
            {
                var accepted = snaps.FirstOrDefault(s => s.Name == evidence.Snapshot);
                if (accepted is null || accepted.Referenced != evidence.Referenced || accepted.Written != evidence.Written)
                { Log("FAIL: original warning snapshot/content evidence changed or is unavailable; explicit review required"); return Finish(1); }
                var (write, _, error) = Zfs.Exec(c, "zfs", "set", $"{c.PropAcceptedContent}={id}|{evidence.Snapshot}", ds);
                if (write != 0) { Log($"FAIL: cannot persist legitimate content baseline: {error.Trim()}"); return Finish(1); }
                Log($"  ACCEPT: exact recorded warning snapshot {evidence.Snapshot}");
            }
        }
        // Verification/infrastructure failures never approve data content. They
        // must actually be resolved before any receipt is published; real content
        // warnings are evaluated again after the narrow content acceptance below.
        foreach (var (ds, _) in c.Retention)
        {
            var (rc, snaps, warnings) = Zfs.ListDailies(c, ds);
            foreach (var warning in warnings) Log(warning);
            if (rc != 0) return Finish(1);
            if (snaps.Count == 0) continue;
            var warningNow = GuardChecks.ContentShockGuard(c, ds, snaps, c.GuardsByDataset[ds]);
            if (warningNow is not null && !Latched.IsContentWarning(warningNow))
            { Log($"FAIL: verification still incomplete: {ds}: {warningNow}"); return Finish(1); }
        }
        // The receipt alone must not deactivate a pending sentinel. Confirm the
        // durable unhealthy fallback BEFORE publishing approval, even when the
        // initial warning's compatibility health write never succeeded.
        var (fallbackOk, fallbackMessage) = Zfs.WriteHealth(c, "unhealthy", reason);
        Log(fallbackMessage);
        if (!fallbackOk) { Log("FAIL: cannot persist unhealthy fallback; no new approval receipt published"); return Finish(1); }
        var (ackOk, alreadyAcknowledged) = Latched.FalsePositiveState(c, id);
        if (!ackOk) { Log("FAIL: cannot verify acknowledgment record"); return Finish(1); }
        if (!alreadyAcknowledged)
        {
            var (rc, _, error) = Zfs.Exec(c, "zfs", "set",
                $"{c.FalsePositiveProperty(id)}={((DateTimeOffset)c.Now).ToUnixTimeSeconds()}", c.PoolDataset);
            if (rc != 0) { Log($"FAIL: cannot persist false-positive acknowledgment: {error.Trim()}"); return Finish(1); }
        }
        // Acceptance of the original warning must not silently approve a new
        // warning. Re-evaluate every dataset after the narrow receipt takes
        // effect, but BEFORE clearing health. Surface a new incident and keep
        // the pool frozen for its own explicit operator decision.
        foreach (var (ds, _) in c.Retention)
        {
            var (rc, snaps, warnings) = Zfs.ListDailies(c, ds);
            foreach (var warning in warnings) Log(warning);
            if (rc != 0) { Log("FAIL: cannot verify current content warnings"); return Finish(1); }
            if (snaps.Count == 0) continue;
            var shock = GuardChecks.ContentShockGuard(c, ds, snaps, c.GuardsByDataset[ds]);
            if (shock is null) continue;
            var next = Latched.BeginWarning(c, Log, ds, snaps.MaxBy(s => s.Date), $"{ds}: {shock}");
            Log(next is null ? "FAIL: original false positive recorded, but new warning could not be persisted; latch remains"
                : $"Original false positive {id} acknowledged; NEW WARNING {next.Id} remains latched: {next.Reason}. Review separately.");
            return Finish(1);
        }
        var (wok, message) = Zfs.WriteHealth(c, "healthy", null);
        Log(message);
        Log(wok ? $"False positive {id} acknowledged; incident history and safety holds retained; no snapshots deleted"
                : "FAIL: latch clear incomplete; protection retained, safe to retry");
        return Finish(wok ? 0 : 1);
    }
}
