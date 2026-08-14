namespace Heartbeat.Core.DTOs.Apps;

public sealed class AppCatalogAdminErrorResponse
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class AppCatalogAdminInventoryResponse
{
    public int SchemaVersion { get; set; }
    public int CatalogVersion { get; set; }
    public bool IsRollbackCompatible { get; set; }
    public List<AppCatalogAdminProductResponse> Products { get; set; } = [];
    public List<AppCatalogAdminOverrideResponse> ActiveOverrides { get; set; } = [];
}

public sealed class AppCatalogAdminProductResponse
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsProvisional { get; set; }
    public List<AppCatalogAdminIdentityResponse> Identities { get; set; } = [];
    public AppCatalogAdminUsageResponse Usage { get; set; } = new();
}

public sealed class AppCatalogAdminIdentityResponse
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    /// <summary>built-in / override / provisional。</summary>
    public string EffectiveSource { get; set; } = string.Empty;
    public AppCatalogAdminOverrideResponse? ActiveOverride { get; set; }
}

public sealed class AppCatalogAdminUsageResponse
{
    public int SegmentCount { get; set; }
    public long DurationSeconds { get; set; }
    public int DeviceCount { get; set; }
    public DateTimeOffset? LastObservedAt { get; set; }
}

public sealed class AppCatalogAdminOverrideResponse
{
    public long Id { get; set; }
    public string IdentityKey { get; set; } = string.Empty;
    public string TargetAppKey { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string CreatedBySubject { get; set; } = string.Empty;
    public string UpdatedBySubject { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class AppCatalogAdminAuditListResponse
{
    public List<AppCatalogAdminAuditResponse> Entries { get; set; } = [];
}

public sealed class AppCatalogAdminAuditResponse
{
    public long Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public int? SchemaVersion { get; set; }
    public int? CatalogVersion { get; set; }
    public string? ContentHash { get; set; }
    public string? ActorSubject { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string SummaryJson { get; set; } = "{}";
}

public sealed class AppCatalogOverrideSetRequest
{
    public string TargetAppKey { get; set; } = string.Empty;
    public string? NewAppDisplayName { get; set; }
}

public sealed class AppCatalogReconciliationResponse
{
    /// <summary>commit 后的数据库 Id；dry-run 为 null，因为 PostgreSQL sequence 不回滚。</summary>
    public long? TargetAppId { get; set; }
    public string TargetAppKey { get; set; } = string.Empty;
    public List<string> IdentityKeys { get; set; } = [];
    public int LegacySegmentsRebound { get; set; }
    public int CurrentDevicesAffected { get; set; }
    public int ProductsRemoved { get; set; }
    public int IconsMovedOrRemoved { get; set; }
    public int KnowledgeRowsChangedOrDeduplicated { get; set; }
    public int QuestionCachesInvalidated { get; set; }
    public List<AppCatalogAffectedProductResponse> RemovedProducts { get; set; } = [];
    public List<AppCatalogIconImpactResponse> IconImpacts { get; set; } = [];
    public List<AppCatalogKnowledgeChangeResponse> KnowledgeChanges { get; set; } = [];
    public List<AppCatalogKnowledgeDeduplicationResponse> KnowledgeDeduplications { get; set; } = [];
    /// <summary>删除 Override 后的 catalog / provisional 回落；设置 Override 时为空。</summary>
    public string? FallbackSource { get; set; }
}

public sealed class AppCatalogAffectedProductResponse
{
    public long Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool IsProvisional { get; set; }
}

public sealed class AppCatalogIconImpactResponse
{
    /// <summary>keep-target / move-source。</summary>
    public string Resolution { get; set; } = string.Empty;
    public int Count { get; set; }
}

public sealed class AppCatalogKnowledgeChangeResponse
{
    public string Category { get; set; } = string.Empty;
    public string BeforeStepsJson { get; set; } = string.Empty;
    public string AfterStepsJson { get; set; } = string.Empty;
}

public sealed class AppCatalogKnowledgeDeduplicationResponse
{
    public string Category { get; set; } = string.Empty;
    public int RemovedRows { get; set; }
}

public sealed class AppCatalogExportRequest
{
    public List<string> SelectedIdentityKeys { get; set; } = [];
}

public sealed class AppCatalogExportResponse
{
    public bool HasChanges { get; set; }
    public int SchemaVersion { get; set; }
    public int ProposedCatalogVersion { get; set; }
    public string? FileName { get; set; }
    public string? ContentHash { get; set; }
    /// <summary>JSON 文件原始 UTF-8 bytes；JSON 传输时为 base64。</summary>
    public byte[]? Content { get; set; }
}
