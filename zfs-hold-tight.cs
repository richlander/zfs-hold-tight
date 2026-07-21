#:property PublishAot=true
#:property InvariantGlobalization=true
#:property StripSymbols=true
#:property Version=1.0.0
#:include ./Types.cs
#:include ./Utils.cs
#:include ./Zfs.cs
#:include ./Guards.cs
#:include ./Prune.cs
#:include ./Doctor.cs
#:include ./Summary.cs
#:include ./Fix.cs
#:include ./Holds.cs

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
//   recent 7-day window complete (frontier-aware — a day already covered by the
//   two-holds invariant isn't a gap, so this tool's own routine pruning can't trip its
//   own feed-gap check; this guard protects only this tool's pruning decision, not
//   replication health in general — see docs/SAFETY.md guard 1 for the scope), newest
//   daily fresh (<=26h, guardMaxDailyAgeHours),
//   clock sane, no sudden data shrinkage (guardMaxDailyShrinkPct — mass deletion), no
//   excessive churn (guardMaxDailyChurnPct — mass rewrite/encryption), volume
//   circuit-breaker (<=14/run w/o --force — 2x the 7-day ISO-week cadence a weekly
//   keeper promotes on, since that promotion is a once-a-week batch event, not a smooth
//   daily one). Accepted tradeoff: 14 also raises the bar for catching a non-routine
//   steady-state anomaly (misconfig/bug), not just routine weekly batches — deliberate,
//   since the old cap of 5 never cleared a real routine week either, so it only ever
//   fired on the routine case. See docs/SAFETY.md guard 6. Separately, a cold-start
//   floor (any nonzero doomed count, gated on prior <ns>:tier history) guarantees a
//   reviewed --force on a dataset's first-ever prune regardless of the 14 cap — see
//   docs/SAFETY.md guard 7. Each guard threshold defaults globally but may be
//   overridden per retention policy.
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
// This is a file-based app split across multiple .cs files via #:include (see the
// directives above) -- zfs-hold-tight.cs stays the one entry point (arg parsing, config
// loading, dispatch); Types/Utils/Zfs/Guards hold shared state and helpers, and each
// command (Prune/Doctor/Summary/Fix/Holds) lives in its own file as a method on the
// partial `Commands` class. `dotnet run`/`dotnet publish` both still just target this
// file — the included files compile into the SAME single AOT binary, not a separate
// assembly (unlike `#:ref`, which would).
//
// Usage: zfs-hold-tight [--prune] [--force] [--dataset archive/<name>] [-v] [--no-hc]
//        zfs-hold-tight --doctor [--dataset archive/<name>]
//        zfs-hold-tight --summary [--dataset archive/<name>] [--since YYYY-MM-DD]
//        zfs-hold-tight --check-holds [--no-hc]
//        zfs-hold-tight --fix [--dataset archive/<name>]
//        zfs-hold-tight --hold-adhoc <snapshot> [--no-hc]
//        zfs-hold-tight --hold-latest-tier <tier> [--dataset archive/<name>] [--no-hc]
//        zfs-hold-tight --hold-latest [--dataset archive/<name>] [--no-hc]
//        zfs-hold-tight --version

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

// Tool version: the base Version (#:property) plus the git short SHA the SDK appends to
// InformationalVersion when built with -p:SourceRevisionId=<sha> (see the build recipe).
// Stamped into every run header so the logs record exactly which build ran. (Pattern
// borrowed from the dotnet-inspect peer repo — works under NativeAOT.)
string Version = Utils.GetVersion();
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
int MaxDeletePerRun = 14, RecentWindowDays = 7, MaxDailyAgeHours = 26, MaxDailyShrinkPct = 33, MaxDailyChurnPct = 50;
int MinNewerHolds = 3;          // a non-keeper daily drops once this many held keepers are newer than it
try
{
    if (cfg.TryGetProperty("minNewerHolds", out var mnh))
    {
        MinNewerHolds = mnh.GetInt32();
        // Upper-bounded, not just >= 1: kept even though guardMaxDeletePerRun is no
        // longer derived FROM minNewerHolds (see the "Considered and rejected" note
        // where BacklogPerKeeperInterval used to be) — bounding user input is cheap
        // insurance against any future arithmetic that uses this value, and there is no
        // legitimate reason to configure a value anywhere near this range regardless.
        if (MinNewerHolds < 1 || MinNewerHolds > 10000) throw new ArgumentException("minNewerHolds must be 1-10000");
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
// Dataset-level (not snapshot) watermark: "as of the last successful, non-WORRY --prune,
// everything up to and including this date was already correctly evaluated by this
// tool." See docs/DESIGN.md -- this is what decouples guard 1's gap detection from the
// CURRENT retention policy, so raising minNewerHolds/dailyKeepDays can never retroactively
// make already-legitimately-pruned history look like an unexplained gap.
string propPrunedThrough = $"{ns}:pruned_through";
// Pool-wide (NOT per-dataset) latch: "starting" (never set) | "healthy" | "unhealthy".
// Lives on the pool/prefix parent dataset (e.g. "archive"), not any one managed leaf --
// there's one of these for the whole tool, not one per dataset. Set to "unhealthy" the
// moment ANY dataset shows a content shock (guard 4/5); once set, EVERY dataset freezes
// (no destroy, no pruned_through advance) on every subsequent run regardless of that
// run's own guard results, until an operator runs --reset-health. See docs/DESIGN.md
// "Cross-dataset shock".
string propHealth = $"{ns}:health";
// Free-text companion to propHealth: WHY the pool is unhealthy, e.g. "archive/scratch:
// referenced dropped 41% (...)". Persisted so --doctor/--summary/--reset-health can show
// an operator what tripped it without needing to SSH in and grep the day's log -- the
// pool-wide latch itself doesn't carry that detail. Only ever written alongside a
// transition TO unhealthy (see Zfs.WriteHealth); cleared the moment health goes healthy
// again, so it never lingers as a stale explanation for a since-resolved incident.
string propHealthReason = $"{ns}:health_reason";

// Resolve zfs/curl to absolute trusted paths rather than trusting the (root) cron's PATH —
// a writable earlier PATH element would otherwise be root code execution. Override with
// zfsPath / curlPath in config for non-standard layouts.
string ResolveBin(string name, string cfgKey, string[] candidates)
{
    var configured = cfg.TryGetProperty(cfgKey, out var be) ? be.GetString() : null;
    if (!string.IsNullOrEmpty(configured)) return configured;
    foreach (var cnd in candidates) if (File.Exists(cnd)) return cnd;
    return name;  // last resort: ambient PATH
}
string zfsBin = ResolveBin("zfs", "zfsPath", new[] { "/usr/sbin/zfs", "/sbin/zfs", "/usr/local/sbin/zfs", "/bin/zfs" });
string curlBin = ResolveBin("curl", "curlPath", new[] { "/usr/bin/curl", "/bin/curl", "/usr/local/bin/curl" });
if (zfsBin == "zfs")
    Console.Error.WriteLine("WARNING: zfs not found at a standard absolute path; using PATH. Set \"zfsPath\" in config to harden.");

// Build retention policy from config (needed for all modes)
var retention = new Dictionary<string, Retention>();
var guardsByDataset = new Dictionary<string, Guards>();
var policies = new Dictionary<string, Retention>();
var policyGuards = new Dictionary<string, Guards>();
const string DatasetPrefix = "archive/";  // allowlist: only prune archive/* datasets
string poolDataset = DatasetPrefix.TrimEnd('/');  // where the pool-wide health latch lives

// Considered and rejected: deriving guardMaxDeletePerRun's default per-policy as
// minNewerHolds * 7, on the theory that a policy's first-ever hold-frontier activation
// releases a one-time backlog proportional to minNewerHolds. Simulated the actual
// algorithm for an ORGANICALLY-grown dataset (one snapshot/day since day 1, pruned every
// day) and that premise is false: the max single-run doomed count stays ~6, independent
// of minNewerHolds, for every N from 1 to 10000+. The frontier's position moves back in
// time as N grows, but an organic dataset's own history only extends as far back as N
// grows too — there's no room for a backlog to accumulate behind the frontier, because
// the oldest of the N keepers is never far from day 1. Confirmed against archive/memories'
// actual snapshot history on the live deployment: its daily-tagged history starts exactly
// as far back as its snapshot count implies, one per day, no gap — organic growth, not a
// pre-existing backlog cold-started under a high minNewerHolds. The one scenario that DOES
// release a real N-dependent burst — REDUCING minNewerHolds on an already-established
// dataset — isn't fixed by deriving from the new (smaller) N either. See git history for
// the reverted change if this needs revisiting; don't re-derive from minNewerHolds without
// re-simulating first.

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
        if (minNewerHolds < 1 || minNewerHolds > 10000)
            throw new ArgumentException($"Policy '{policy.Name}': minNewerHolds must be 1-10000");

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

// Parse --since if present (--summary only; ignored by every other mode).
DateTime? since = null;
int sinceIdx = Array.IndexOf(args, "--since");
if (sinceIdx >= 0)
{
    if (sinceIdx + 1 >= args.Length ||
        !DateTime.TryParseExact(args[sinceIdx + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var sinceVal))
    { Console.Error.WriteLine("--since requires a YYYY-MM-DD date"); configDoc?.Dispose(); return 2; }
    since = sinceVal;
}

// Run-lock: serialize any run that mutates ZFS state (prune/fix/hold-*), so two
// overlapping cron invocations can't double-process. Read-only modes skip the lock.
bool willMutate = args.Contains("--prune") || args.Contains("--fix")
    || args.Contains("--hold-adhoc") || args.Contains("--hold-latest-tier") || args.Contains("--hold-latest")
    || args.Contains("--reset-health");
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
    // DirectoryNotFoundException derives from IOException, so it needs its own catch
    // BEFORE the generic one below — otherwise a bad/vanished logDir (race between the
    // CreateDirectory above and the FileStream open, or an invalid configured path)
    // misreports as "another run is in progress", sending an operator to look for a
    // nonexistent concurrent run instead of the actual directory problem.
    catch (DirectoryNotFoundException ex)
    {
        Console.Error.WriteLine($"FATAL: log directory problem: {ex.Message}");
        configDoc?.Dispose();
        return 2;
    }
    catch (IOException)
    {
        Console.Error.WriteLine("another zfs-hold-tight run is in progress (lock held); exiting");
        configDoc?.Dispose();
        return 3;
    }
}

// Shared, read-only environment every command function needs from here on (see
// Types.cs for why this is passed explicitly rather than closed over).
var ctx = new Ctx
{
    Version = Version, Now = DateTime.UtcNow,
    Retention = retention, GuardsByDataset = guardsByDataset,
    Ns = ns, TagMonthly = tagMonthly, TagWeekly = tagWeekly, TagAdhoc = tagAdhoc,
    PropKeep = propKeep, PropTier = propTier, PropPrunedThrough = propPrunedThrough,
    PropHealth = propHealth, PropHealthReason = propHealthReason,
    ZfsBin = zfsBin, CurlBin = curlBin, HcUrl = hcUrl, LogPath = logPath,
    PoolDataset = poolDataset, DatasetPrefix = DatasetPrefix, SharedDataMode = SharedDataMode
};

// Check for special commands
if (args.Contains("--doctor"))
{
    var result = Commands.Doctor(ctx, datasetFilter);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--summary"))
{
    var result = Commands.Summary(ctx, datasetFilter, since);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--check-holds"))
{
    var result = Commands.CheckHolds(ctx, noHc);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--fix"))
{
    var result = Commands.Fix(ctx, datasetFilter);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--hold-adhoc"))
{
    int idx = Array.IndexOf(args, "--hold-adhoc");
    if (idx + 1 >= args.Length) { Console.Error.WriteLine("--hold-adhoc requires snapshot argument"); configDoc?.Dispose(); return 2; }
    string snap = args[idx + 1];
    var result = Commands.HoldAdhoc(ctx, snap, noHc);
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
    var result = Commands.HoldLatestTier(ctx, tier, datasetFilter, noHc);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--hold-latest"))
{
    var result = Commands.HoldLatestTier(ctx, "daily", datasetFilter, noHc);
    configDoc?.Dispose();
    return result;
}

if (args.Contains("--reset-health"))
{
    var result = Commands.ResetHealth(ctx, noHc);
    configDoc?.Dispose();
    return result;
}

bool prune = args.Contains("--prune");
bool force = args.Contains("--force");
bool verbose = args.Contains("-v") || args.Contains("--verbose");

int pruneResult = Commands.Prune(ctx, prune, force, verbose, datasetFilter, noHc);
configDoc?.Dispose();
runLock?.Dispose();
return pruneResult;
