// ---- summary: compact per-dataset markdown table (dataset health at a glance, for
//      pasting into a gist/PR/chat — not a replacement for --doctor's per-keeper detail
//      or the safety guards' own run-time decisions). ----
static partial class Commands
{
    public static int Summary(Ctx c, string? datasetFilter, DateTime? since)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        // Log-file-only: lands in the day's log (tail -40 .../logs/...) for full detail, but
        // never on Console — Console.WriteLine output is exactly what nightly.sh pipes
        // straight into the pushed summary file, so it must stay a strict, well-formed
        // markdown table.
        void LogFile(string m) { lines.Add(m); }

        // Two sections (docs/DESIGN.md "Cross-dataset shock"): keeper/snapshot status is its
        // own concern from pool health, so they get separate headings rather than a banner
        // spliced above one shared table. This is a human-read dashboard (pushed to a repo
        // and read directly), not something grepped like the console log -- so unlike the
        // log's WORRY convention, the Health section always states its line, healthy or not.
        Log("## Snapshots\n");
        Log("| Dataset | Monthlies | Weeklies | Dailies | Start | End | Continuity |");
        Log("|---|---|---|---|---|---|---|");

        int errors = 0;
        foreach (var (ds, ret) in c.Retention)
        {
            if (datasetFilter is not null && ds != datasetFilter) continue;
            // Warnings go to LogFile (day's log only), never Log/Console — Console.WriteLine
            // output is exactly what nightly.sh pipes straight into the pushed summary file,
            // so it must stay a strict, well-formed markdown table; a short note is appended
            // to the row below.
            var (code, snaps, warnings) = Zfs.ListDailies(c, ds);
            foreach (var w in warnings) LogFile(w);
            if (code != 0) { Log($"| {ds} | — | — | — | — | — | ERROR: zfs list failed (see log) |"); errors++; continue; }
            if (snaps.Count == 0) { Log($"| {ds} | 0 | 0 | 0 | — | — | no snapshots |"); continue; }

            var oldest = snaps.Min(s => s.Date).Date;
            var newest = snaps.Max(s => s.Date).Date;
            // --since narrows the report window; it can never claim data older than what's
            // actually retained (clamped to `oldest`, the default when --since is omitted),
            // and a --since after the newest snapshot is reported explicitly rather than
            // silently producing an empty, vacuously "✓" window.
            var effectiveSince = Utils.Later(since ?? oldest, oldest).Date;
            if (effectiveSince > newest)
            {
                Log($"| {ds} | 0 | 0 | 0 | {effectiveSince:yyyy-MM-dd} | {newest:yyyy-MM-dd} | --since is after the newest snapshot |");
                continue;
            }

            var (weekKeeper, monthKeeper, keeperNames, _, _) = Utils.ComputeKeepersAndFrontier(snaps, ret, c.Now);
            // Same bootstrap default as Commands.Prune (see its comment): an established
            // dataset predating this property shouldn't have its whole pre-existing daily
            // tail reported as a Continuity break just because the watermark is new.
            bool everEstablished = snaps.Any(s => s.Tier is not null);
            var prunedThrough = Zfs.LoadPrunedThrough(c, ds) ?? (everEstablished ? c.Now.Date : DateTime.MinValue);

            var windowed = snaps.Where(s => s.Date.Date >= effectiveSince).ToList();
            int monthlies = windowed.Count(s => monthKeeper.Contains(s.Name));
            int weeklies  = windowed.Count(s => weekKeeper.Contains(s.Name) && !monthKeeper.Contains(s.Name));
            int dailies   = windowed.Count - monthlies - weeklies;

            // Continuity: scan the DAILY TAIL from max(effectiveSince, prunedThrough) to newest —
            // the same persisted-watermark scope guard 1 uses (see docs/DESIGN.md), just extended
            // across the whole requested window instead of a fixed recent-window cap, so a day
            // this tool legitimately pruned is never reported as a break. Deliberately NOT the
            // hold frontier: that's re-derived from the CURRENT retention policy, so it would
            // flag already-legitimately-pruned history as a gap the instant minNewerHolds or
            // dailyKeepDays changes -- the same bug guard 1 had, in the same place. Older than the
            // watermark, only keeper cadence exists (by GFS design) — this report doesn't attempt
            // to detect a missing week/month there; that's a materially different check.
            //
            // No watermark yet (this dataset hasn't completed a --prune under a binary that
            // writes one) -- fall back to the same bounded window guard 1 itself uses
            // (guardRecentWindowDays), NOT the full effectiveSince: unlike guard 1's own scan,
            // which is always bounded to that window, effectiveSince here defaults to the
            // dataset's ENTIRE history, and scanning all of it with nothing excused would flag
            // every day GFS has ever legitimately thinned away as a "gap" on the very first run.
            // Self-resolving: the real watermark starts advancing after that first healthy prune.
            var scanStart = prunedThrough == DateTime.MinValue
                ? Utils.Later(effectiveSince, newest.AddDays(-c.GuardsByDataset[ds].RecentWindowDays))
                : Utils.Later(effectiveSince, prunedThrough.Date);
            var existingDays = snaps.Select(s => s.Date.Date).ToHashSet();
            var missing = new List<DateTime>();
            for (var d = scanStart; d <= newest; d = d.AddDays(1))
                if (!existingDays.Contains(d)) missing.Add(d);

            string continuity = missing.Count == 0 ? "✓" : FormatRanges(missing);
            // warnings here means "unparseable snapshot date" (code==0 path only — the
            // code!=0 path already `continue`d above) — noted compactly; full detail is in
            // the log file via the LogFile(w) loop above, not repeated in the table cell.
            if (warnings.Count > 0) continuity += $" ({warnings.Count} snapshot(s) skipped, see log)";
            Log($"| {ds} | {monthlies} | {weeklies} | {dailies} | {effectiveSince:yyyy-MM-dd} | {newest:yyyy-MM-dd} | {continuity} |");
        }

        // Second section: the pool-wide latch (docs/DESIGN.md "Cross-dataset shock"). Same
        // "{resource}: WORRY - {reason}" grammar as any per-dataset line -- but unlike the
        // console log's grep-WORRY convention, this always states its line, healthy or not:
        // this file is read directly by a human, not grepped, so an explicit "healthy" beats
        // making them infer it from an absence. When unhealthy, includes the persisted
        // reason (see Ctx.PropHealthReason) -- the one place this tool explains what it was
        // worried about without requiring a login to read the day's log.
        Log("\n## Health\n");
        var (healthOk, health) = Zfs.LoadHealth(c);
        if (!healthOk)
            Log($"{c.PoolDataset}: WORRY - cannot verify pool health (zfs get failed); treat as unhealthy until confirmed otherwise");
        else if (health == "unhealthy")
        {
            var (reasonOk, reason) = Zfs.LoadHealthReason(c);
            var reasonText = reasonOk && reason != "" ? reason : "no recorded reason";
            Log($"{c.PoolDataset}: WORRY - {reasonText} (run `--reset-health` after review)");
        }
        else
            Log($"{c.PoolDataset}: {(health == "" ? "starting" : "healthy")}");

        Zfs.FlushLog(c, lines);
        return errors > 0 ? 1 : 0;
    }

    // Consolidate sorted-or-not missing days into compact "date" / "date..date" runs.
    static string FormatRanges(List<DateTime> days)
    {
        days.Sort();
        var parts = new List<string>();
        int i = 0;
        while (i < days.Count)
        {
            int j = i;
            while (j + 1 < days.Count && (days[j + 1] - days[j]).Days == 1) j++;
            parts.Add(i == j ? days[i].ToString("yyyy-MM-dd") : $"{days[i]:yyyy-MM-dd}..{days[j]:yyyy-MM-dd}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }
}
