#:property PublishAot=true
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property Version=1.0.0

// zfs-hold-tight - defender-side GFS retention for TrueNAS archive/* datasets.
//
// Replication pulls ONLY daily snapshots from merritt (Approach 4 in
// zfs/backup-history.md). This tool thins those dailies into a tiered retention (daily/weekly/monthly)
// keep set - newest N dailies + newest-per-week + newest-per-month - tagging each with
// <ns>:keep_until (and <ns>:tier = daily|weekly|monthly, for audit; <ns> = propertyNamespace) and destroying
// dailies past expiry. It never creates snapshots.
//
// SAFETY: it deletes only when the dataset's feed is provably healthy; otherwise it
// HOLDs (deletes nothing) and alerts. Full contract: docs/SAFETY.md.
//   Run-level guards, evaluated INDEPENDENTLY PER DATASET (SKIP that dataset on failure):
//   recent 7-day window complete, newest daily fresh (<=26h, guardMaxDailyAgeHours),
//   clock sane, no sudden data shrinkage (guardMaxDailyShrinkPct — mass deletion), no
//   excessive churn (guardMaxDailyChurnPct — mass rewrite/encryption), volume
//   circuit-breaker (<=5/run w/o --force). Each guard threshold defaults globally but
//   may be overridden per retention policy.
//   Per-snapshot: never the newest/base, a week/month keeper still WITHIN its retention,
//   an uncovered daily, a held snapshot, or a non-_daily snapshot. (A keeper past its
//   retention ages out — see docs/SAFETY.md.)
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
//        zfs-hold-tight --version

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Reflection;

// Tool version: the base Version (#:property) plus the git short SHA the SDK appends to
// InformationalVersion when built with -p:SourceRevisionId=<sha> (see the build recipe).
// Stamped into every run header so the logs record exactly which build ran. (Pattern
// borrowed from the dotnet-inspect peer repo — works under NativeAOT.)
string Version = GetVersion();
if (args.Contains("--version")) { Console.WriteLine($"zfs-hold-tight {Version}"); return 0; }

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
int MinNewerHolds = 2;          // a non-keeper daily drops once this many held keepers are newer than it
try
{
    if (cfg.TryGetProperty("minNewerHolds", out var mnh))
    {
        MinNewerHolds = mnh.GetInt32();
        if (MinNewerHolds < 1) throw new ArgumentException("minNewerHolds must be >= 1");
    }
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
// Logs + lock are shared runtime *data*, not root-only *code*: create them group-writable
// (0664) so both root (cron) and the truenas_admin operator can append/rotate them. The
// binary + config stay root-owned; only this data dir is shared (deploy it 2775 root:operator).
const UnixFileMode SharedDataMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
    | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;

string? hcUrl = Environment.GetEnvironmentVariable("ZFS_HOLD_TIGHT_HC_URL");
if (string.IsNullOrEmpty(hcUrl) && cfg.TryGetProperty("healthchecksUrl", out var hu))
    hcUrl = hu.GetString();

// Healthchecks is optional: with no URL configured (and no env override), skip pings
// entirely rather than baking in a site URL. Set healthchecksUrl in config to enable.
bool noHc = args.Contains("--no-hc") || string.IsNullOrEmpty(hcUrl);

// Namespace for this tool's ZFS user-properties and hold tags (<ns>:keep_until,
// <ns>:tier, <ns>:monthly, <ns>:weekly, <ns>:adhoc, <ns>:worry). Configurable so the source carries
// no site identity; defaults to "zht".
string ns = (cfg.TryGetProperty("propertyNamespace", out var pnEl) ? pnEl.GetString() : null) ?? "zht";
if (!Regex.IsMatch(ns, @"^[A-Za-z0-9_.-]+$"))
{ Console.Error.WriteLine($"FATAL: propertyNamespace '{ns}' must be letters/digits/._- (no colon)"); configDoc?.Dispose(); return 2; }
string tagMonthly = $"{ns}:monthly", tagWeekly = $"{ns}:weekly", tagAdhoc = $"{ns}:adhoc", propKeep = $"{ns}:keep_until", propTier = $"{ns}:tier";

// Resolve zfs/curl to absolute trusted paths rather than trusting the (root) cron's PATH —
// a writable earlier PATH element would otherwise be root code execution. Override with
// zfsPath / curlPath in config for non-standard layouts.
string ResolveBin(string name, string cfgKey, string[] candidates)
{
    var configured = cfg.TryGetProperty(cfgKey, out var be) ? be.GetString() : null;
    if (!string.IsNullOrEmpty(configured)) return configured;
    foreach (var c in candidates) if (File.Exists(c)) return c;
    return name;  // last resort: ambient PATH
}
string zfsBin = ResolveBin("zfs", "zfsPath", new[] { "/usr/sbin/zfs", "/sbin/zfs", "/usr/local/sbin/zfs", "/bin/zfs" });
string curlBin = ResolveBin("curl", "curlPath", new[] { "/usr/bin/curl", "/bin/curl", "/usr/local/bin/curl" });
if (zfsBin == "zfs")
    Console.Error.WriteLine("WARNING: zfs not found at a standard absolute path; using PATH. Set \"zfsPath\" in config to harden.");

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
        int minNewerHolds = p.TryGetProperty("minNewerHolds", out var mn) ? mn.GetInt32() : MinNewerHolds;

        if (daily <= 0 || weekly <= 0 || monthly <= 0)
            throw new ArgumentException($"Policy '{policy.Name}': all retention days must be positive");
        if (minNewerHolds < 1)
            throw new ArgumentException($"Policy '{policy.Name}': minNewerHolds must be >= 1");

        policies[policy.Name] = new Retention(daily, weekly, monthly, minNewerHolds);
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
// --dataset must name a configured dataset. Validate here, BEFORE any command dispatches,
// so a missing value can never silently widen a destructive run to all datasets and an
// unknown value can never look like a successful no-op (--doctor/--fix/--hold-*).
string? datasetFilter = null;
int dsIdx = Array.IndexOf(args, "--dataset");
if (dsIdx >= 0)
{
    if (dsIdx + 1 >= args.Length || args[dsIdx + 1].StartsWith("--"))
    { Console.Error.WriteLine("--dataset requires a dataset name"); configDoc?.Dispose(); return 2; }
    datasetFilter = args[dsIdx + 1];
    if (!retention.ContainsKey(datasetFilter))
    { Console.Error.WriteLine($"unknown dataset '{datasetFilter}'"); configDoc?.Dispose(); return 2; }
}

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
        var lockOpts = new FileStreamOptions {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) lockOpts.UnixCreateMode = SharedDataMode;
        runLock = new FileStream(lockPath, lockOpts);
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
string? only = datasetFilter;   // already validated against config above

Log($"=== zfs-hold-tight {Version} {now:yyyy-MM-dd HH:mm:ss}Z  mode={(prune ? "PRUNE" : "dry-run")}{(force ? " --force" : "")} ===");
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
        // ListDailies already logged the ERROR detail; emit the dataset verdict.
        Log($"{ds}: SKIP - zfs list failed (cannot verify feed health)");
        held.Add($"{ds}: zfs list error");
        continue;
    }
    if (snaps.Count == 0) { Log($"{ds}: no daily snapshots, skip"); continue; }
    var gd = guardsByDataset[ds];   // this dataset's effective guard thresholds

    // ---- keepers + desired keep_until ----
    var weekKeeper  = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                           .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
    var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month)).Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
    var newest = snaps.MaxBy(s => s.Date)!.Name;
    // keepers = weekKeeper ∪ monthKeeper: exactly the snapshots that carry a real hold
    // (weekly with <ns>:weekly, monthly with <ns>:monthly). A daily is held by none of them.
    var keeperNames = weekKeeper.Union(monthKeeper).ToHashSet();
    // Two-holds invariant: a non-keeper daily is droppable only once at least minNewerHolds
    // held keepers are strictly NEWER than it. Equivalent clean cutoff: sort keeper dates DESC;
    // holdFrontier = the Nth-newest keeper's date; a non-keeper daily s is doomed iff
    // s.Date < holdFrontier. Fewer than N keepers ⇒ nothing droppable yet (frontier = MinValue).
    // The daily's own week+month grandfathers are strictly newer, so the default of 2 subsumes
    // the old week/month coverage test plus a buffer.
    int destroyDepth = ret.MinNewerHolds;
    var keeperDates = snaps.Where(s => keeperNames.Contains(s.Name)).Select(s => s.Date)
                           .OrderByDescending(dt => dt).ToList();
    var holdFrontier = keeperDates.Count >= destroyDepth ? keeperDates[destroyDepth - 1] : DateTime.MinValue;
    // Only keepers have a date-based expiry (their 5-year grandfather retention); a plain daily
    // is governed by the frontier, not a date, so it returns MaxValue here (never expires by date).
    DateTime Desired(Snap s)
    {
        bool isWeek = weekKeeper.Contains(s.Name), isMonth = monthKeeper.Contains(s.Name);
        if (!isWeek && !isMonth) return DateTime.MaxValue;
        var k = DateTime.MinValue;
        if (isWeek)  k = Later(k, s.Date.AddDays(ret.WeeklyKeepDays));
        if (isMonth) k = Later(k, s.Date.AddDays(ret.MonthlyKeepDays));
        return k;
    }

    // Current holds on this dataset — used to (a) honor guard 12 (never delete a held
    // snapshot) and (b) tell our managed <ns>:monthly hold from foreign/defensive ones.
    // A holds-list failure means we can't verify guard 12, so SKIP rather than guess.
    var holdMap = LoadHolds(snaps.Select(s => s.Name).ToList());
    if (holdMap is null)
    {
        Log($"{ds}: ERROR - zfs holds failed (cannot verify holds)");
        Log($"{ds}: SKIP - cannot verify holds (no deletions)");
        held.Add($"{ds}: zfs holds error");
        continue;
    }
    bool IsForeignHeld(string name) =>
        holdMap.TryGetValue(name, out var tags) && tags.Any(t => t != tagMonthly && t != tagWeekly);

    // Keeper/hold summary — hoisted above the worry paths so the WORRY lines and the normal
    // line all report the same numbers. keepersHeld counts computed keepers that ALREADY carry
    // a real <ns>:weekly / <ns>:monthly hold on disk; on a dry-run before the first prune the
    // weekly holds are still pending, so keepersHeld < keeperNames.Count — that gap is itself
    // the signal that a real run still has holds to place.
    int mCount = monthKeeper.Count;
    int wCount = weekKeeper.Count(w => !monthKeeper.Contains(w));
    int keepersHeld = keeperNames.Count(k =>
        holdMap.TryGetValue(k, out var kt) && (kt.Contains(tagMonthly) || kt.Contains(tagWeekly)));
    string tail = holdFrontier == DateTime.MinValue ? "—" : holdFrontier.ToString("yyyy-MM-dd");

    // ---- run-level guards. On a worry, protection only ever INCREASES: still hold the
    //      monthly keepers (even the suspect newest — a false positive costs nothing, and
    //      we don't want to skip the hold and lose the chance later) and record a worry
    //      breadcrumb on the tip — but never release a hold or delete anything. ----
    string? guard = HealthGuard(snaps, ret, gd);
    if (guard is not null)
    {
        Log($"{ds}: WORRY - {guard} ({keepersHeld}/{keeperNames.Count} keepers held)");
        if (prune)
        {
            // Proactively hold EVERY keeper (monthly with <ns>:monthly, weekly with <ns>:weekly).
            // Protection only ever increases on a worry; never release or delete here.
            foreach (var mk in monthKeeper)
                if (!(holdMap.TryGetValue(mk, out var mt) && mt.Contains(tagMonthly)))
                {
                    var (hc, _, he) = Exec("zfs", "hold", tagMonthly, mk);
                    if (hc == 0) Log($"  HOLD:ADD {mk} (protected during worry)");
                    else if (!(he.Contains("already") || he.Contains("exists")))
                        Log($"    HOLD:ADD {Short(mk)} FAILED: {he.Trim()}");
                }
            foreach (var wk in weekKeeper.Where(w => !monthKeeper.Contains(w)))
                if (!(holdMap.TryGetValue(wk, out var wt) && wt.Contains(tagWeekly)))
                {
                    var (hc, _, he) = Exec("zfs", "hold", tagWeekly, wk);
                    if (hc == 0) Log($"  HOLD:ADD {wk} (protected during worry)");
                    else if (!(he.Contains("already") || he.Contains("exists")))
                        Log($"    HOLD:ADD {Short(wk)} FAILED: {he.Trim()}");
                }
            var (wc, _, we) = Exec("zfs", "set", $"{ns}:worry={now:yyyy-MM-dd} {guard}", newest);
            if (wc == 0) Log($"  META:ADD {newest} {ns}:worry={now:yyyy-MM-dd} {guard}");
            else Log($"    META:ADD {Short(newest)} FAILED: {we.Trim()}");
        }
        Log($"{ds}: SKIP - guard tripped (keepers protected, no deletions)");
        held.Add($"{ds}: {guard}");
        continue;
    }

    // ---- PLAN (read-only): compute the doomed set; mutate nothing yet ----
    var doomed = new List<string>();
    foreach (var s in snaps.OrderBy(s => s.Date))
    {
        if (s.Name == newest) continue;                     // never the base (guard 7)
        if (IsForeignHeld(s.Name))                          // guard 12: held => never delete, skip cleanly
        { if (verbose) Log($"  {ds}: skip {Short(s.Name)} - held ({string.Join(",", holdMap[s.Name])})"); continue; }
        if (keeperNames.Contains(s.Name))
        {
            // A held week/month keeper ages out only past its own grandfather retention
            // (weekly/monthly keep-days); until then it is kept, invariant or not.
            if (now <= Desired(s)) continue;
        }
        else
        {
            // Two-holds invariant: keep a non-keeper daily until the derived destroy depth
            // of held keepers is strictly newer than it (its date drops below the frontier).
            // The frontier subsumes the old week+month coverage test (its own grandfathers are
            // strictly newer) plus a buffer, so no coverage hole can open early.
            if (s.Date >= holdFrontier) continue;
        }
        doomed.Add(s.Name);
    }

    // ---- volume circuit-breaker: caps DESTROYS only. Keeper hold-tagging and demotion
    //      cleanup below still run on a capped dataset — "on a worry, protection only
    //      ever increases" (same principle as the HealthGuard branch above). Gating Promote
    //      behind this guard would leave newly-due weekly/monthly keepers unheld for as long
    //      as the dataset stays capped: a protection regression masquerading as a safety win.
    bool volumeCapped = doomed.Count > gd.MaxDeletePerRun && !force;
    if (volumeCapped)
        Log($"{ds}: WORRY - would delete {doomed.Count} > cap {gd.MaxDeletePerRun}; {keepersHeld}/{keeperNames.Count} keepers held, daily tail to {tail} (rerun with --force if intended)");

    // ---- tag keepers / clean up demotions (prune only) ----
    if (prune)
    {
        var promoteErrors = new List<string>();  // snapshots whose metadata/hold write failed
        foreach (var s in snaps.OrderBy(s => s.Date))
        {
            string tier = monthKeeper.Contains(s.Name) ? "monthly" : weekKeeper.Contains(s.Name) ? "weekly" : "daily";
            // Our keeper holds currently on this snapshot (from the load-time holds list), so
            // Promote can add-then-release across a tier change without a transient unheld gap.
            var heldKeeperTags = new HashSet<string>();
            if (holdMap.TryGetValue(s.Name, out var sht))
            {
                if (sht.Contains(tagMonthly)) heldKeeperTags.Add(tagMonthly);
                if (sht.Contains(tagWeekly))  heldKeeperTags.Add(tagWeekly);
            }
            if (!Promote(s, Desired(s), tier, heldKeeperTags)) promoteErrors.Add(Short(s.Name));
        }
        // Metadata/hold write failures SKIP the dataset (delete nothing) and alert.
        if (promoteErrors.Count > 0)
        {
            // The per-op `… FAILED` lines above are the reason; emit the verdict.
            Log($"{ds}: SKIP - metadata/hold write failed (no deletions)");
            held.Add($"{ds}: metadata/hold failures");
            metadataErrors.AddRange(promoteErrors.Select(n => $"  {ds}@{n}"));
            continue;
        }
    }

    if (volumeCapped)
    {
        Log($"{ds}: SKIP - volume circuit-breaker (no deletions)");
        held.Add($"{ds}: volume {doomed.Count}>cap"); continue;
    }

    int kept = snaps.Count - doomed.Count;
    totalKeep += kept; totalDoom += doomed.Count;
    string verb = prune ? "destroy" : "would destroy";
    string rng = doomed.Count > 0 ? $" [{Short(doomed[0])} .. {Short(doomed[^1])}]" : "";
    // keeper tag breakdown (mCount/wCount/tail hoisted above the worry paths) and the daily-tail
    // frontier: dailies newer than it survive (< destroyDepth held keepers ahead).
    Log($"{ds}: {snaps.Count} snaps -> keep {kept} ({mCount} monthly + {wCount} weekly held; daily tail: >={destroyDepth} newer holds, to {tail}), {verb} {doomed.Count}{rng}");
    foreach (var name in doomed)
    {
        if (!prune) { if (verbose) Log($"  WOULD-DESTROY {name}"); continue; }

        // Release ANY of our managed holds (<ns>:monthly and/or <ns>:weekly) before destroy,
        // but ONLY for snapshots that actually carry one (an expired keeper — the tag Promote
        // gave it this run — or one held at load time) — not the common plain-daily case.
        // (Guard 12 already excluded foreign-held snapshots.)
        var toRelease = new HashSet<string>();
        if (monthKeeper.Contains(name)) toRelease.Add(tagMonthly);           // Promote holds monthly (precedence)
        else if (weekKeeper.Contains(name)) toRelease.Add(tagWeekly);        // weekly-only keeper holds weekly
        if (holdMap.TryGetValue(name, out var ht))
        {
            if (ht.Contains(tagMonthly)) toRelease.Add(tagMonthly);
            if (ht.Contains(tagWeekly))  toRelease.Add(tagWeekly);
        }
        foreach (var tag in toRelease)
        {
            var (rc, _, re) = Exec("zfs", "release", tag, name);
            if (rc != 0 && !re.Contains("no such hold") && !re.Contains("not found"))
                Log($"    HOLD:REMOVE {Short(name)} FAILED: {re.Trim()}");
            else Log($"  HOLD:REMOVE {name} (expired keeper, before destroy)");
        }

        destroyAttempted++;
        var (code, _, err) = Exec("zfs", "destroy", name);
        if (code != 0) { destroyFails++; Log($"    SNAP:DESTROY {Short(name)} FAILED: {err.Trim()}"); }
        else { destroySucceeded++; Log($"  SNAP:DESTROY {name}"); }
    }
}

bool trouble = held.Count > 0 || metadataErrors.Count > 0 || (prune && destroyFails > 0);
Log($"\n{(prune ? "PRUNED" : "DRY RUN")}: kept {totalKeep}, {(prune ? $"destroyed {destroySucceeded}/{destroyAttempted}" : $"would destroy {totalDoom}")}"
    + (held.Count > 0 ? $", {held.Count} SKIPPED" : "")
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
    Log($"=== zfs-hold-tight {Version} {now:yyyy-MM-dd HH:mm:ss}Z  mode=hold-adhoc ===");

    if (!snap.StartsWith(DatasetPrefix) || !snap.Contains('@'))
    { Log($"FAIL: snapshot '{snap}' must be under '{DatasetPrefix}' and name a snapshot (contain '@')"); FlushLog(lines); return 2; }

    var (c, _, e) = Exec("zfs", "hold", tagAdhoc, snap);
    if (c != 0) { Log($"FAIL: {e.Trim()}"); FlushLog(lines); return 1; }
    Log($"  HOLD:ADD {snap} ({tagAdhoc}, adhoc)");

    // Verify hold was created
    var (vc, vo, _) = Exec("zfs", "holds", snap);
    if (vc == 0 && vo.Contains(tagAdhoc))
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
    Log($"=== zfs-hold-tight {Version} {now:yyyy-MM-dd HH:mm:ss}Z  mode=check-holds ===");

    // A held snapshot is "stale" once it is older than ITS dataset's longest retention
    // (max of weekly/monthly keep-days) — checked per dataset, since retention varies by
    // policy. A flat threshold would false-alarm on long policies and miss short ones.
    var dateRe = new Regex(@"@autosnap_(\d{4}-\d{2}-\d{2})");
    var stale = new List<string>();
    bool anyFail = false;
    foreach (var (ds, ret) in retention)
    {
        var (code, snaps) = ListDailies(ds);
        if (code != 0) { Log($"  WARNING: {ds}: zfs list failed"); anyFail = true; continue; }
        var holdMap = LoadHolds(snaps.Select(s => s.Name).ToList());
        if (holdMap is null) { Log($"  WARNING: {ds}: zfs holds failed"); anyFail = true; continue; }
        foreach (var (snap, tags) in holdMap)
        {
            // Judge each hold against ITS tier's retention: a <ns>:weekly hold is stale past
            // weeklyKeepDays, a <ns>:monthly hold past monthlyKeepDays. Other holds (adhoc /
            // foreign) are judged against the longest retention, as before.
            int days = tags.Contains(tagMonthly) ? ret.MonthlyKeepDays
                     : tags.Contains(tagWeekly)  ? ret.WeeklyKeepDays
                     : Math.Max(ret.WeeklyKeepDays, ret.MonthlyKeepDays);
            var m = dateRe.Match(snap);
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var snapDate))
                continue;
            if (snapDate < now.AddDays(-days))
                stale.Add($"{ds}: hold on {Short(snap)} exceeds its {days}d retention");
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
    FlushLog(lines);
    if (!noHc && trouble)
        HcPing(hcUrl + "/fail", stale.Count > 0 ? "stale holds:\n" + string.Join("\n", stale) : "check-holds: a zfs holds call failed");
    return trouble ? 1 : 0;
}

// ---- doctor: validate hold strategy ----
int Doctor(string? datasetFilter)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    Log($"=== zfs-hold-tight {Version} {now:yyyy-MM-dd HH:mm:ss}Z  mode=doctor ===");

    int missing = 0, protected_ = 0, adhoc_ = 0, orphaned = 0, errors = 0;
    foreach (var (ds, _) in retention)
    {
        if (datasetFilter is not null && ds != datasetFilter) continue;
        var (code, snaps) = ListDailies(ds);
        if (code != 0) { Log($"{ds}: ERROR - zfs list failed, cannot validate holds"); errors++; continue; }
        if (snaps.Count == 0) { Log($"{ds}: no snapshots"); continue; }

        // Find keepers: monthlies (<ns>:monthly) and weekly-only keepers (<ns>:weekly).
        var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month))
                              .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
        var weekKeeper = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                              .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
        var weekOnly = weekKeeper.Where(w => !monthKeeper.Contains(w)).ToHashSet();

        // Check which snapshots have holds
        var holdMap = LoadHolds(snaps.Select(s => s.Name).ToList());
        if (holdMap is null) { Log($"{ds}: ERROR - zfs holds failed, hold status unknown"); errors++; continue; }

        // Report monthly + weekly keeper status (each expects its matching hold)
        Log($"{ds}: {monthKeeper.Count} monthlies, {weekOnly.Count} weeklies");
        foreach (var monthly in monthKeeper.OrderByDescending(s => s))
        {
            if (holdMap.TryGetValue(monthly, out var tags) && tags.Contains(tagMonthly))
            {
                Log($"  ✓ {Short(monthly)} ({tagMonthly})");
                protected_++;
            }
            else
            {
                Log($"  ✗ {Short(monthly)} MISSING HOLD");
                missing++;
            }
        }
        foreach (var weekly in weekOnly.OrderByDescending(s => s))
        {
            if (holdMap.TryGetValue(weekly, out var tags) && tags.Contains(tagWeekly))
            {
                Log($"  ✓ {Short(weekly)} ({tagWeekly})");
                protected_++;
            }
            else
            {
                Log($"  ✗ {Short(weekly)} MISSING HOLD");
                missing++;
            }
        }

        // Report other holds — a keeper carrying its own <ns>:weekly/<ns>:monthly hold is
        // expected, so exclude both keeper sets here; only genuinely unexpected holds remain.
        foreach (var (snap, tags) in holdMap)
        {
            if (!monthKeeper.Contains(snap) && !weekOnly.Contains(snap) && snap.Contains("_daily"))
            {
                if (tags.Contains(tagAdhoc))
                {
                    Log($"  ℹ {Short(snap)} ({tagAdhoc} - defensive)");
                    adhoc_++;
                }
                else if (!tags.Any(t => t.StartsWith($"{ns}:")))
                {
                    Log($"  ? {Short(snap)} has foreign holds: {string.Join(",", tags)}");
                    orphaned++;
                }
            }
        }
    }

    Log($"\nSummary: {protected_} keepers protected, {missing} missing, {adhoc_} adhoc, {orphaned} unexpected"
        + (errors > 0 ? $", {errors} datasets unverified (zfs errors)" : ""));
    if (missing == 0 && orphaned == 0 && errors == 0)
        Log("✓ Hold strategy validated");
    else
        Log($"⚠ Issues: {missing} missing holds, {orphaned} unexpected, {errors} unverified");

    FlushLog(lines);
    return (missing > 0 || errors > 0) ? 1 : 0;
}

// ---- fix: create missing managed keeper holds ----
int Fix(string? datasetFilter)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    Log($"=== zfs-hold-tight {Version} {now:yyyy-MM-dd HH:mm:ss}Z  mode=fix ===");

    int fixed_ = 0, errors = 0;
    foreach (var (ds, _) in retention)
    {
        if (datasetFilter is not null && ds != datasetFilter) continue;
        var (code, snaps) = ListDailies(ds);
        if (code != 0) { Log($"  ✗ {ds}: zfs list failed, skipping"); errors++; continue; }
        if (snaps.Count == 0) continue;

        // Find keepers, load current holds once, and hold only the ones still MISSING their
        // matching tag (batched — `zfs hold <tag> <snap>...` takes multiple snapshots).
        var monthKeeper = snaps.GroupBy(s => (s.Date.Year, s.Date.Month))
                              .Select(g => g.MaxBy(s => s.Date)!.Name).ToHashSet();
        var weekOnly = snaps.GroupBy(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date)))
                            .Select(g => g.MaxBy(s => s.Date)!.Name).Where(w => !monthKeeper.Contains(w)).ToHashSet();
        var holdMap = LoadHolds(snaps.Select(s => s.Name).ToList());
        if (holdMap is null) { Log($"  ✗ {ds}: zfs holds failed, skipping"); errors++; continue; }

        // Hold every keeper in `keepers` that lacks `tag`, batched with a one-by-one retry.
        void HoldMissing(string tag, IEnumerable<string> keepers)
        {
            var toHold = keepers.Where(k => !(holdMap.TryGetValue(k, out var t) && t.Contains(tag))).ToList();
            foreach (var k in keepers.Except(toHold)) Log($"  ✓ {ds}/{Short(k)} (already held)");
            for (int i = 0; i < toHold.Count; i += 256)
            {
                var chunk = toHold.GetRange(i, Math.Min(256, toHold.Count - i));
                var a = new List<string> { "hold", tag }; a.AddRange(chunk);
                var (c, _, _) = Exec("zfs", a.ToArray());
                if (c == 0) { foreach (var k in chunk) { Log($"  HOLD:ADD {k} (fix)"); fixed_++; } continue; }
                // Chunk failed — retry one-by-one so the report names the actual bad snapshot(s)
                // (the fast batched path stays the common case).
                foreach (var k in chunk)
                {
                    var (cc, _, ee) = Exec("zfs", "hold", tag, k);
                    if (cc == 0 || ee.Contains("already") || ee.Contains("exists")) { Log($"  HOLD:ADD {k} (fix)"); fixed_++; }
                    else { Log($"  ✗ {ds}/{Short(k)}: {ee.Trim()}"); errors++; }
                }
            }
        }
        HoldMissing(tagMonthly, monthKeeper);
        HoldMissing(tagWeekly, weekOnly);
    }

    Log($"Fixed {fixed_} holds" + (errors > 0 ? $", {errors} errors" : ""));
    FlushLog(lines);
    return errors > 0 ? 1 : 0;
}

// ---- hold latest of tier ----
int HoldLatestTier(string tier, string? datasetFilter, bool noHc)
{
    var lines = new List<string>();
    void Log(string m) { Console.WriteLine(m); lines.Add(m); }
    Log($"=== zfs-hold-tight {Version} {now:yyyy-MM-dd HH:mm:ss}Z  mode=hold-latest-tier tier={tier} ===");

    int errors = 0;
    foreach (var (ds, _) in retention)
    {
        if (datasetFilter is not null && ds != datasetFilter) continue;
        var (code, snaps) = ListDailies(ds);
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

        var (c, _, e) = Exec("zfs", "hold", tagAdhoc, target.Name);
        if (c == 0)
            Log($"  HOLD:ADD {target.Name} (adhoc, {tier})");
        else if (e.Contains("already") || e.Contains("exists"))
            Log($"  ✓ {ds}/{Short(target.Name)} ({tier}) - already held");
        else
        { Log($"  ✗ {ds}/{Short(target.Name)}: {e.Trim()}"); errors++; }
    }

    FlushLog(lines);
    if (!noHc) HcPing(errors > 0 ? hcUrl + "/fail" : hcUrl + "/hold-latest-" + tier,
                      errors > 0 ? $"hold-latest-{tier}: {errors} errors" : "ok");
    return errors > 0 ? 1 : 0;
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

    // population note (informational): under the two-holds invariant nothing is droppable
    // until the derived destroy depth of held keepers exists (a keeper ≈ one per ISO-week/month).
    int keeperWeeks = snaps.Select(s => (ISOWeek.GetYear(s.Date), ISOWeek.GetWeekOfYear(s.Date))).Distinct().Count();
    int destroyDepth = ret.MinNewerHolds;
    if (keeperWeeks < destroyDepth)
        Log($"  note: only ~{keeperWeeks} held keeper(s) so far, need {destroyDepth} newer before any daily drops");
    return null;
}

// ---- helpers ----
(int, List<Snap>) ListDailies(string ds)
{
    // -p: exact bytes for referenced/written (the shrinkage + churn guards need raw numbers).
    var (code, outp, err) = Exec("zfs", "list", "-Hp", "-t", "snapshot", "-o", $"name,{propKeep},{propTier},referenced,written", "-d", "1", ds);
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
// Only keepers (weekly/monthly) carry <ns>:tier + <ns>:keep_until tags; a plain
// daily is implicitly daily and stays untagged. This keeps the first run cheap (~24
// writes, not ~280) and is self-correcting: a snapshot that was a keeper last run but
// is now superseded (demotion) has its tags inherited away and its monthly hold released
// — without that release a demoted monthly's hold would later block its own destroy.
bool Promote(Snap s, DateTime keepUntil, string tier, HashSet<string> heldKeeperTags)
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
            var (c, _, e) = Exec("zfs", "inherit", propTier, s.Name);
            if (c != 0) { Log($"    META:REMOVE {Short(s.Name)} FAILED: {e.Trim()}"); success = false; }
            else Log($"  META:REMOVE {s.Name} (demoted to daily)");
        }
        if (s.KeepUntil != null)
        {
            var (c, _, e) = Exec("zfs", "inherit", propKeep, s.Name);
            if (c != 0) { Log($"    META:REMOVE {Short(s.Name)} keep_until FAILED: {e.Trim()}"); success = false; }
        }
        if (success)                          // demoted keeper: drop the now-unneeded hold(s) last
            foreach (var tag in heldKeeperTags)
            {
                var (rc, _, re) = Exec("zfs", "release", tag, s.Name);
                if (rc != 0 && !re.Contains("no such hold") && !re.Contains("not found"))
                { Log($"    HOLD:REMOVE {Short(s.Name)} FAILED: {re.Trim()}"); success = false; }
                else Log($"  HOLD:REMOVE {s.Name} (demoted to daily)");
            }
        return success;
    }

    // Set keep_until and/or tier in a single `zfs set` (it accepts multiple prop=value args).
    long want = ((DateTimeOffset)keepUntil).ToUnixTimeSeconds();
    var props = new List<string>();
    if (s.KeepUntil != want) props.Add($"{propKeep}={want}");
    if (s.Tier != tier)      props.Add($"{propTier}={tier}");   // weekly|monthly — highest qualifying tier
    if (props.Count > 0)
    {
        // ADD when the snapshot carried no tags before; UPDATE when an existing keeper's
        // tier/keep_until changed (e.g. a weekly survivor promoted to monthly). The prior
        // values come free from the batched `zfs list`, so the distinction costs no extra I/O.
        string metaVerb = s.Tier is null && s.KeepUntil is null ? "ADD" : "UPDATE";
        var setArgs = new List<string> { "set" }; setArgs.AddRange(props); setArgs.Add(s.Name);
        var (c, _, e) = Exec("zfs", setArgs.ToArray());
        if (c != 0) { Log($"    META:{metaVerb} {Short(s.Name)} FAILED: {e.Trim()}"); success = false; }
        else Log($"  META:{metaVerb} {s.Name} ({tier}): {string.Join(" ", props)}");
    }
    // Hold invariant: held ⇔ keeper, with the tag matching the tier (monthly→<ns>:monthly,
    // weekly→<ns>:weekly). ADD the wanted tag first (if missing), and only once it is confirmed
    // present RELEASE any wrong-tier tag — add-then-release, so a snapshot is never transiently
    // unheld across a tier change (e.g. weekly→monthly).
    string wantTag  = tier == "monthly" ? tagMonthly : tagWeekly;
    string otherTag = tier == "monthly" ? tagWeekly  : tagMonthly;
    bool wantHeld = heldKeeperTags.Contains(wantTag);
    if (!wantHeld)
    {
        var (c, _, e) = Exec("zfs", "hold", wantTag, s.Name);
        if (c == 0)
            { Log($"  HOLD:ADD {s.Name} ({tier} keeper, prune ~{keepUntil:yyyy-MM-dd})"); wantHeld = true; }
        else if (e.Contains("already") || e.Contains("exists"))
            wantHeld = true;   // idempotent: hold already present (race)
        else
            { Log($"    HOLD:ADD {Short(s.Name)} FAILED: {e.Trim()}"); success = false; }
    }
    if (wantHeld && heldKeeperTags.Contains(otherTag))   // wrong-tier hold left over → release it
    {
        var (rc, _, re) = Exec("zfs", "release", otherTag, s.Name);
        if (rc != 0 && !re.Contains("no such hold") && !re.Contains("not found"))
            { Log($"    HOLD:REMOVE {Short(s.Name)} FAILED: {re.Trim()}"); success = false; }
        else
            Log($"  HOLD:REMOVE {s.Name} ({(tier == "monthly" ? "weekly→monthly" : "monthly→weekly")})");
    }
    return success;
}

// snapshot -> set of hold tags. `zfs holds` takes SNAPSHOT args (not a dataset; `-r` is
// for descending from a snapshot), so we pass the dataset's snapshot names, batched in
// chunks to stay under argv limits — one or a few commands per dataset, not one per snap.
// Returns null on any `zfs holds` failure (distinct from "no holds") so a destructive
// caller can refuse to proceed without knowing the hold state.
Dictionary<string, HashSet<string>>? LoadHolds(IReadOnlyList<string> snapNames)
{
    var map = new Dictionary<string, HashSet<string>>();
    const int chunk = 256;
    for (int i = 0; i < snapNames.Count; i += chunk)
    {
        var a = new List<string> { "holds", "-H" };
        for (int j = i; j < Math.Min(i + chunk, snapNames.Count); j++) a.Add(snapNames[j]);
        var (c, outp, _) = Exec("zfs", a.ToArray());
        if (c != 0) return null;
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
        // Create the day's log group-writable so the other principal (operator vs. cron) can
        // append to it too; harmless if it already exists.
        if (!File.Exists(logPath))
        {
            var newOpts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) newOpts.UnixCreateMode = SharedDataMode;
            try { using (new FileStream(logPath, newOpts)) { } }
            catch (IOException) { /* another run created it first — fine */ }
        }
        File.AppendAllText(logPath, string.Join("\n", logLines ?? lines) + "\n");
    }
    catch { /* best effort - e.g. a non-root run whose log dir isn't shared can't write */ }
}

(int code, string outp, string err) Exec(string file, params string[] a)
{
    if (file == "zfs") file = zfsBin;            // absolute trusted path, not ambient PATH
    else if (file == "curl") file = curlBin;
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

static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;
static string Short(string snap) => snap[(snap.IndexOf('@') + 1)..];

// InformationalVersion is "<Version>+<full-sha>" when built with -p:SourceRevisionId;
// shorten the SHA to 7 chars (e.g. "1.0.0+ee4f11a"). Falls back to the assembly version.
static string GetVersion()
{
    var asm = typeof(Snap).Assembly;
    var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (string.IsNullOrEmpty(info)) return asm.GetName().Version?.ToString() ?? "0.0.0";
    int plus = info.IndexOf('+');
    return (plus > 0 && info.Length > plus + 8) ? info[..(plus + 8)] : info;
}

record Retention(int DailyKeepDays, int WeeklyKeepDays, int MonthlyKeepDays, int MinNewerHolds);
record Guards(int MaxDeletePerRun, int RecentWindowDays, int MaxDailyAgeHours, int MaxDailyShrinkPct, int MaxDailyChurnPct);
record Snap(string Name, DateTime Date, long? KeepUntil, string? Tier, long? Referenced = null, long? Written = null);
