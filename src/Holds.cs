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

    // ---- reset the pool-wide health latch back to healthy -- the ONLY way it clears.
    //      Never bundled with --force (that means something else entirely: bypass the
    //      routine volume cap) -- an operator's habitual --force reflex must never double
    //      as "yes, I reviewed a possible mass-deletion/encryption event." A separate,
    //      explicitly-named command that does only this one thing. ----
    public static int ResetHealth(Ctx c, bool noHc)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode=reset-health ===");

        var (ok, health) = Zfs.LoadHealth(c);
        if (!ok) { Log("FAIL: zfs get failed, cannot verify current health state"); Zfs.FlushLog(c, lines); return 1; }
        if (health != "unhealthy")
        {
            string shown = health == "" ? "starting" : health;
            Log($"Nothing to reset: current state is {shown}, not unhealthy");
            Zfs.FlushLog(c, lines);
            if (!noHc) Zfs.HcPing(c, c.HcUrl + "/reset-health", $"no-op: state was {shown}");
            return 0;
        }

        // The one place this tool explains what it was worried about -- an operator running
        // --reset-health is, by definition, in the middle of reviewing the incident (see the
        // guarantee this command exists at all: only an explicit, reviewed reset clears the
        // latch), so show the persisted reason before it's cleared below.
        var (reasonOk, reasonBefore) = Zfs.LoadHealthReason(c);
        Log($"Reason for the latch: {(reasonOk && reasonBefore != "" ? reasonBefore : "not recorded")}");

        var (wok, msg) = Zfs.WriteHealth(c, "healthy", "reset via --reset-health");
        Log(msg);
        Zfs.FlushLog(c, lines);
        if (!noHc)
            Zfs.HcPing(c, wok ? c.HcUrl + "/reset-health" : c.HcUrl + "/fail", wok ? "ok: health reset to healthy" : "reset-health: write failed");
        return wok ? 0 : 1;
    }
}
