// Shared record types + the run-wide context every command function needs.
//
// Local functions declared via top-level statements close over that ONE file's locals --
// a file pulled in via #:include can't share those closures, since it isn't part of the
// same top-level-statements block (only one file in a compilation may have those). So
// every function that used to read a top-level local like `zfsBin` or `retention`
// directly now takes a `Ctx` instead, built once in zfs-hold-tight.cs right after config
// validation and passed explicitly from there on.
class Ctx
{
    public required string Version;
    public required DateTime Now;
    public required Dictionary<string, Retention> Retention;
    public required Dictionary<string, Guards> GuardsByDataset;
    public required string Ns, TagMonthly, TagWeekly, TagAdhoc;
    public required string PropKeep, PropTier, PropPrunedThrough, PropHealth, PropHealthReason;
    public string TagLatched => $"{Ns}:latched";
    public string PropLatchHistory => $"{Ns}:latch_history";
    public string PropLatchEvidence => $"{Ns}:latch_evidence";
    public string PropLatchId => $"{Ns}:latch_id";
    public string FalsePositiveProperty(string id) => $"{Ns}:false_positive_{id}";
    public string PropAcceptedContent => $"{Ns}:accepted_content";
    public string PropEstablished => $"{Ns}:established";
    public required string ZfsBin, CurlBin;
    public required string? HcUrl;
    public required string LogPath;
    public required string PoolDataset, DatasetPrefix;
    public required UnixFileMode SharedDataMode;
}

record Retention(int DailyKeepDays, int WeeklyKeepDays, int MonthlyKeepDays, int MinNewerHolds);
record Guards(int MaxDeletePerRun, int RecentWindowDays, int MaxDailyAgeHours, int MaxDailyShrinkPct, int MaxDailyChurnPct);
record Snap(string Name, DateTime Date, long? KeepUntil, string? Tier, long? Referenced = null, long? Written = null, string? LatchHistory = null);
