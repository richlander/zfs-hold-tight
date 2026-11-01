using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

// zfs/curl process invocation, snapshot listing, and the small set of dataset-level
// properties this tool persists (pruned_through, health, health_reason). Every function
// here is read-only or a single, narrowly-scoped write -- the actual destroy/hold/release
// decisions live in Commands.Prune, which calls these as building blocks.
static class Zfs
{
    static readonly Regex SnapRe = new(@"@autosnap_(\d{4}-\d{2}-\d{2}_\d{2}:\d{2}:\d{2})_daily$");

    public static (int code, string outp, string err) Exec(Ctx c, string file, params string[] a)
    {
        if (file == "zfs") file = c.ZfsBin;            // absolute trusted path, not ambient PATH
        else if (file == "curl") file = c.CurlBin;
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var x in a) psi.ArgumentList.Add(x);
        using var p = Process.Start(psi)!;
        // Drain both pipes concurrently — reading one fully before the other can deadlock if the
        // child fills the unread pipe's buffer.
        var oT = p.StandardOutput.ReadToEndAsync();
        var eT = p.StandardError.ReadToEndAsync();
        string o = oT.GetAwaiter().GetResult(), e = eT.GetAwaiter().GetResult();
        p.WaitForExit();
        return (p.ExitCode, o, e);
    }

    // ListDailies never logs anything itself — it only RETURNS warnings/errors in `warnings`.
    // Every caller loops over `warnings` and logs them through its OWN local Log(), so each
    // message lands in the log file that caller's own FlushLog(lines) actually persists.
    // (An earlier version had ListDailies call a shared top-level Log() directly: since
    // ListDailies is a top-level local function, that Log() lexically resolves to the
    // top-level `lines` list, not whichever function's `lines` is calling it right now — so
    // the message printed to Console fine, but silently never reached the log FILE for any
    // caller other than the one function whose Log() happens to BE that top-level one.)
    public static (int, List<Snap>, List<string> warnings) ListDailies(Ctx c, string ds)
    {
        var warnings = new List<string>();
        // -p: exact bytes for referenced/written (the shrinkage + churn guards need raw numbers).
        var (code, outp, err) = Exec(c, "zfs", "list", "-Hp", "-t", "snapshot", "-o", $"name,{c.PropKeep},{c.PropTier},referenced,written,{c.PropLatchHistory}", "-d", "1", ds);
        var list = new List<Snap>();
        if (code != 0) { warnings.Add($"ERROR: zfs list {ds} failed: {err.Trim()}"); return (code, list, warnings); }
        foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = line.Split('\t');
            var m = SnapRe.Match(cols[0]);
            if (!m.Success) continue;
            // TryParse, not Parse: a regex-shaped but out-of-range date (e.g. ..._2026-13-45_...)
            // must skip that one snapshot, never abort the whole run.
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            { warnings.Add($"  WARNING: unparseable snapshot date, skipping: {cols[0]}"); continue; }
            long? ku = cols.Length > 1 && long.TryParse(cols[1], out var v) ? v : null;
            string? tier = cols.Length > 2 && cols[2] != "-" ? cols[2] : null;
            long? refb = cols.Length > 3 && long.TryParse(cols[3], out var rb) ? rb : null;
            long? wrb = cols.Length > 4 && long.TryParse(cols[4], out var wb) ? wb : null;
            list.Add(new Snap(cols[0], date, ku, tier, refb, wrb, cols.Length > 5 && cols[5] != "-" ? cols[5] : null));
        }
        return (0, list, warnings);
    }

    // Reads the dataset-level pruned_through watermark (see Ctx.PropPrunedThrough).
    // Never-set and read-failure both return null (caller defaults to DateTime.MinValue,
    // i.e. nothing excused) -- the safe direction: worst case guard 1 is more likely to
    // flag a real gap, never silently excuses one it shouldn't just because this read
    // had a hiccup.
    public static DateTime? LoadPrunedThrough(Ctx c, string ds)
    {
        var (code, outp, _) = Exec(c, "zfs", "get", "-Hp", "-o", "value", c.PropPrunedThrough, ds);
        if (code != 0) return null;
        var v = outp.Trim();
        return long.TryParse(v, out var secs) ? DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime : (DateTime?)null;
    }

    // Reads the pool-wide <ns>:health latch (see Ctx.PropHealth). Returns (false, _) on
    // a read FAILURE -- unlike LoadPrunedThrough, this fails toward the CAUTIOUS direction:
    // if we can't even verify whether the pool is already latched unhealthy, we must not
    // silently assume it's fine (same reasoning `zfs holds` failures already SKIP a dataset
    // elsewhere in this tool). Returns (true, "") for never-set (reported as "starting"),
    // otherwise (true, "healthy") or (true, "unhealthy").
    public static (bool ok, string value) LoadHealth(Ctx c)
    {
        var (evidenceOk, evidence) = Latched.ReadEvidence(c);
        if (!evidenceOk) return (false, "");
        if (evidence is not null)
        {
            var (ackOk, acknowledged) = Latched.FalsePositiveState(c, evidence.Id);
            if (!ackOk) return (false, "");
            if (!acknowledged) return (true, "unhealthy");
        }
        var (code, outp, _) = Exec(c, "zfs", "get", "-H", "-o", "value", c.PropHealth, c.PoolDataset);
        if (code != 0) return (false, "");
        var v = outp.Trim();
        return v is "-" or "healthy" or "unhealthy" ? (true, v == "-" ? "" : v) : (false, "");
    }

    // Reads the persisted WHY behind the current/most-recent unhealthy latch (see
    // Ctx.PropHealthReason). Returns (true, "") if never set (nothing to report) or
    // (false, _) on a read failure -- callers fall back to a generic message either way,
    // so this is advisory detail, not something worth blocking on the way pool health itself
    // does.
    public static (bool ok, string reason) LoadHealthReason(Ctx c)
    {
        var (evidenceOk, evidence) = Latched.ReadEvidence(c);
        if (!evidenceOk) return (false, "");
        if (evidence is not null) return (true, evidence.Reason);
        var (code, outp, _) = Exec(c, "zfs", "get", "-H", "-o", "value", c.PropHealthReason, c.PoolDataset);
        if (code != 0) return (false, "");
        var v = outp.Trim();
        return (true, v == "-" ? "" : v);
    }

    // Writes the pool-wide health latch. Does NOT log itself -- unlike most write helpers
    // in this file, this one is called both from Commands.Prune (whose Log() it could
    // safely close over) AND from the standalone --acknowledge-false-positive command (which has its
    // OWN separate `lines`/Log()); a shared Log() call here would only ever reach ONE of
    // those callers' log files (the exact bug class ListDailies's own comment already
    // documents once). Caller logs the returned message with its own Log().
    //
    // `reason` is used for the returned log message either way, but is only PERSISTED to
    // PropHealthReason when going unhealthy -- when going healthy it's inherited away
    // instead (best-effort; a failure here doesn't fail the health write itself, since a
    // stale reason next to a healthy latch is a cosmetic wart, not a safety concern), so a
    // resolved incident's explanation never lingers as if it still applied.
    public static (bool ok, string message) WriteHealth(Ctx c, string value, string? reason)
    {
        // Identity is assigned on entry to a new unacknowledged incident,
        // independently of this health write. Acknowledgment persists before
        // clearing health, so retries/partial health writes never invent approval.
        var args = new List<string> { "set", $"{c.PropHealth}={value}" };
        args.Add(c.PoolDataset);
        var (code, _, e) = Exec(c, "zfs", args.ToArray());
        if (code != 0) return (false, $"    META:UPDATE {c.PoolDataset} {c.PropHealth} FAILED: {e.Trim()}");
        if (value == "unhealthy" && reason is not null)
            Exec(c, "zfs", "set", $"{c.PropHealthReason}={reason}", c.PoolDataset);
        else if (value == "healthy")
            Exec(c, "zfs", "inherit", c.PropHealthReason, c.PoolDataset);
        return (true, $"  META:UPDATE {c.PoolDataset} {c.PropHealth}={value}" + (reason is null ? "" : $" ({reason})"));
    }

    public static bool IsMissingHold(string error) =>
        error.Contains("no such hold") || error.Contains("no such tag") || error.Contains("not found");

    // Annotate one snapshot for its current tier. Returns false if any write failed.
    // Only keepers (weekly/monthly) carry <ns>:tier + <ns>:keep_until tags; a plain
    // daily is implicitly daily and stays untagged. This keeps the first run cheap (~24
    // writes, not ~280) and is self-correcting: a snapshot that was a keeper last run but
    // is now superseded (demotion) has its tags inherited away and its monthly hold released
    // — without that release a demoted monthly's hold would later block its own destroy.
    public static bool Promote(Ctx c, Action<string> log, Snap s, DateTime keepUntil, string tier, HashSet<string> heldKeeperTags)
    {
        bool success = true;
        if (tier == "daily")
        {
            // Demotion: clear the audit tags first, and only once that's known-good release ALL of
            // our protective holds (weekly and/or monthly, whichever it carries). If an inherit
            // fails the snapshot KEEPS its holds (stays protected) and the run SKIPs the dataset;
            // the cleanup retries next run.
            if (s.Tier != null)
            {
                var (code, _, e) = Exec(c, "zfs", "inherit", c.PropTier, s.Name);
                if (code != 0) { log($"    META:REMOVE {Utils.Short(s.Name)} FAILED: {e.Trim()}"); success = false; }
                else log($"  META:REMOVE {s.Name} (demoted to daily)");
            }
            if (s.KeepUntil != null)
            {
                var (code, _, e) = Exec(c, "zfs", "inherit", c.PropKeep, s.Name);
                if (code != 0) { log($"    META:REMOVE {Utils.Short(s.Name)} keep_until FAILED: {e.Trim()}"); success = false; }
            }
            if (success)                          // demoted keeper: drop the now-unneeded hold(s) last
                foreach (var tag in heldKeeperTags)
                {
                    var (rc, _, re) = Exec(c, "zfs", "release", tag, s.Name);
                    if (rc != 0 && !IsMissingHold(re))
                    { log($"    HOLD:REMOVE {Utils.Short(s.Name)} FAILED: {re.Trim()}"); success = false; }
                    else log($"  HOLD:REMOVE {s.Name} (demoted to daily)");
                }
            return success;
        }

        // Set keep_until and/or tier in a single `zfs set` (it accepts multiple prop=value args).
        long want = ((DateTimeOffset)keepUntil).ToUnixTimeSeconds();
        var props = new List<string>();
        if (s.KeepUntil != want) props.Add($"{c.PropKeep}={want}");
        if (s.Tier != tier)      props.Add($"{c.PropTier}={tier}");   // weekly|monthly — highest qualifying tier
        if (props.Count > 0)
        {
            // ADD when the snapshot carried no tags before; UPDATE when an existing keeper's
            // tier/keep_until changed (e.g. a weekly survivor promoted to monthly). The prior
            // values come free from the batched `zfs list`, so the distinction costs no extra I/O.
            string metaVerb = s.Tier is null && s.KeepUntil is null ? "ADD" : "UPDATE";
            var setArgs = new List<string> { "set" }; setArgs.AddRange(props); setArgs.Add(s.Name);
            var (code, _, e) = Exec(c, "zfs", setArgs.ToArray());
            if (code != 0) { log($"    META:{metaVerb} {Utils.Short(s.Name)} FAILED: {e.Trim()}"); success = false; }
            else log($"  META:{metaVerb} {s.Name} ({tier}): {string.Join(" ", props)}");
        }
        // Hold invariant: held ⇔ keeper, with the tag matching the tier (monthly→<ns>:monthly,
        // weekly→<ns>:weekly). ADD the wanted tag first (if missing), and only once it is confirmed
        // present RELEASE any wrong-tier tag — add-then-release, so a snapshot is never transiently
        // unheld across a tier change (e.g. weekly→monthly).
        string wantTag  = tier == "monthly" ? c.TagMonthly : c.TagWeekly;
        string otherTag = tier == "monthly" ? c.TagWeekly  : c.TagMonthly;
        bool wantHeld = heldKeeperTags.Contains(wantTag);
        if (!wantHeld)
        {
            var (code, _, e) = Exec(c, "zfs", "hold", wantTag, s.Name);
            if (code == 0)
                { log($"  HOLD:ADD {s.Name} ({tier} keeper, prune ~{keepUntil:yyyy-MM-dd})"); wantHeld = true; }
            else if (e.Contains("already") || e.Contains("exists"))
                wantHeld = true;   // idempotent: hold already present (race)
            else
                { log($"    HOLD:ADD {Utils.Short(s.Name)} FAILED: {e.Trim()}"); success = false; }
        }
        if (wantHeld && heldKeeperTags.Contains(otherTag))   // wrong-tier hold left over → release it
        {
            var (rc, _, re) = Exec(c, "zfs", "release", otherTag, s.Name);
            if (rc != 0 && !IsMissingHold(re))
                { log($"    HOLD:REMOVE {Utils.Short(s.Name)} FAILED: {re.Trim()}"); success = false; }
            else
                log($"  HOLD:REMOVE {s.Name} ({(tier == "monthly" ? "weekly→monthly" : "monthly→weekly")})");
        }
        return success;
    }

    // snapshot -> set of hold tags. `zfs holds` takes SNAPSHOT args (not a dataset; `-r` is
    // for descending from a snapshot), so we pass the dataset's snapshot names, batched in
    // chunks to stay under argv limits — one or a few commands per dataset, not one per snap.
    // Returns null on any `zfs holds` failure (distinct from "no holds") so a destructive
    // caller can refuse to proceed without knowing the hold state.
    public static Dictionary<string, HashSet<string>>? LoadHolds(Ctx c, IReadOnlyList<string> snapNames)
    {
        var map = new Dictionary<string, HashSet<string>>();
        const int chunk = 256;
        for (int i = 0; i < snapNames.Count; i += chunk)
        {
            var a = new List<string> { "holds", "-H" };
            for (int j = i; j < Math.Min(i + chunk, snapNames.Count); j++) a.Add(snapNames[j]);
            var (code, outp, _) = Exec(c, "zfs", a.ToArray());
            if (code != 0) return null;
            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cols = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);   // -H: NAME\tTAG\tTIMESTAMP
                if (cols.Length < 2) continue;
                if (!map.TryGetValue(cols[0], out var set)) { set = new(); map[cols[0]] = set; }
                set.Add(cols[1]);
            }
        }
        return map;
    }

    public static void HcPing(Ctx c, string url, string? body)
    {
        var a = new List<string> { "-fsS", "--retry", "2", "-m", "10" };
        if (body is not null) { a.Add("--data-raw"); a.Add(body); }
        a.Add(url);
        try { Exec(c, "curl", a.ToArray()); } catch { /* best effort */ }
    }

    public static void FlushLog(Ctx c, List<string> logLines)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(c.LogPath)!);
            // Create the day's log group-writable so the other principal (operator vs. cron) can
            // append to it too; harmless if it already exists.
            if (!File.Exists(c.LogPath))
            {
                var newOpts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) newOpts.UnixCreateMode = c.SharedDataMode;
                try { using (new FileStream(c.LogPath, newOpts)) { } }
                catch (IOException) { /* another run created it first — fine */ }
            }
            File.AppendAllText(c.LogPath, string.Join("\n", logLines) + "\n");
        }
        catch { /* best effort - e.g. a non-root run whose log dir isn't shared can't write */ }
    }
}
