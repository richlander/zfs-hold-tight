#:property PublishAot=true
#:property InvariantGlobalization=true
#:property StripSymbols=true

// zfs-hold-tight - defender-side GFS retention for TrueNAS archive/* datasets.
//
// Replication pulls ONLY daily snapshots from merritt (Approach 4 in
// zfs/backup-history.md). This tool thins those dailies into a tiered retention (daily/weekly/monthly)
// keep set - newest N dailies + newest-per-week + newest-per-month - tagging each with
// lander:keep_until (and lander:tier = daily|weekly|monthly, for audit) and destroying
// dailies past expiry. It never creates snapshots.
//
// SAFETY: it deletes only when the dataset's feed is provably healthy; otherwise it
// HOLDs (deletes nothing) and alerts. Full contract: docs/SAFETY.md.
//   Run-level guards, evaluated INDEPENDENTLY PER DATASET (HOLD that dataset on failure):
//   recent 7-day window complete, newest daily fresh (<=26h, guardMaxDailyAgeHours),
//   clock sane, no sudden data shrinkage (guardMaxDailyShrinkPct — mass deletion), no
//   excessive churn (guardMaxDailyChurnPct — mass rewrite/encryption), volume
//   circuit-breaker (<=5/run w/o --force). Each guard threshold defaults globally but
//   may be overridden per retention policy.
//   Per-snapshot: never the newest/base, week/month keepers, uncovered, held, or
//   non-_daily snapshots.
//
// Every run appends to /mnt/archive/tools/zfs-hold-tight.log and (on --prune) pings
// healthchecks.io: the base URL on a clean run, /fail with diagnostics on trouble.
//
// Build (native, glibc-matched to the target - NOT a cross-compile). The dotnet AOT SDK
// image is Debian 12-based (glibc 2.36, matching TrueNAS) with clang+zlib already present:
//   docker run --rm -v "$PWD:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0-aot \
//     dotnet publish zfs-hold-tight.cs -r linux-x64 -c Release -o out
//   # verify it will run on the target: highest symbol must be <= the target's glibc
//   objdump -T out/zfs-hold-tight | grep -oE 'GLIBC_[0-9.]+' | sort -V | tail -1
//
// Usage: zfs-hold-tight [--prune] [--force] [--dataset archive/<name>] [-v] [--no-hc]
//        zfs-hold-tight --doctor [--dataset archive/<name>]
//        zfs-hold-tight --check-holds [--no-hc]
//        zfs-hold-tight --fix [--dataset archive/<name>]
//        zfs-hold-tight --hold-adhoc <snapshot> [--no-hc]
//        zfs-hold-tight --hold-latest-tier <tier> [--dataset archive/<name>] [--no-hc]
//        zfs-hold-tight --hold-latest [--dataset archive/<name>] [--no-hc]

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Load config from JSON
string configPath = Environment.GetEnvironmentVariable("ZFS_HOLD_TIGHT_CONFIG")
    ?? Path.Combine(AppContext.BaseDirectory, "zfs-hold-tight.json");  // BaseDirectory works under single-file/AOT
JsonDocument? configDoc = null;
JsonElement cfg = default;
try
{
    string configText = File.ReadAllText(configPath);
    configDoc = JsonDocument.Parse(configText);
    cfg = configDoc.RootElement;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Failed to load config from {configPath}: {ex.Message}");
    return 2;
}

// Validate config structure
if (!cfg.TryGetProperty("retentionPolicies", out var policiesElem) || policiesElem.ValueKind != JsonValueKind.Object)
{
    Console.Error.WriteLine("FATAL: config must have 'retentionPolicies' object");
    configDoc?.Dispose();
    return 2;
}
if (!cfg.TryGetProperty("datasets", out var datasetsElem) || datasetsElem.ValueKind != JsonValueKind.Object)
{
    Console.Error.WriteLine("FATAL: config must have 'datasets' object");
    configDoc?.Dispose();
    return 2;
}

// Validate and load numeric config with bounds
int MaxDeletePerRun = 5, RecentWindowDays = 7, MaxDailyAgeHours = 26, MaxDailyShrinkPct = 33, MaxDailyChurnPct = 50;
try
{
    if (cfg.TryGetProperty("guardMaxDeletePerRun", out var gdp))
    {
        MaxDeletePerRun = gdp.GetInt32();
        if (MaxDeletePerRun <= 0) throw new ArgumentException("guardMaxDeletePerRun must be > 0");
    }
    if (cfg.TryGetProperty("guardRecentWindowDays", out var grd))
    {
        RecentWindowDays = grd.GetInt32();
        if (RecentWindowDays <= 0 || RecentWindowDays > 365) throw new ArgumentException("guardRecentWindowDays must be 1-365");
    }
    if (cfg.TryGetProperty("guardMaxDailyAgeHours", out var gma))
    {
        MaxDailyAgeHours = gma.GetInt32();
        if (MaxDailyAgeHours <= 0 || MaxDailyAgeHours > 720) throw new ArgumentException("guardMaxDailyAgeHours must be 1-720");
    }
    if (cfg.TryGetProperty("guardMaxDailyShrinkPct", out var gms))
    {
        MaxDailyShrinkPct = gms.GetInt32();   // 0 disables the data-shrinkage guard
        if (MaxDailyShrinkPct < 0 || MaxDailyShrinkPct > 99) throw new ArgumentException("guardMaxDailyShrinkPct must be 0-99 (0 disables)");
    }
    if (cfg.TryGetProperty("guardMaxDailyChurnPct", out var gmc))
    {
        MaxDailyChurnPct = gmc.GetInt32();    // 0 disables the churn (mass-rewrite) guard
        if (MaxDailyChurnPct < 0 || MaxDailyChurnPct > 100) throw new ArgumentException("guardMaxDailyChurnPct must be 0-100 (0 disables)");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: invalid config values: {ex.Message}");
    configDoc?.Dispose();
    return 2;
}

// Validate a per-policy guard override (same bounds as the top-level keys); falls back
// to the global default when the policy omits the key.
int GuardInt(JsonElement p, string key, int dflt, Func<int, bool> ok, string rule)
{
    if (!p.TryGetProperty(key, out var e)) return dflt;
    int v = e.GetInt32();
    if (!ok(v)) throw new ArgumentException($"{key} {rule}");
    return v;
}

string logDir = Environment.GetEnvironmentVariable("ZFS_HOLD_TIGHT_LOG_DIR")
    ?? (cfg.TryGetProperty("logDir", out var ld) ? ld.GetString() : null)
    ?? "/mnt/archive/tools/logs";
string logPath = Path.Combine(logDir, $"zfs-hold-tight-{DateTime.UtcNow:yyyy-MM-dd}.log");

string? hcUrl = Environment.GetEnvironmentVariable("ZFS_HOLD_TIGHT_HC_URL");
if (string.IsNullOrEmpty(hcUrl) && cfg.TryGetProperty("healthchecksUrl", out var hu))
    hcUrl = hu.GetString();

// Healthchecks is optional: with no URL configured (and no env override), skip pings
// entirely rather than baking in a site URL. Set healthchecksUrl in config to enable.
bool noHc = args.Contains("--no-hc") || string.IsNullOrEmpty(hcUrl);
var snapRe = new Regex(@"@autosnap_(\d{4}-\d{2}-\d{2}_\d{2}:\d{2}:\d{2})_daily$");
var now = DateTime.UtcNow;
var lines = new List<string>();
void Log(string m) { Console.WriteLine(m); lines.Add(m); }

// Build retention policy from config (needed for all modes)
var retention = new Dictionary<string, Retention>();
var guardsByDataset = new Dictionary<string, Guards>();
var policies = new Dictionary<string, Retention>();
var policyGuards = new Dictionary<string, Guards>();
const string DatasetPrefix = "archive/";  // allowlist: only prune archive/* datasets

// Load and validate retention policies + their (optional) per-policy guard overrides.
try
{
    foreach (var policy in policiesElem.EnumerateObject())
    {
        var p = policy.Value;
        int daily = p.TryGetProperty("dailyKeepDays", out var d) ? d.GetInt32() : 30;
        int weekly = p.TryGetProperty("weeklyKeepDays", out var w) ? w.GetInt32() : 5 * 365;
        int monthly = p.TryGetProperty("monthlyKeepDays", out var m) ? m.GetInt32() : 5 * 365;

        if (daily <= 0 || weekly <= 0 || monthly <= 0)
            throw new ArgumentException($"Policy '{policy.Name}': all retention days must be positive");

        policies[policy.Name] = new Retention(daily, weekly, monthly);
        policyGuards[policy.Name] = new Guards(
            GuardInt(p, "guardMaxDeletePerRun",   MaxDeletePerRun,   v => v > 0,              "must be > 0"),
            GuardInt(p, "guardRecentWindowDays",  RecentWindowDays,  v => v > 0 && v <= 365,  "must be 1-365"),
            GuardInt(p, "guardMaxDailyAgeHours",  MaxDailyAgeHours,  v => v > 0 && v <= 720,  "must be 1-720"),
            GuardInt(p, "guardMaxDailyShrinkPct", MaxDailyShrinkPct, v => v >= 0 && v <= 99,  "must be 0-99 (0 disables)"),
            GuardInt(p, "guardMaxDailyChurnPct",  MaxDailyChurnPct,  v => v >= 0 && v <= 100, "must be 0-100 (0 disables)"));
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: invalid retention policies: {ex.Message}");
    configDoc?.Dispose();
    return 2;
}

// Load datasets and map to policies, with allowlist validation
try
{
    foreach (var dataset in datasetsElem.EnumerateObject())
    {
        if (!dataset.Name.StartsWith(DatasetPrefix))
            throw new ArgumentException($"Dataset '{dataset.Name}' must start with '{DatasetPrefix}'");

        string policyName = dataset.Value.GetString() ?? "baseline";
        if (policies.TryGetValue(policyName, out var policy))
        {
            retention[dataset.Name] = policy;
            guardsByDataset[dataset.Name] = policyGuards[policyName];
        }
        else
            throw new ArgumentException($"Dataset '{dataset.Name}' references unknown policy '{policyName}'");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"FATAL: invalid dataset config: {ex.Message}");
    configDoc?.Dispose();
    return 2;
}

// Parse --dataset if present
string? datasetFilter = null;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--dataset") datasetFilter = args[i + 1];

// Run-lock: serialize any run that mutates ZFS state (prune/fix/hold-*), so two
// overlapping cron invocations can't double-process. Read-only modes skip the lock.
bool willMutate = args.Contains("--prune") || args.Contains("--fix")
    || args.Contains("--hold-adhoc") || args.Contains("--hold-latest-tier") || args.Contains("--hold-latest");
FileStream? runLock = null;
if (willMutate)
{
    string lockPath = Path.Combine(logDir, "zfs-hold-tight.lock");
    try
    {
        Directory.CreateDirectory(logDir);
        runLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    catch (IOException)
    {
        Console.Error.WriteLine("another zfs-hold-tight run is in progress (lock held); exiting");
        configDoc?.Dispose();
        return 3;
    }
}

// Check for special commands
if (args.Contains("--doctor"))
{
    var result = Doctor(datasetFilter);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--check-holds"))
{
    var result = CheckHolds(noHc);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--fix"))
{
    var result = Fix(datasetFilter);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--hold-adhoc"))
{
    int idx = Array.IndexOf(args, "--hold-adhoc");
    if (idx + 1 >= args.Length) { Console.Error.WriteLine("--hold-adhoc requires snapshot argument"); configDoc?.Dispose(); return 2; }
    string snap = args[idx + 1];
    var result = HoldAdhoc(snap, noHc);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--hold-latest-tier"))
{
    int idx = Array.IndexOf(args, "--hold-latest-tier");
    if (idx + 1 >= args.Length) { Console.Error.WriteLine("--hold-latest-tier requires tier argument"); configDoc?.Dispose(); return 2; }
    string tier = args[idx + 1];
    if (tier is not ("daily" or "weekly" or "monthly"))
    { Console.Error.WriteLine($"invalid tier '{tier}' (must be daily|weekly|monthly)"); configDoc?.Dispose(); return 2; }
    var result = HoldLatestTier(tier, datasetFilter, noHc);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--hold-latest"))
{
    var result = HoldLatestTier("daily", datasetFilter, noHc);
    configDoc?.Dispose();
    return result;
}

bool prune = args.Contains("--prune");
bool force = args.Contains("--force");
bool verbose = args.Contains("-v") || args.Contains("--verbose");
string? only = datasetFilter;
if (only is not null && !retention.ContainsKey(only)) { Console.Error.WriteLine($"unknown dataset '{only}'"); return 2; }

Log($"=== zfs-hold-tight {now:yyyy-MM-dd HH:mm:ss}Z  mode={(prune ? "PRUNE" : "dry-run")}{(force ? " --force" : "")} ===");
if (prune && !noHc) HcPing(hcUrl + "/start", null);

int totalKeep = 0, totalDoom = 0, destroyAttempted = 0, destroySucceeded = 0, destroyFails = 0;
var held = new List<string>();   // datasets held by a guard
var metadataErrors = new List<string>();  // metadata/hold failures

foreach (var (ds, ret) in retention)
{
    if (only is not null && ds != only) continue;
    var (listCode, snaps) = ListDailies(ds);
    if (listCode != 0)
    {
        Log($"{ds}: HOLD - zfs list failed (cannot verify feed health)");
        held.Add($"{ds}: zfs list error");
        continue;
    }
    if (snaps.Count == 0) { Log($"{ds}: no daily snapshots, skip"); continue; }
    var gd = guardsByDataset[ds];   // this dataset's effective guard thresholds

    // ---- run-level guards (HOLD on failure) ----
    string? guard = HealthGuard(snaps, ret, gd);
    if (guard is not null) { Log($"{ds}: HOLD - {guard} (no deletions)"); held.Add($"{ds}: {guard}"); continue; }

    // ---- keepers + desired keep_until ----
    var weekKeeper  = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                           .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
    var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month)).Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
    var newest = snaps.MaxBy(s => s.Date)!.Name;
    DateTime Desired(Snap s)
    {
        var k = s.Date.AddDays(ret.DailyKeepDays);
        if (weekKeeper.Contains(s.Name))  k = Later(k, s.Date.AddDays(ret.WeeklyKeepDays));
        if (monthKeeper.Contains(s.Name)) k = Later(k, s.Date.AddDays(ret.MonthlyKeepDays));
        return k;
    }

    // Current holds on this dataset — used to (a) honor guard 12 (never delete a held
    // snapshot) and (b) tell our managed lander:monthly hold from foreign/defensive ones.
    var holdMap = LoadHolds(ds);
    bool IsForeignHeld(string name) =>
        holdMap.TryGetValue(name, out var tags) && tags.Any(t => t != "lander:monthly");

    var doomed = new List<string>();
    var promoteErrors = new List<string>();  // snapshots whose metadata/hold write failed
    foreach (var s in snaps.OrderBy(s => s.Date))
    {
        var keepUntil = Desired(s);
        string tier = monthKeeper.Contains(s.Name) ? "monthly" : weekKeeper.Contains(s.Name) ? "weekly" : "daily";
        if (prune && !Promote(s, keepUntil, tier)) promoteErrors.Add(Short(s.Name));
        if (now <= keepUntil) continue;                     // still retained
        if (s.Name == newest) continue;                     // never the base (guard 7)
        if (IsForeignHeld(s.Name))                          // guard 12: held => never delete, skip cleanly
        { if (verbose) Log($"  {ds}: skip {Short(s.Name)} - held ({string.Join(",", holdMap[s.Name])})"); continue; }
        bool weekCov  = snaps.Any(o => o.Name != s.Name && SameIsoWeek(o.Date, s.Date) && Desired(o) > now);
        bool monthCov = snaps.Any(o => o.Name != s.Name && o.Date.Year == s.Date.Year && o.Date.Month == s.Date.Month && Desired(o) > now);
        if (!(weekCov && monthCov)) { Log($"  {ds}: skip {Short(s.Name)} - not yet covered by a keeper"); continue; }
        doomed.Add(s.Name);
    }

    // Metadata/hold write failures HOLD the dataset (delete nothing) and alert.
    if (promoteErrors.Count > 0)
    {
        Log($"{ds}: HOLD - metadata/hold errors detected (no deletions)");
        held.Add($"{ds}: metadata/hold failures");
        metadataErrors.AddRange(promoteErrors.Select(n => $"  {ds}@{n}"));
        continue;
    }

    // ---- volume circuit-breaker ----
    if (doomed.Count > gd.MaxDeletePerRun && !force)
    {
        Log($"{ds}: HOLD - would delete {doomed.Count} > cap {gd.MaxDeletePerRun}; rerun with --force if intended");
        held.Add($"{ds}: volume {doomed.Count}>cap"); continue;
    }

    int kept = snaps.Count - doomed.Count;
    totalKeep += kept; totalDoom += doomed.Count;
    string verb = prune ? "destroy" : "would destroy";
    string rng = doomed.Count > 0 ? $" [{Short(doomed[0])} .. {Short(doomed[^1])}]" : "";
    Log($"{ds}: {snaps.Count} dailies -> keep {kept} (d{ret.DailyKeepDays}/w{ret.WeeklyKeepDays / 7}/m{ret.MonthlyKeepDays / 30}), {verb} {doomed.Count}{rng}");
    foreach (var name in doomed)
    {
        if (verbose) Log($"  {verb}: {Short(name)}");
        if (!prune) continue;

        // Release our managed lander:monthly hold before destroy (idempotent). Doomed
        // snapshots carry no foreign hold (guard 12 excluded those above).
        var (rc, _, re) = Exec("zfs", "release", "lander:monthly", name);
        if (rc != 0 && !re.Contains("no such hold") && !re.Contains("not found"))
            Log($"    WARNING: release lander:monthly failed {Short(name)}: {re.Trim()}");

        destroyAttempted++;
        var (code, _, err) = Exec("zfs", "destroy", name);
        if (code != 0) { destroyFails++; Log($"  FAILED {Short(name)}: {err.Trim()}"); }
        else { destroySucceeded++; if (verbose) Log($"  OK: {Short(name)}"); }
    }
}

bool trouble = held.Count > 0 || metadataErrors.Count > 0 || (prune && destroyFails > 0);
Log($"\n{(prune ? "PRUNED" : "DRY RUN")}: kept {totalKeep}, {(prune ? $"destroyed {destroySucceeded}/{destroyAttempted}" : $"would destroy {totalDoom}")}"
    + (held.Count > 0 ? $", {held.Count} HELD" : "")
    + (metadataErrors.Count > 0 ? $", {metadataErrors.Count} metadata errors" : "")
    + (prune && destroyFails > 0 ? $", {destroyFails} destroy-failures" : ""));
if (metadataErrors.Count > 0) Log("Metadata errors:\n" + string.Join("\n", metadataErrors));
FlushLog();
if (prune && !noHc)
    HcPing(trouble ? hcUrl + "/fail" : hcUrl!,   // !noHc guarantees hcUrl is set
           trouble ? "zfs-hold-tight trouble:\n" + string.Join("\n", held) + (metadataErrors.Count > 0 ? "\nMetadata errors:\n" + string.Join("\n", metadataErrors) : "") + (destroyFails > 0 ? $"\n{destroyFails} destroy failures" : "")
                   : $"ok: kept {totalKeep}, destroyed {destroySucceeded}");
configDoc?.Dispose();
runLock?.Dispose();
return trouble ? 1 : 0;

// ---- on-demand hold ----
int HoldAdhoc(string snap, bool noHc)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    var now = DateTime.UtcNow;
    Log($"=== zfs-hold-tight {now:yyyy-MM-dd HH:mm:ss}Z  mode=hold-adhoc ===");

    if (!snap.StartsWith(DatasetPrefix) || !snap.Contains('@'))
    { Log($"FAIL: snapshot '{snap}' must be under '{DatasetPrefix}' and name a snapshot (contain '@')"); FlushLog(lines); return 2; }

    var (c, _, e) = Exec("zfs", "hold", "lander:adhoc", snap);
    if (c != 0) { Log($"FAIL: {e.Trim()}"); FlushLog(lines); return 1; }
    Log($"OK: held {snap} with tag lander:adhoc");

    // Verify hold was created
    var (vc, vo, _) = Exec("zfs", "holds", snap);
    if (vc == 0 && vo.Contains("lander:adhoc"))
        Log($"Verified: hold is active");
    else
        Log($"WARNING: hold may not be active (verification inconclusive)");

    FlushLog(lines);
    if (!noHc)
        HcPing(hcUrl + "/adhoc-hold", $"ok: held {snap}");
    return 0;
}

// ---- check holds exceeding retention ----
int CheckHolds(bool noHc)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    var now = DateTime.UtcNow;
    Log($"=== zfs-hold-tight {now:yyyy-MM-dd HH:mm:ss}Z  mode=check-holds ===");

    const int MaxRetentionDays = 5 * 365;  // 5 years (coarse net: longest policy retention)
    var maxKeepDate = now.AddDays(-MaxRetentionDays);

    var pool = DatasetPrefix.TrimEnd('/');
    var (c, out_, err) = Exec("zfs", "holds", "-r", pool);
    if (c != 0) { Log($"FAIL: {err.Trim()}"); FlushLog(lines); return 1; }

    var stale = new List<string>();
    foreach (var line in out_.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.StartsWith("NAME")) continue;  // skip header
        var cols = line.Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (cols.Length < 3) continue;
        var snap = cols[0];

        // Parse snapshot date from name
        var m = new Regex(@"@autosnap_(\d{4}-\d{2}-\d{2})").Match(snap);
        if (!m.Success) continue;
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var snapDate))
            continue;

        if (snapDate < maxKeepDate)
            stale.Add($"{snap}: held but older than {MaxRetentionDays}d");
    }

    if (stale.Count == 0)
        Log($"OK: no stale holds found");
    else
    {
        Log($"WARNING: {stale.Count} holds exceed {MaxRetentionDays}d retention:");
        foreach (var s in stale) Log($"  {s}");
    }

    FlushLog(lines);
    if (!noHc && stale.Count > 0)
        HcPing(hcUrl + "/fail", $"stale holds:\n" + string.Join("\n", stale));
    return stale.Count > 0 ? 1 : 0;
}

// ---- doctor: validate hold strategy ----
int Doctor(string? datasetFilter)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    Log($"=== zfs-hold-tight {now:yyyy-MM-dd HH:mm:ss}Z  mode=doctor ===");

    int missing = 0, protected_ = 0, adhoc_ = 0, orphaned = 0;
    foreach (var (ds, _) in retention)
    {
        if (datasetFilter is not null && ds != datasetFilter) continue;
        var (_, snaps) = ListDailies(ds);
        if (snaps.Count == 0) { Log($"{ds}: no snapshots"); continue; }

        // Find monthlies
        var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month))
                              .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();

        // Check which snapshots have holds
        var holdMap = LoadHolds(ds);

        // Report monthly status
        Log($"{ds}: {monthKeeper.Count} monthlies");
        foreach (var monthly in monthKeeper.OrderByDescending(s => s))
        {
            if (holdMap.TryGetValue(monthly, out var tags) && tags.Contains("lander:monthly"))
            {
                Log($"  ✓ {Short(monthly)} (lander:monthly)");
                protected_++;
            }
            else
            {
                Log($"  ✗ {Short(monthly)} MISSING HOLD");
                missing++;
            }
        }

        // Report other holds
        foreach (var (snap, tags) in holdMap)
        {
            if (!monthKeeper.Contains(snap) && snap.Contains("_daily"))
            {
                if (tags.Contains("lander:adhoc"))
                {
                    Log($"  ℹ {Short(snap)} (lander:adhoc - defensive)");
                    adhoc_++;
                }
                else if (!tags.Any(t => t.StartsWith("lander:")))
                {
                    Log($"  ? {Short(snap)} has non-lander holds: {string.Join(",", tags)}");
                    orphaned++;
                }
            }
        }
    }

    Log($"\nSummary: {protected_} monthlies protected, {missing} missing, {adhoc_} adhoc, {orphaned} unexpected");
    if (missing == 0 && orphaned == 0)
        Log("✓ Hold strategy validated");
    else
        Log($"⚠ Issues: {missing} missing holds, {orphaned} unexpected");

    FlushLog(lines);
    return missing > 0 ? 1 : 0;
}

// ---- fix: create missing monthly holds ----
int Fix(string? datasetFilter)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    Log($"=== zfs-hold-tight {now:yyyy-MM-dd HH:mm:ss}Z  mode=fix ===");

    int fixed_ = 0;
    foreach (var (ds, _) in retention)
    {
        if (datasetFilter is not null && ds != datasetFilter) continue;
        var (_, snaps) = ListDailies(ds);
        if (snaps.Count == 0) continue;

        // Find monthlies
        var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month))
                              .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();

        foreach (var monthly in monthKeeper)
        {
            var (c, _, e) = Exec("zfs", "hold", "lander:monthly", monthly);
            if (c == 0)
            {
                Log($"  ✓ {ds}/{Short(monthly)}");
                fixed_++;
            }
            else if (e.Contains("already"))
                Log($"  ✓ {ds}/{Short(monthly)} (already held)");
            else
                Log($"  ✗ {ds}/{Short(monthly)}: {e.Trim()}");
        }
    }

    Log($"Fixed {fixed_} holds");
    FlushLog(lines);
    return 0;
}

// ---- hold latest of tier ----
int HoldLatestTier(string tier, string? datasetFilter, bool noHc)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    Log($"=== zfs-hold-tight {now:yyyy-MM-dd HH:mm:ss}Z  mode=hold-latest-tier tier={tier} ===");

    foreach (var (ds, _) in retention)
    {
        if (datasetFilter is not null && ds != datasetFilter) continue;
        var (_, snaps) = ListDailies(ds);
        if (snaps.Count == 0) continue;

        // Find latest of requested tier
        Snap? target = null;
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

        if (target is not null)
        {
            var (c, _, e) = Exec("zfs", "hold", "lander:adhoc", target.Name);
            if (c == 0)
                Log($"  ✓ {ds}/{Short(target.Name)} ({tier})");
            else if (e.Contains("already"))
                Log($"  ✓ {ds}/{Short(target.Name)} ({tier}) - already held");
            else
                Log($"  ✗ {ds}/{Short(target.Name)}: {e.Trim()}");
        }
    }

    FlushLog(lines);
    if (!noHc) HcPing(hcUrl + "/hold-latest-" + tier, "ok");
    return 0;
}

// ---- guards (evaluated independently per dataset; thresholds from that dataset's policy) ----
string? HealthGuard(List<Snap> snaps, Retention ret, Guards gd)
{
    var newest = snaps.Max(s => s.Date);
    var newestSnap = snaps.MaxBy(s => s.Date)!;
    if (snaps.Any(s => s.Date > now.AddMinutes(5)))                       // guard: clock
        return "snapshot dated in the future (clock?)";
    if (now - newest > TimeSpan.FromHours(gd.MaxDailyAgeHours))           // guard: newest daily is fresh
        return $"newest daily is stale: {newest:yyyy-MM-dd HH:mm}Z is {(now - newest).TotalHours:F0}h old (> {gd.MaxDailyAgeHours}h)";
    var days = snaps.Select(s => s.Date.Date).ToHashSet();               // guard: recent window
    var missing = new List<string>();
    for (int d = 0; d < gd.RecentWindowDays; d++)
    {
        var day = newest.Date.AddDays(-d);
        if (!days.Contains(day)) missing.Add(day.ToString("MM-dd"));
    }
    if (missing.Count > 0) return $"gap in last {gd.RecentWindowDays}d: missing {string.Join(",", missing)}";

    // baseline = recent dailies of THIS dataset (so a small dataset is judged on its own scale)
    var baseline = snaps.Where(s => s.Name != newestSnap.Name && s.Date >= newest.AddDays(-gd.RecentWindowDays))
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

    if (snaps.Count < ret.DailyKeepDays)                                 // guard: population (note only)
        Log($"  note: {snaps.Count} dailies < keep {ret.DailyKeepDays}, nothing beyond window");
    return null;
}

// ---- helpers ----
(int, List<Snap>) ListDailies(string ds)
{
    // -p: exact bytes for referenced/written (the shrinkage + churn guards need raw numbers).
    var (code, outp, err) = Exec("zfs", "list", "-Hp", "-t", "snapshot", "-o", "name,lander:keep_until,lander:tier,referenced,written", "-d", "1", ds);
    var list = new List<Snap>();
    if (code != 0) { Log($"ERROR: zfs list {ds} failed: {err.Trim()}"); return (code, list); }
    foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        var cols = line.Split('\t');
        var m = snapRe.Match(cols[0]);
        if (!m.Success) continue;
        // TryParse, not Parse: a regex-shaped but out-of-range date (e.g. ..._2026-13-45_...)
        // must skip that one snapshot, never abort the whole run.
        if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd_HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
        { Log($"  WARNING: unparseable snapshot date, skipping: {cols[0]}"); continue; }
        long? ku = cols.Length > 1 && long.TryParse(cols[1], out var v) ? v : null;
        string? tier = cols.Length > 2 && cols[2] != "-" ? cols[2] : null;
        long? refb = cols.Length > 3 && long.TryParse(cols[3], out var rb) ? rb : null;
        long? wrb = cols.Length > 4 && long.TryParse(cols[4], out var wb) ? wb : null;
        list.Add(new Snap(cols[0], date, ku, tier, refb, wrb));
    }
    return (0, list);
}

// Annotate one snapshot for its current tier. Returns false if any write failed.
// Only keepers (weekly/monthly) carry lander:tier + lander:keep_until tags; a plain
// daily is implicitly daily and stays untagged. This keeps the first run cheap (~24
// writes, not ~280) and is self-correcting: a snapshot that was a keeper last run but
// is now superseded (demotion) has its tags inherited away and its monthly hold released
// — without that release a demoted monthly's hold would later block its own destroy.
bool Promote(Snap s, DateTime keepUntil, string tier)
{
    bool success = true;
    if (tier == "daily")
    {
        if (s.Tier == "monthly")   // demoted monthly: drop the protective hold first
        {
            var (rc, _, re) = Exec("zfs", "release", "lander:monthly", s.Name);
            if (rc != 0 && !re.Contains("no such hold") && !re.Contains("not found"))
                Log($"    WARNING: release lander:monthly failed {Short(s.Name)}: {re.Trim()}");
        }
        if (s.Tier != null)
        {
            var (c, _, e) = Exec("zfs", "inherit", "lander:tier", s.Name);
            if (c != 0) { Log($"    ERROR: inherit tier failed {Short(s.Name)}: {e.Trim()}"); success = false; }
        }
        if (s.KeepUntil != null)
        {
            var (c, _, e) = Exec("zfs", "inherit", "lander:keep_until", s.Name);
            if (c != 0) { Log($"    ERROR: inherit keep_until failed {Short(s.Name)}: {e.Trim()}"); success = false; }
        }
        return success;
    }

    long want = ((DateTimeOffset)keepUntil).ToUnixTimeSeconds();
    if (s.KeepUntil != want)
    {
        var (c, _, e) = Exec("zfs", "set", $"lander:keep_until={want}", s.Name);
        if (c != 0) { Log($"    ERROR: set keep_until failed {Short(s.Name)}: {e.Trim()}"); success = false; }
    }
    if (s.Tier != tier)   // weekly|monthly — highest tier the snapshot qualifies for
    {
        var (c, _, e) = Exec("zfs", "set", $"lander:tier={tier}", s.Name);
        if (c != 0) { Log($"    ERROR: set tier failed {Short(s.Name)}: {e.Trim()}"); success = false; }
    }
    // Auto-create hold on monthlies for protection
    if (tier == "monthly")
    {
        var (c, _, e) = Exec("zfs", "hold", "lander:monthly", s.Name);
        if (c == 0)
            Log($"    hold: {Short(s.Name)} (monthly keeper protected)");
        else if (e.Contains("already") || e.Contains("exists"))
            { /* idempotent: hold already present */ }
        else
            { Log($"    ERROR: hold failed {Short(s.Name)}: {e.Trim()}"); success = false; }
    }
    return success;
}

// Parse `zfs holds -r <ds>` into snapshot -> set of hold tags. Tolerant of tab- or
// space-aligned output; only the first two columns (NAME, TAG) are needed.
Dictionary<string, HashSet<string>> LoadHolds(string ds)
{
    var map = new Dictionary<string, HashSet<string>>();
    var (c, outp, _) = Exec("zfs", "holds", "-r", ds);
    if (c != 0) return map;
    foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
    {
        if (line.StartsWith("NAME")) continue;
        var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (cols.Length < 2) continue;
        if (!map.TryGetValue(cols[0], out var set)) { set = new(); map[cols[0]] = set; }
        set.Add(cols[1]);
    }
    return map;
}

void HcPing(string url, string? body)
{
    var a = new List<string> { "-fsS", "--retry", "2", "-m", "10" };
    if (body is not null) { a.Add("--data-raw"); a.Add(body); }
    a.Add(url);
    try { Exec("curl", a.ToArray()); } catch { /* best effort */ }
}

void FlushLog(List<string>? logLines = null)
{
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath, string.Join("\n", logLines ?? lines) + "\n");
    }
    catch { /* best effort - e.g. dry-run as non-root cannot write the root-owned log */ }
}

static (int code, string outp, string err) Exec(string file, params string[] a)
{
    var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var x in a) psi.ArgumentList.Add(x);
    using var p = Process.Start(psi)!;
    string o = p.StandardOutput.ReadToEnd(), e = p.StandardError.ReadToEnd();
    p.WaitForExit();
    return (p.ExitCode, o, e);
}

static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
static bool SameIsoWeek(DateTime a, DateTime b) =>
    ISOWeek.GetYear(a) == ISOWeek.GetYear(b) && ISOWeek.GetWeekOfYear(a) == ISOWeek.GetWeekOfYear(b);
static string Short(string snap) => snap[(snap.IndexOf('@') + 1)..];

record Retention(int DailyKeepDays, int WeeklyKeepDays, int MonthlyKeepDays);
record Guards(int MaxDeletePerRun, int RecentWindowDays, int MaxDailyAgeHours, int MaxDailyShrinkPct, int MaxDailyChurnPct);
record Snap(string Name, DateTime Date, long? KeepUntil, string? Tier, long? Referenced = null, long? Written = null);
