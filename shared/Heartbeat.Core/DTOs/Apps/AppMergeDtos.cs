namespace Heartbeat.Core.DTOs.Apps;

public class AppMergeRequest
{
    public string SourceAppKey { get; set; } = string.Empty;
    public string TargetAppKey { get; set; } = string.Empty;
    public bool DryRun { get; set; } = true;
}

public class AppMergeResponse
{
    public bool DryRun { get; set; }
    public bool Committed { get; set; }
    public bool AlreadyMerged { get; set; }
    public AppMergeAppInfo Source { get; set; } = new();
    public AppMergeAppInfo Target { get; set; } = new();
    public List<string> AppIdentityKeys { get; set; } = [];
    public AppMergeKnowledgeImpact Knowledge { get; set; } = new();
    public List<AppMergeIconImpact> Icons { get; set; } = [];
    public List<AppMergeAppInfo> ProvisionalAppsRemoved { get; set; } = [];
    public int LegacySegmentsRebound { get; set; }
    public int CurrentDevicesAffected { get; set; }
}

public class AppMergeAppInfo
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsProvisional { get; set; }
}

public class AppMergeKnowledgeImpact
{
    public int StrandMatchers { get; set; }
    public int MutedMatchers { get; set; }
    public int RecurrenceProbes { get; set; }
    public int QuestionCachesInvalidated { get; set; }
    public List<AppMergeKnowledgeChange> Changes { get; set; } = [];
    public List<AppMergeKnowledgeDeduplication> Deduplications { get; set; } = [];
}

public class AppMergeKnowledgeChange
{
    /// <summary>strand-matcher / muted-matcher / recurrence-probe。</summary>
    public string Category { get; set; } = string.Empty;
    public Guid RowId { get; set; }
    public string BeforeStepsJson { get; set; } = string.Empty;
    public string AfterStepsJson { get; set; } = string.Empty;
}

public class AppMergeKnowledgeDeduplication
{
    public string Category { get; set; } = string.Empty;
    public Guid KeptRowId { get; set; }
    public List<Guid> RemovedRowIds { get; set; } = [];
    public string? KeptStatus { get; set; }
}

public class AppMergeIconImpact
{
    public string OwnerId { get; set; } = string.Empty;
    public bool SourceIconExists { get; set; }
    public bool TargetIconExists { get; set; }
    /// <summary>keep-target / move-source。</summary>
    public string Resolution { get; set; } = string.Empty;
}
