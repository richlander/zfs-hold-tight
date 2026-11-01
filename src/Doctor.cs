using System.Globalization;

// ---- doctor: validate hold strategy ----
static partial class Commands
{
    public static int Doctor(Ctx c, string? datasetFilter)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode=doctor ===");

        // Pool-wide health latch (docs/DESIGN.md "Cross-dataset shock") -- surfaced first so
        // it's the first thing an operator running --doctor sees. Same WORRY grammar as
        // everything else in this tool, so silent here too when healthy/starting -- there's
        // nothing to worry about, and staying silent keeps a clean --doctor run matching the
        // documented "grep for WORRY, empty means fine" habit.
        var (healthOk, health) = Zfs.LoadHealth(c);
        if (!healthOk)
            Log($"{c.PoolDataset}: WORRY - cannot verify pool health (zfs get failed); treat as unhealthy until confirmed otherwise");
        else if (health == "unhealthy")
        {
            var (reasonOk, reason) = Zfs.LoadHealthReason(c);
            var reasonText = reasonOk && reason != "" ? reason : "no recorded reason";
            Log($"{c.PoolDataset}: WORRY - {reasonText} (run --acknowledge-false-positive only for a confirmed false positive; attacks require external recovery)");
        }

        int missing = 0, protected_ = 0, adhoc_ = 0, orphaned = 0, errors = 0;
        int incidentHeld = 0, incidentHistory = 0, staleManaged = 0, staleMetadata = 0;
        foreach (var (ds, ret) in c.Retention)
        {
            if (datasetFilter is not null && ds != datasetFilter) continue;
            var (code, snaps, listWarnings) = Zfs.ListDailies(c, ds);
            foreach (var w in listWarnings) Log(w);
            if (code != 0) { Log($"{ds}: ERROR - zfs list failed, cannot validate holds"); errors++; continue; }
            if (snaps.Count == 0) { Log($"{ds}: no snapshots"); continue; }

            // Find keepers: monthlies (<ns>:monthly) and weekly-only keepers (<ns>:weekly).
            var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month))
                                  .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
            var weekKeeper = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                                  .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
            var weekOnly = weekKeeper.Where(w => !monthKeeper.Contains(w)).ToHashSet();
            var (_, _, _, _, desiredExpiry) = Utils.ComputeKeepersAndFrontier(snaps, ret, c.Now);

            // Check which snapshots have holds
            var holdMap = Zfs.LoadHolds(c, snaps.Select(s => s.Name).ToList());
            if (holdMap is null) { Log($"{ds}: ERROR - zfs holds failed, hold status unknown"); errors++; continue; }

            // Report monthly + weekly keeper status (each expects its matching hold)
            Log($"{ds}: {monthKeeper.Count} monthlies, {weekOnly.Count} weeklies");
            foreach (var monthly in monthKeeper.OrderByDescending(s => s))
            {
                if (holdMap.TryGetValue(monthly, out var tags) && tags.Contains(c.TagMonthly))
                {
                    Log($"  ✓ {Utils.Short(monthly)} ({c.TagMonthly})");
                    protected_++;
                }
                else
                {
                    Log($"  ✗ {Utils.Short(monthly)} MISSING HOLD");
                    missing++;
                }
            }
            foreach (var weekly in weekOnly.OrderByDescending(s => s))
            {
                if (holdMap.TryGetValue(weekly, out var tags) && tags.Contains(c.TagWeekly))
                {
                    Log($"  ✓ {Utils.Short(weekly)} ({c.TagWeekly})");
                    protected_++;
                }
                else
                {
                    Log($"  ✗ {Utils.Short(weekly)} MISSING HOLD");
                    missing++;
                }
            }

            // Report actual protection separately from calculated classification.
            // Legacy managed holds are ambiguous, never silently relabelled as
            // incident history that the old version did not actually record.
            foreach (var s in snaps)
            {
                holdMap.TryGetValue(s.Name, out var actualTags);
                actualTags ??= new();
                bool hasHistory = Latched.HasHistory(s);
                if (s.LatchHistory is not null && !hasHistory)
                { Log($"  ? {Utils.Short(s.Name)} INVALID INCIDENT HISTORY"); errors++; }
                if (hasHistory) incidentHistory++;
                var safetyTags = actualTags.Where(t => Latched.IsSafetyTag(c, t)).ToList();
                if (safetyTags.Count > 0) incidentHeld++;
                foreach (var tag in safetyTags)
                {
                    bool verified = Latched.VerifiedHold(c, s, tag);
                    Log($"  ℹ {Utils.Short(s.Name)} ({tag}; {(verified ? "matching incident history recorded" : "UNVERIFIED incident protection")})");
                    if (!verified) errors++;
                }
                string? expected = monthKeeper.Contains(s.Name) ? "monthly" : weekOnly.Contains(s.Name) ? "weekly" : null;
                long? expectedKeep = expected is null ? null : ((DateTimeOffset)desiredExpiry(s)).ToUnixTimeSeconds();
                if (s.Tier != expected || s.KeepUntil != expectedKeep)
                {
                    staleMetadata++;
                    Log($"  ? {Utils.Short(s.Name)} STALE METADATA: recorded {s.Tier ?? "daily"}/{s.KeepUntil?.ToString() ?? "-"}, calculated {expected ?? "daily"}/{expectedKeep?.ToString() ?? "-"}");
                }
                if (expected is null && actualTags.Any(t => t == c.TagMonthly || t == c.TagWeekly))
                {
                    staleManaged++;
                    Log($"  ? {Utils.Short(s.Name)} STALE MANAGED HOLD: calculated daily; {(hasHistory ? "incident history recorded" : "legacy provenance unknown")}");
                }
            }

            // Report other holds — a keeper carrying its own <ns>:weekly/<ns>:monthly hold is
            // expected, so exclude both keeper sets here; only genuinely unexpected holds remain.
            foreach (var (snap, tags) in holdMap)
            {
                if (!monthKeeper.Contains(snap) && !weekOnly.Contains(snap) && snap.Contains("_daily"))
                {
                    if (tags.Contains(c.TagAdhoc))
                    {
                        Log($"  ℹ {Utils.Short(snap)} ({c.TagAdhoc} - defensive)");
                        adhoc_++;
                    }
                    else if (!tags.Any(t => t.StartsWith($"{c.Ns}:")))
                    {
                        Log($"  ? {Utils.Short(snap)} has foreign holds: {string.Join(",", tags)}");
                        orphaned++;
                    }
                }
            }
        }

        Log($"\nSummary: {protected_} keepers protected, {missing} missing, {adhoc_} adhoc, {orphaned} unexpected"
            + (errors > 0 ? $", {errors} datasets unverified (zfs errors)" : ""));
        Log($"Incident protection: {incidentHeld} latch-held, {incidentHistory} with history; {staleManaged} stale managed holds, {staleMetadata} stale tier records");
        if (missing == 0 && orphaned == 0 && errors == 0 && staleManaged == 0 && staleMetadata == 0)
            Log("✓ Hold strategy validated");
        else
            Log($"⚠ Issues: {missing} missing holds, {orphaned} unexpected, {errors} unverified");

        Zfs.FlushLog(c, lines);
        return (missing > 0 || errors > 0 || staleManaged > 0 || staleMetadata > 0) ? 1 : 0;
    }
}
