using System.Text;
using System.Text.Json;

// Retention classification and incident protection have independent lifetimes.
// A latch hold protects every affected snapshot while tier/expiry keep moving.
// Incident history is append-only and is not removed by reset or reconciliation.
static class Latched
{
    public record Incident(string Id, string Reason, string ObservedUtc);

    public record Evidence(string Id, string Dataset, string? Snapshot, string Reason, long? Referenced, long? Written, string Kind = "verification");

    // Unacknowledged evidence is a durable pending-latch sentinel. It is stored
    // before compatibility health/id mirrors, closing the interruption window
    // between detecting a new warning and assigning that warning its identity.
    public static (bool ok, Evidence? evidence) ReadEvidence(Ctx c)
    {
        var (rc, text, _) = Zfs.Exec(c, "zfs", "get", "-H", "-o", "value", c.PropLatchEvidence, c.PoolDataset);
        if (rc != 0) return (false, null);
        if (text.Trim() == "-") return (true, null);
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString();
            var ds = root.GetProperty("dataset").GetString();
            var snap = root.GetProperty("snapshot").GetString();
            var reason = root.GetProperty("reason").GetString();
            var referenced = root.GetProperty("referenced"); var written = root.GetProperty("written");
            string kind = root.TryGetProperty("kind", out var kindValue) ? kindValue.GetString() ?? "verification" : "verification";
            if (kind is not ("shrink" or "churn" or "verification")) return (false, null);
            if (kind != "verification" && (ds is null || reason is null || !reason.StartsWith(ds + ": newest daily " + (kind == "shrink" ? "shrank " : "churned "), StringComparison.Ordinal)))
                return (false, null);
            if (!Guid.TryParseExact(id, "N", out _) || ds is null || !ds.StartsWith(c.DatasetPrefix, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(reason)
                || (snap is not null && (!snap.StartsWith(ds + "@autosnap_", StringComparison.Ordinal)
                                         || !snap.EndsWith("_daily", StringComparison.Ordinal)))) return (false, null);
            return (true, new(id!, ds, snap, reason!, referenced.ValueKind == JsonValueKind.Null ? null : referenced.GetInt64(),
                written.ValueKind == JsonValueKind.Null ? null : written.GetInt64(), kind));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return (false, null); }
    }

    public static Evidence? BeginWarning(Ctx c, Action<string> log, string ds, Snap? tip, string reason)
    {
        string kind = reason.StartsWith(ds + ": newest daily shrank ", StringComparison.Ordinal) ? "shrink"
                    : reason.StartsWith(ds + ": newest daily churned ", StringComparison.Ordinal) ? "churn" : "verification";
        var evidence = new Evidence(Guid.NewGuid().ToString("N"), ds, tip?.Name, reason, tip?.Referenced, tip?.Written, kind);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("id", evidence.Id); writer.WriteString("dataset", ds);
            writer.WriteString("snapshot", evidence.Snapshot); writer.WriteString("reason", reason); writer.WriteString("kind", kind);
            if (evidence.Referenced is long r) writer.WriteNumber("referenced", r); else writer.WriteNull("referenced");
            if (evidence.Written is long w) writer.WriteNumber("written", w); else writer.WriteNull("written");
            writer.WriteEndObject();
        }
        var (rc, _, error) = Zfs.Exec(c, "zfs", "set", $"{c.PropLatchEvidence}={Encoding.UTF8.GetString(stream.ToArray())}", c.PoolDataset);
        if (rc != 0) { log($"ERROR: cannot persist new warning evidence: {error.Trim()}"); return null; }
        log($"  WARN:LATCH incident {evidence.Id}: {reason}; snapshot {tip?.Name ?? "unavailable"}");
        Mirror(c, log, evidence);
        return evidence;
    }

    static void Mirror(Ctx c, Action<string> log, Evidence evidence)
    {
        var (rc, _, error) = Zfs.Exec(c, "zfs", "set", $"{c.PropLatchId}={evidence.Id}", c.PoolDataset);
        if (rc != 0) log($"WARNING: latch identity mirror incomplete: {error.Trim()}");
        var (ok, message) = Zfs.WriteHealth(c, "unhealthy", evidence.Reason);
        if (!ok) log("WARNING: health mirror incomplete; pending warning evidence remains authoritative");
        log(message);
    }

    public static bool ReadHistory(string? value, out List<Incident> history)
    {
        history = new();
        if (value is null) return true;
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return false;
            var seen = new HashSet<string>();
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                var reason = entry.GetProperty("reason").GetString();
                var observed = entry.GetProperty("observed_utc").GetString();
                if (!Guid.TryParseExact(id, "N", out _) || !seen.Add(id!) || string.IsNullOrWhiteSpace(reason)
                    || !DateTimeOffset.TryParse(observed, out _)) return false;
                history.Add(new(id!, reason!, observed!));
            }
            return history.Count > 0;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { return false; }
    }

    public static bool IsContentWarning(string warning) =>
        warning.StartsWith("newest daily shrank ", StringComparison.Ordinal)
        || warning.StartsWith("newest daily churned ", StringComparison.Ordinal);

    public static bool HasHistory(Snap s) => ReadHistory(s.LatchHistory, out var history) && history.Count > 0;

    public static string HoldTag(Ctx c, string id) => $"{c.TagLatched}:{id}";

    public static bool IsSafetyTag(Ctx c, string tag) =>
        tag == c.TagLatched || tag.StartsWith(c.TagLatched + ":", StringComparison.Ordinal);

    public static string? HoldIncident(Ctx c, string tag)
    {
        if (!tag.StartsWith(c.TagLatched + ":", StringComparison.Ordinal)) return null;
        var id = tag[(c.TagLatched.Length + 1)..];
        return Guid.TryParseExact(id, "N", out _) ? id : null;
    }

    public static bool VerifiedHold(Ctx c, Snap s, string tag) =>
        HoldIncident(c, tag) is string id && ReadHistory(s.LatchHistory, out var history)
        && history.Any(i => i.Id == id);

    public static bool ValidateHistory(Ctx c, Action<string> log, List<Snap> snaps,
                                       Dictionary<string, HashSet<string>> holds)
    {
        bool ok = true;
        foreach (var s in snaps)
        {
            // Validate retained history even when there is no physical safety
            // hold. Corrupt audit evidence must not disappear through pruning.
            if (!ReadHistory(s.LatchHistory, out _))
            { log($"{s.Name}: ERROR - invalid incident history; no reconciliation or deletion"); ok = false; }
            if (!holds.TryGetValue(s.Name, out var tags)) continue;
            foreach (var tag in tags.Where(t => IsSafetyTag(c, t)))
                if (!VerifiedHold(c, s, tag))
                { log($"{s.Name}: ERROR - unverified incident hold {tag}"); ok = false; }
        }
        return ok;
    }

    public static bool ValidateAcknowledgments(Ctx c, Action<string> log,
        Dictionary<string, HashSet<string>> holds, string? activeId = null)
    {
        bool ok = true;
        foreach (var tag in holds.Values.SelectMany(t => t).Where(t => IsSafetyTag(c, t)).Distinct())
        {
            var id = HoldIncident(c, tag);
            if (id is null || (id != activeId && !IsFalsePositive(c, id)))
            { log($"ERROR: incident hold {tag} has no verified false-positive acknowledgment"); ok = false; }
        }
        return ok;
    }

    public static (bool ok, bool acknowledged) FalsePositiveState(Ctx c, string id)
    {
        var (rc, value, _) = Zfs.Exec(c, "zfs", "get", "-H", "-o", "value", c.FalsePositiveProperty(id), c.PoolDataset);
        if (rc != 0) return (false, false);
        var text = value.Trim();
        if (text == "-") return (true, false);
        return long.TryParse(text, out var timestamp) && timestamp > 0 ? (true, true) : (false, false);
    }

    public static bool IsFalsePositive(Ctx c, string id)
    {
        var (ok, acknowledged) = FalsePositiveState(c, id);
        return ok && acknowledged;
    }

    // Bind the legitimate content baseline to a durable false-positive receipt.
    // Pending/failed acknowledgments never suppress a content warning.
    public static (bool ok, DateTime? from, string? acceptedTip) ContentBaseline(Ctx c, string ds)
    {
        var (rc, value, _) = Zfs.Exec(c, "zfs", "get", "-H", "-o", "value", c.PropAcceptedContent, ds);
        if (rc != 0) return (false, null, null);
        var text = value.Trim();
        if (text == "-") return (true, null, null);
        var parts = text.Split('|');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out _)
            || !parts[1].StartsWith(ds + "@autosnap_", StringComparison.Ordinal)
            || !parts[1].EndsWith("_daily", StringComparison.Ordinal)) return (false, null, null);
        var stamp = parts[1][(ds.Length + "@autosnap_".Length)..^"_daily".Length];
        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var date))
            return (false, null, null);
        var (ackOk, acknowledged) = FalsePositiveState(c, parts[0]);
        if (!ackOk) return (false, null, null);
        return acknowledged ? (true, date, parts[1]) : (true, null, null);
    }

    static string Encode(IEnumerable<Incident> history)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartArray();
            foreach (var i in history)
            {
                w.WriteStartObject(); w.WriteString("id", i.Id); w.WriteString("reason", i.Reason);
                w.WriteString("observed_utc", i.ObservedUtc); w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static (bool ok, bool value, bool recorded) Established(Ctx c, string ds, List<Snap> snaps)
    {
        var (rc, text, _) = Zfs.Exec(c, "zfs", "get", "-H", "-o", "value", c.PropEstablished, ds);
        if (rc != 0) return (false, false, false);
        return text.Trim() switch
        {
            "yes" => (true, true, true), "no" => (true, false, true),
            // Legacy metadata remains evidence only until the explicit state is
            // recorded, BEFORE new latched metadata can make it ambiguous.
            "-" => (true, snaps.Any(s => s.Tier is not null), false),
            _ => (false, false, false)
        };
    }

    public static bool RecordEstablished(Ctx c, Action<string> log, string ds, bool value)
    {
        var (rc, _, error) = Zfs.Exec(c, "zfs", "set", $"{c.PropEstablished}={(value ? "yes" : "no")}", ds);
        if (rc == 0) return true;
        log($"{ds}: ERROR - establishment write failed: {error.Trim()}"); return false;
    }

    // Existing pre-upgrade latches receive an identity when first observed by
    // this version. observed_utc does not pretend to be the original event time.
    public static string? Identity(Ctx c, Action<string> log)
    {
        var (evidenceOk, evidence) = ReadEvidence(c);
        if (!evidenceOk) { log("ERROR: cannot verify current warning evidence"); return null; }
        if (evidence is not null)
        {
            // An acknowledgment receipt with still-unhealthy health means clear
            // failed, not a second incident. Retry keeps this exact identity.
            Mirror(c, log, evidence);
            return evidence.Id;
        }
        var (rc, text, error) = Zfs.Exec(c, "zfs", "get", "-H", "-o", "value", c.PropLatchId, c.PoolDataset);
        if (rc != 0) { log($"ERROR: cannot read latch identity: {error.Trim()}"); return null; }
        var value = text.Trim();
        if (value != "-")
        {
            if (Guid.TryParseExact(value, "N", out _)) return value;
            log("ERROR: invalid latch identity; refusing to replace incident history"); return null;
        }
        value = Guid.NewGuid().ToString("N");
        var (wr, _, we) = Zfs.Exec(c, "zfs", "set", $"{c.PropLatchId}={value}", c.PoolDataset);
        if (wr != 0) { log($"ERROR: cannot persist latch identity: {we.Trim()}"); return null; }
        log($"  META:ADD {c.PoolDataset} {c.PropLatchId}={value}"); return value;
    }

    public static bool Protect(Ctx c, Action<string> log, List<Snap> snaps,
                               Dictionary<string, HashSet<string>> holds, string id, string reason)
    {
        bool ok = true;
        string tag = HoldTag(c, id);
        // Protect every snapshot first. Only after this entire phase succeeds
        // may normal keeper reconciliation release superseded tier holds.
        foreach (var s in snaps)
        {
            if (!holds.TryGetValue(s.Name, out var tags)) holds[s.Name] = tags = new();
            if (!tags.Contains(tag))
            {
                var (rc, _, error) = Zfs.Exec(c, "zfs", "hold", tag, s.Name);
                if (rc != 0 && !error.Contains("already") && !error.Contains("exists"))
                { log($"  HOLD:ADD {s.Name} FAILED: {error.Trim()}"); ok = false; continue; }
                tags.Add(tag); log($"  HOLD:ADD {s.Name} ({tag}, incident {id})");
            }
            if (!ReadHistory(s.LatchHistory, out var history))
            { log($"{s.Name}: ERROR - invalid latch history; preserving protection and metadata"); ok = false; continue; }
            if (history.Any(i => i.Id == id)) continue;
            history.Add(new(id, reason, c.Now.ToString("O")));
            var value = Encode(history);
            if (Encoding.UTF8.GetByteCount(value) > 8192)
            { log($"{s.Name}: ERROR - latch history exceeds ZFS property limit; preserving protection"); ok = false; continue; }
            var (wr, _, we) = Zfs.Exec(c, "zfs", "set", $"{c.PropLatchHistory}={value}", s.Name);
            if (wr != 0) { log($"{s.Name}: ERROR - latch history write failed: {we.Trim()}"); ok = false; }
        }
        return ok;
    }

    public static bool Reconcile(Ctx c, Action<string> log, List<Snap> snaps,
                                 HashSet<string> monthly, HashSet<string> weekly, Func<Snap, DateTime> desired,
                                 Dictionary<string, HashSet<string>> holds)
    {
        // All replacement keepers are held before any prior keeper is demoted.
        foreach (var s in snaps.Where(s => monthly.Contains(s.Name) || weekly.Contains(s.Name)))
        {
            string tag = monthly.Contains(s.Name) ? c.TagMonthly : c.TagWeekly;
            if (!holds.TryGetValue(s.Name, out var tags)) holds[s.Name] = tags = new();
            if (tags.Contains(tag)) continue;
            var (rc, _, error) = Zfs.Exec(c, "zfs", "hold", tag, s.Name);
            if (rc != 0 && !error.Contains("already") && !error.Contains("exists"))
            { log($"{s.Name}: ERROR - replacement keeper hold failed: {error.Trim()}"); return false; }
            tags.Add(tag); log($"  HOLD:ADD {s.Name} ({tag}, before reconciliation)");
        }
        bool ok = true;
        foreach (var s in snaps.OrderBy(s => s.Date))
        {
            string tier = monthly.Contains(s.Name) ? "monthly" : weekly.Contains(s.Name) ? "weekly" : "daily";
            if (!holds.TryGetValue(s.Name, out var tags)) holds[s.Name] = tags = new();
            var managed = tags.Where(t => t == c.TagMonthly || t == c.TagWeekly).ToHashSet();
            if (!Zfs.Promote(c, log, s, desired(s), tier, managed)) { ok = false; continue; }
            // Keep the plan's hold map in sync; otherwise a later destroy repeats
            // a successful demotion release against stale load-time hold data.
            tags.Remove(c.TagMonthly); tags.Remove(c.TagWeekly);
            if (tier != "daily") tags.Add(tier == "monthly" ? c.TagMonthly : c.TagWeekly);
        }
        return ok;
    }

    public static bool ReleaseAcknowledged(Ctx c, Action<string> log, List<Snap> snaps,
                                           Dictionary<string, HashSet<string>> holds)
    {
        // Every physical hold identifies its own incident; valid older history
        // cannot verify a later hold whose history write failed.
        if (!ValidateHistory(c, log, snaps, holds)) return false;
        if (!ValidateAcknowledgments(c, log, holds)) return false;
        bool ok = true;
        foreach (var s in snaps)
        {
            if (!holds.TryGetValue(s.Name, out var tags)) continue;
            foreach (var tag in tags.Where(t => IsSafetyTag(c, t)).ToList())
            {
                var (rc, _, error) = Zfs.Exec(c, "zfs", "release", tag, s.Name);
                if (rc != 0 && !Zfs.IsMissingHold(error))
                { log($"{s.Name}: ERROR - incident hold release failed: {error.Trim()}"); ok = false; continue; }
                tags.Remove(tag); log($"  HOLD:REMOVE {s.Name} ({tag}; false positive acknowledged, history retained)");
            }
        }
        return ok;
    }
}
