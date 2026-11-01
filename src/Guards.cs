using System.Globalization;

// Run-level guards (docs/SAFETY.md), evaluated independently per dataset; thresholds
// come from that dataset's own retention policy (Ctx.GuardsByDataset).
static class GuardChecks
{
    public static string? HealthGuard(Ctx c, Action<string> log, List<Snap> snaps, Retention ret, Guards gd, DateTime prunedThrough)
    {
        var newest = snaps.Max(s => s.Date);
        if (snaps.Any(s => s.Date > c.Now.AddMinutes(5)))                       // guard: clock
            return "snapshot dated in the future (clock?)";
        if (c.Now - newest > TimeSpan.FromHours(gd.MaxDailyAgeHours))           // guard: newest daily is fresh
            return $"newest daily is stale: {newest:yyyy-MM-dd HH:mm}Z is {(c.Now - newest).TotalHours:F0}h old (> {gd.MaxDailyAgeHours}h)";
        var days = snaps.Select(s => s.Date.Date).ToHashSet();               // guard: recent window
        var missing = new List<string>();
        for (int d = 0; d < gd.RecentWindowDays; d++)
        {
            var day = newest.Date.AddDays(-d);
            // A day at or before the persisted pruned_through watermark was already certified
            // healthy by a past successful, non-WORRY --prune -- a FACT about history, never
            // re-derived from today's retention policy. Deliberately NOT the hold frontier:
            // re-deriving "was this gap legitimate" from the CURRENT frontier breaks the instant
            // minNewerHolds or dailyKeepDays changes, because legitimacy of a past decision was
            // determined under whatever policy was in effect then, not today's (see
            // docs/DESIGN.md -- this is the fix for the guard/retention coupling bug class).
            if (day <= prunedThrough.Date) continue;
            if (!days.Contains(day)) missing.Add(day.ToString("MM-dd"));
        }
        if (missing.Count > 0)
        {
            string pt = prunedThrough == DateTime.MinValue ? "never" : prunedThrough.ToString("yyyy-MM-dd");
            return $"gap in last {gd.RecentWindowDays}d: missing {string.Join(",", missing)} (pruned_through={pt})";
        }

        string? shock = ContentShockGuard(c, snaps[0].Name.Split('@')[0], snaps, gd);
        if (shock is not null) return shock;

        // population note (informational): under the two-holds invariant nothing is droppable
        // until the derived destroy depth of held keepers exists (a keeper ≈ one per ISO-week/month).
        int keeperWeeks = snaps.Select(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date))).Distinct().Count();
        int destroyDepth = ret.MinNewerHolds;
        if (keeperWeeks < destroyDepth)
            log($"  note: only ~{keeperWeeks} held keeper(s) so far, need {destroyDepth} newer before any daily drops");
        return null;
    }

    // Guards 4-5 only: content-level "shock" (mass deletion / mass rewrite-encryption),
    // extracted from HealthGuard so the cross-dataset pre-pass (see the pool-wide
    // <ns>:health latch, docs/DESIGN.md "Cross-dataset shock") can check it for every
    // dataset BEFORE any dataset commits to destroying anything, without duplicating the
    // threshold logic. A dataset's own per-run HealthGuard call still goes through this
    // same function, so the two can never drift apart.
    public static string? ContentShockGuard(Ctx c, string ds, List<Snap> snaps, Guards gd)
    {
        var newest = snaps.Max(s => s.Date);
        var newestSnap = snaps.MaxBy(s => s.Date)!;
        var (acceptedOk, acceptedFrom, acceptedTip) = Latched.ContentBaseline(c, ds);
        if (!acceptedOk) return "cannot verify false-positive content baseline";
        if (acceptedFrom is not null && newest < acceptedFrom.Value)
            return "newest snapshot predates acknowledged false-positive baseline";
        // An operator accepted the exact tip, not arbitrary future content. New
        // snapshots use the post-acknowledgment reference-size population and
        // are still checked for fresh shrinkage/churn.
        if (newestSnap.Name == acceptedTip) return null;
        // baseline = recent dailies of THIS dataset (so a small dataset is judged on its own scale)
        var baseline = snaps.Where(s => s.Name != newestSnap.Name && s.Date >= newest.AddDays(-gd.RecentWindowDays)
                                && (acceptedFrom is null || s.Date >= acceptedFrom.Value))
                            .ToList();

        // guard: data shrinkage. Newest daily referencing far less than this dataset's recent
        // baseline ⇒ mass deletion. Catches deletion, NOT encryption (which preserves `referenced`).
        if (gd.MaxDailyShrinkPct > 0 && newestSnap.Referenced is long nref)
        {
            var refs = baseline.Where(s => s.Referenced is long rv && rv > 0).Select(s => s.Referenced!.Value).OrderBy(x => x).ToList();
            if (refs.Count > 0)
            {
                long median = refs[refs.Count / 2];
                if (nref < median * (100 - gd.MaxDailyShrinkPct) / 100.0)
                    return $"newest daily shrank {100.0 * (1 - (double)nref / median):F0}% ({nref / 1048576}MiB referenced vs ~{median / 1048576}MiB baseline, > {gd.MaxDailyShrinkPct}% drop) — possible mass deletion on source";
            }
        }

        // guard: churn. A single daily rewriting most of this dataset ⇒ mass rewrite / in-place
        // encryption. `written` = bytes changed since the prior snapshot. 0 disables.
        if (gd.MaxDailyChurnPct > 0 && newestSnap.Written is long nwr && newestSnap.Referenced is long nrf && nrf > 0)
        {
            if (nwr > nrf * gd.MaxDailyChurnPct / 100.0)
                return $"newest daily churned {100.0 * nwr / nrf:F0}% ({nwr / 1048576}MiB written vs {nrf / 1048576}MiB referenced, > {gd.MaxDailyChurnPct}%) — possible mass rewrite/encryption on source";
        }
        return null;
    }
}
