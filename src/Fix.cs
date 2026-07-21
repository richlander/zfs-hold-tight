using System.Globalization;

// ---- fix: create missing managed keeper holds ----
static partial class Commands
{
    public static int Fix(Ctx c, string? datasetFilter)
    {
        var lines = new List<string>();
        void Log(string m) { Console.WriteLine(m); lines.Add(m); }
        Log($"=== zfs-hold-tight {c.Version} {c.Now:yyyy-MM-dd HH:mm:ss}Z  mode=fix ===");

        int fixed_ = 0, errors = 0;
        foreach (var (ds, _) in c.Retention)
        {
            if (datasetFilter is not null && ds != datasetFilter) continue;
            var (code, snaps, listWarnings) = Zfs.ListDailies(c, ds);
            foreach (var w in listWarnings) Log(w);
            if (code != 0) { Log($"  ✗ {ds}: zfs list failed, skipping"); errors++; continue; }
            if (snaps.Count == 0) continue;

            // Find keepers, load current holds once, and hold only the ones still MISSING their
            // matching tag (batched — `zfs hold <tag> <snap>...` takes multiple snapshots).
            var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month))
                                  .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
            var weekOnly = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                                .Select(g => g.MaxBy(s => s.Date)!.Name).Where(w => !monthKeeper.Contains(w)).ToHashSet();
            var holdMap = Zfs.LoadHolds(c, snaps.Select(s => s.Name).ToList());
            if (holdMap is null) { Log($"  ✗ {ds}: zfs holds failed, skipping"); errors++; continue; }

            // Hold every keeper in `keepers` that lacks `tag`, batched with a one-by-one retry.
            void HoldMissing(string tag, IEnumerable<string> keepers)
            {
                var toHold = keepers.Where(k => !(holdMap.TryGetValue(k, out var t) && t.Contains(tag))).ToList();
                foreach (var k in keepers.Except(toHold)) Log($"  ✓ {ds}/{Utils.Short(k)} (already held)");
                for (int i = 0; i < toHold.Count; i += 256)
                {
                    var chunk = toHold.GetRange(i, Math.Min(256, toHold.Count - i));
                    var a = new List<string> { "hold", tag }; a.AddRange(chunk);
                    var (code, _, _) = Zfs.Exec(c, "zfs", a.ToArray());
                    if (code == 0) { foreach (var k in chunk) { Log($"  HOLD:ADD {k} (fix)"); fixed_++; } continue; }
                    // Chunk failed — retry one-by-one so the report names the actual bad snapshot(s)
                    // (the fast batched path stays the common case).
                    foreach (var k in chunk)
                    {
                        var (cc, _, ee) = Zfs.Exec(c, "zfs", "hold", tag, k);
                        if (cc == 0 || ee.Contains("already") || ee.Contains("exists")) { Log($"  HOLD:ADD {k} (fix)"); fixed_++; }
                        else { Log($"  ✗ {ds}/{Utils.Short(k)}: {ee.Trim()}"); errors++; }
                    }
                }
            }
            HoldMissing(c.TagMonthly, monthKeeper);
            HoldMissing(c.TagWeekly, weekOnly);
        }

        Log($"Fixed {fixed_} holds" + (errors > 0 ? $", {errors} errors" : ""));
        Zfs.FlushLog(c, lines);
        return errors > 0 ? 1 : 0;
    }
}
