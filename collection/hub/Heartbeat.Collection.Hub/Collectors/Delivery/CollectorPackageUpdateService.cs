using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// The whole owner-facing update management surface for one Collector Instance: read
/// <c>Current</c>, run one manual <c>CheckNow</c>, approve one exact Collector Package reference.
/// It is what the authenticated Hub/Headless management API maps onto, and it is intentionally the
/// only entry point — there is no batch approval, no opaque offer token, no approval audit and no
/// background scheduler anywhere behind it.
///
/// <para>
/// <c>CheckNow</c> is synchronous and manual. One call reads the Registry index once, downloads and
/// verifies the artifact once, and installs it into its own version directory once. It never
/// retries, never schedules a retry and never polls; a failure is written to the Collector
/// Instance's persisted update state as a structured last error and the caller gets the same
/// projection back, so the next attempt only happens when a human asks for it again.
/// </para>
///
/// <para>
/// <c>Approve</c> takes an exact reference — PackageId, Version and artifact SHA-256 — and accepts
/// it only when all three still describe a real Collector Installation according to
/// <see cref="CollectorInstallationStore.OpenInstallation" />, the single admission function. It
/// deliberately does not consult the Registry: an exact reference that was downloaded, verified and
/// installed stays approvable after the Registry has moved on, and approval never re-resolves
/// "latest".
/// </para>
///
/// <para>
/// Neither operation writes Collector Desired State, promotes the candidate or demotes the existing
/// Last-Known-Good. Approval is not Ready; taking an approved candidate into use is an Activation
/// concern outside this class.
/// </para>
/// </summary>
public sealed class CollectorPackageUpdateService
{
    private readonly CollectorRuntime _runtime;
    private readonly CollectorInstallationStore _installations;
    private readonly CollectorPackageInstaller? _installer;
    private readonly TimeProvider _timeProvider;

    /// <param name="installer">
    /// <c>null</c> when this Hub has no Registry configured. Approval still works in that case,
    /// because an already installed candidate does not need the Registry to be reachable.
    /// </param>
    public CollectorPackageUpdateService(
        CollectorRuntime runtime,
        CollectorInstallationStore installations,
        CollectorPackageInstaller? installer = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _installations = installations ?? throw new ArgumentNullException(nameof(installations));
        _installer = installer;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The current projection of the Collector Instance's persisted update state. Throws
    /// <see cref="KeyNotFoundException" /> when this Hub does not own that Collector Instance.
    /// </summary>
    public CollectorPackageUpdateStatus Current(Guid collectorInstanceId) =>
        _runtime.GetPackageUpdateStatus(collectorInstanceId);

    /// <summary>
    /// Runs exactly one check: read the Registry index, download and verify the exact artifact it
    /// names, and install it into its own version directory. The result is always the resulting
    /// projection — a failed check reports itself through
    /// <see cref="CollectorPackageUpdateStatus.LastFailure" /> rather than through an exception, so
    /// the management API has one response shape and one authority for "why did this not work".
    /// </summary>
    public async Task<CollectorPackageUpdateStatus> CheckNowAsync(
        Guid collectorInstanceId,
        CancellationToken cancellationToken = default)
    {
        var status = _runtime.GetPackageUpdateStatus(collectorInstanceId);
        if (_installer is null)
            return Fail(
                collectorInstanceId,
                CollectorRegistryFailureReason.RegistryNotConfigured,
                "This Hub has no Official Collector Package Registry configured.");

        CollectorRegistryResult<CollectorRegistryIndex> index;
        try
        {
            index = await _installer.Registry
                .GetCurrentAsync(status.PackageId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Fail(
                collectorInstanceId,
                CollectorRegistryFailureReason.Cancelled,
                $"Reading the Registry index for '{status.PackageId}' was cancelled.");
        }
        if (!index.IsSuccess)
            return Fail(collectorInstanceId, index.Reason!.Value, index.Detail!);

        var advertised = CollectorPackageReference.FromIndex(index.Require());
        var validated = advertised.Validated();
        if (!validated.IsSuccess)
            return Fail(collectorInstanceId, validated.Reason!.Value, validated.Detail!);

        // The index really was read, so the observation is recorded even when the download that
        // follows fails: "what the Registry advertised" and "what is installed" are separate facts.
        var installed = await _installer.InstallAsync(index.Require(), cancellationToken).ConfigureAwait(false);
        return _runtime.RecordPackageUpdateCheck(
            collectorInstanceId,
            advertised,
            _timeProvider.GetUtcNow(),
            installed.Value?.Reference,
            installed.IsSuccess
                ? null
                : new CollectorPackageUpdateFailure(
                    installed.Reason!.Value,
                    installed.Detail!,
                    _timeProvider.GetUtcNow()));
    }

    /// <summary>
    /// Approves exactly <paramref name="reference" />. Throws <see cref="KeyNotFoundException" />
    /// when this Hub does not own the Collector Instance; every other rejection is a structured
    /// result, never an exception and never message text.
    /// </summary>
    public CollectorRegistryResult<CollectorPackageUpdateStatus> Approve(
        Guid collectorInstanceId,
        CollectorPackageReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var status = _runtime.GetPackageUpdateStatus(collectorInstanceId);

        var validated = reference.Validated();
        if (!validated.IsSuccess)
            return CollectorRegistryResult<CollectorPackageUpdateStatus>.Failure(validated);
        if (!string.Equals(reference.PackageId, status.PackageId, StringComparison.Ordinal))
            return CollectorRegistryResult<CollectorPackageUpdateStatus>.Failure(
                CollectorRegistryFailureReason.CollectorInstancePackageMismatch,
                $"Collector Instance '{collectorInstanceId}' runs '{status.PackageId}', " +
                $"not '{reference.PackageId}'.");

        // The installation store is the only authority on whether this exact candidate is really
        // held by this machine. The Registry is deliberately not consulted.
        var installation = _installations.OpenInstallation(reference);
        if (!installation.IsSuccess)
            return CollectorRegistryResult<CollectorPackageUpdateStatus>.Failure(installation);

        return CollectorRegistryResult<CollectorPackageUpdateStatus>.Success(
            _runtime.ApprovePackageCandidate(collectorInstanceId, reference));
    }

    private CollectorPackageUpdateStatus Fail(
        Guid collectorInstanceId,
        CollectorRegistryFailureReason reason,
        string detail) =>
        _runtime.RecordPackageUpdateCheck(
            collectorInstanceId,
            null,
            null,
            null,
            new CollectorPackageUpdateFailure(reason, detail, _timeProvider.GetUtcNow()));
}
