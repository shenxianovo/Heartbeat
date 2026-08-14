using Heartbeat.Core.DTOs.Apps;
using Heartbeat.Server.AppCatalog;
using Heartbeat.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Heartbeat.Server.Controllers;

[ApiController]
[Route("api/v1/admin/app-catalog")]
[Authorize]
public sealed class AdminAppCatalogController(
    AppCatalogAdminQueryService queryService,
    AppCatalogOverrideService overrideService,
    AppCatalogExportService exportService,
    AdminAuthorizationService adminAuthorization,
    ICurrentUserService currentUser) : ControllerBase
{
    [HttpGet]
    [EndpointName("getAdminAppCatalog")]
    [ProducesResponseType<AppCatalogAdminInventoryResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AppCatalogAdminInventoryResponse>> GetInventory(
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        return await queryService.GetInventoryAsync(cancellationToken);
    }

    [HttpGet("audit")]
    [EndpointName("getAdminAppCatalogAudit")]
    [ProducesResponseType<AppCatalogAdminAuditListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<AppCatalogAdminAuditListResponse>> GetAudit(
        [FromQuery] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdmin()) return Forbid();
        return await queryService.GetAuditAsync(limit, cancellationToken);
    }

    [HttpPost("overrides/{identityKey}/preview")]
    [EndpointName("previewAdminAppCatalogOverride")]
    [ProducesResponseType<AppCatalogReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppCatalogReconciliationResponse>> PreviewOverride(
        string identityKey,
        [FromBody] AppCatalogOverrideSetRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            var result = await overrideService.PreviewAsync(
                identityKey, request.TargetAppKey, request.NewAppDisplayName,
                currentUser.GetUserId(), cancellationToken);
            return ToResponse(result, includeTargetAppId: false);
        }
        catch (Exception exception) when (TryMapDomainError(exception, out var action))
        {
            return action;
        }
    }

    [HttpPut("overrides/{identityKey}")]
    [EndpointName("setAdminAppCatalogOverride")]
    [ProducesResponseType<AppCatalogReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppCatalogReconciliationResponse>> SetOverride(
        string identityKey,
        [FromBody] AppCatalogOverrideSetRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            var result = await overrideService.SetAsync(
                identityKey, request.TargetAppKey, request.NewAppDisplayName,
                currentUser.GetUserId(), cancellationToken);
            return ToResponse(result);
        }
        catch (Exception exception) when (TryMapDomainError(exception, out var action))
        {
            return action;
        }
    }

    [HttpPost("overrides/{identityKey}/delete-preview")]
    [EndpointName("previewDeleteAdminAppCatalogOverride")]
    [ProducesResponseType<AppCatalogReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppCatalogReconciliationResponse>> PreviewDeleteOverride(
        string identityKey,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            var result = await overrideService.PreviewDeleteAsync(
                identityKey, currentUser.GetUserId(), cancellationToken);
            return ToResponse(
                result.Reconciliation, result.FallbackSource, includeTargetAppId: false);
        }
        catch (Exception exception) when (TryMapDomainError(exception, out var action))
        {
            return action;
        }
    }

    [HttpDelete("overrides/{identityKey}")]
    [EndpointName("deleteAdminAppCatalogOverride")]
    [ProducesResponseType<AppCatalogReconciliationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppCatalogReconciliationResponse>> DeleteOverride(
        string identityKey,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            var result = await overrideService.DeleteAsync(
                identityKey, currentUser.GetUserId(), cancellationToken);
            return ToResponse(result.Reconciliation, result.FallbackSource);
        }
        catch (Exception exception) when (TryMapDomainError(exception, out var action))
        {
            return action;
        }
    }

    [HttpPost("export")]
    [EndpointName("exportAdminAppCatalogCandidate")]
    [ProducesResponseType<AppCatalogExportResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<AppCatalogAdminErrorResponse>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<AppCatalogExportResponse>> Export(
        [FromBody] AppCatalogExportRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsAdmin()) return Forbid();
        try
        {
            return await exportService.ExportAsync(request, cancellationToken);
        }
        catch (AppCatalogExportException exception)
        {
            return Error<AppCatalogExportResponse>(exception.Code, exception.Message);
        }
    }

    private bool IsAdmin() => adminAuthorization.IsAdmin(currentUser.GetUserId());

    private bool TryMapDomainError(Exception exception, out ActionResult<AppCatalogReconciliationResponse> action)
    {
        if (exception is AppCatalogOverrideException domain)
        {
            action = Error<AppCatalogReconciliationResponse>(domain.Code, domain.Message);
            return true;
        }

        if (exception is AppCatalogException catalog)
        {
            action = Conflict(new AppCatalogAdminErrorResponse
            {
                Code = "catalog_conflict",
                Message = catalog.Message
            });
            return true;
        }

        if (exception is ArgumentException validation)
        {
            action = BadRequest(new AppCatalogAdminErrorResponse
            {
                Code = "invalid_request",
                Message = validation.Message
            });
            return true;
        }

        action = null!;
        return false;
    }

    private ActionResult<T> Error<T>(string code, string message)
    {
        var body = new AppCatalogAdminErrorResponse { Code = code, Message = message };
        if (code.EndsWith("_not_found", StringComparison.Ordinal)) return NotFound(body);
        if (code.StartsWith("invalid_", StringComparison.Ordinal)) return BadRequest(body);
        return Conflict(body);
    }

    private static AppCatalogReconciliationResponse ToResponse(
        AppProductReconciliationResult value,
        string? fallbackSource = null,
        bool includeTargetAppId = true) => new()
    {
        TargetAppId = includeTargetAppId ? value.TargetAppId : null,
        TargetAppKey = value.TargetAppKey,
        IdentityKeys = value.IdentityKeys.ToList(),
        LegacySegmentsRebound = value.LegacySegmentsRebound,
        CurrentDevicesAffected = value.CurrentDevicesAffected,
        ProductsRemoved = value.AppsRemoved,
        IconsMovedOrRemoved = value.IconsMovedOrRemoved,
        KnowledgeRowsChangedOrDeduplicated = value.KnowledgeRowsRewritten,
        QuestionCachesInvalidated = value.QuestionCachesInvalidated,
        RemovedProducts = value.RemovedProducts.Select(x => new AppCatalogAffectedProductResponse
        {
            Id = x.Id,
            Key = x.Key,
            DisplayName = x.DisplayName,
            IsProvisional = x.IsProvisional
        }).ToList(),
        IconImpacts = value.IconImpacts.Select(x => new AppCatalogIconImpactResponse
        {
            Resolution = x.Resolution,
            Count = x.Count
        }).ToList(),
        KnowledgeChanges = value.KnowledgeChanges.Select(x => new AppCatalogKnowledgeChangeResponse
        {
            Category = x.Category,
            BeforeStepsJson = x.BeforeStepsJson,
            AfterStepsJson = x.AfterStepsJson
        }).ToList(),
        KnowledgeDeduplications = value.KnowledgeDeduplications.Select(x =>
            new AppCatalogKnowledgeDeduplicationResponse
            {
                Category = x.Category,
                RemovedRows = x.RemovedRows
            }).ToList(),
        FallbackSource = fallbackSource
    };
}
