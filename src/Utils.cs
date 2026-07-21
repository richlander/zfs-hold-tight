using System.Globalization;
using System.Reflection;

// Small, pure helpers with no dependency on Ctx or any command's own state.
static class Utils
{
    public static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
    public static string Short(string snap) => snap[(snap.IndexOf('@') + 1)..];

    // InformationalVersion is "<Version>+<full-sha>" when built with -p:SourceRevisionId;
    // shorten the SHA to 7 chars (e.g. "1.0.0+ee4f11a"). Falls back to the assembly version.
    public static string GetVersion()
    {
        var asm = typeof(Snap).Assembly;
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(info)) return asm.GetName().Version?.ToString() ?? "0.0.0";
        int plus = info.IndexOf('+');
        return (plus > 0 && info.Length > plus + 8) ? info[..(plus + 8)] : info;
    }

    // Shared by the main per-dataset loop and --summary: weekly/monthly keepers (newest
    // snapshot per ISO-week / calendar-month group) and the resulting hold frontier — the
    // Nth-newest (N = minNewerHolds) keeper's date, below which a non-keeper daily is doomed.
    // A single implementation so a future change to this math (e.g. minNewerHolds semantics,
    // or how a keeper's own expiry date is derived) can't silently drift between what --prune
    // actually decides and what --summary reports — the caller-facing `Desired` delegate
    // returned below is the SAME function this method uses internally to compute the
    // frontier, not a second copy that has to be kept in sync by hand.
    //
    // Frontier candidates exclude a keeper whose OWN weekly/monthly retention has already
    // expired (`now > Desired(keeper)`). Without this exclusion, an expired keeper could
    // still count as one of the "N newer holds" that justifies dooming older plain dailies —
    // and since an expired keeper is ALSO independently doomed by its own retention, the SAME
    // run could destroy both the keeper and the dailies whose protection depended on it still
    // existing, retroactively invalidating the two-holds invariant within one run. Only
    // reachable when a keeper can expire within `minNewerHolds` keepers of the newest one
    // (short weekly/monthly retention relative to minNewerHolds) — not reachable with this
    // tool's shipped 5-year defaults, but nothing in config validation forbids a shorter one.
    public static (HashSet<string> weekKeeper, HashSet<string> monthKeeper, HashSet<string> keeperNames, DateTime holdFrontier, Func<Snap, DateTime> Desired)
        ComputeKeepersAndFrontier(List<Snap> snaps, Retention ret, DateTime now)
    {
        var weekKeeper  = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                               .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
        var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month)).Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
        var keeperNames = weekKeeper.Union(monthKeeper).ToHashSet();
        // Only keepers have a date-based expiry (their weekly/monthly grandfather retention);
        // a plain daily is governed by the frontier, not a date, so it returns MaxValue here
        // (never expires by date).
        DateTime Desired(Snap s)
        {
            bool isWeek = weekKeeper.Contains(s.Name), isMonth = monthKeeper.Contains(s.Name);
            if (!isWeek && !isMonth) return DateTime.MaxValue;
            var k = DateTime.MinValue;
            if (isWeek)  k = Later(k, s.Date.AddDays(ret.WeeklyKeepDays));
            if (isMonth) k = Later(k, s.Date.AddDays(ret.MonthlyKeepDays));
            return k;
        }
        var keeperDates = snaps.Where(s => keeperNames.Contains(s.Name) && now <= Desired(s))
                               .Select(s => s.Date).OrderByDescending(dt => dt).ToList();
        var holdFrontier = keeperDates.Count >= ret.MinNewerHolds ? keeperDates[ret.MinNewerHolds - 1] : DateTime.MinValue;
        return (weekKeeper, monthKeeper, keeperNames, holdFrontier, Desired);
    }
}
