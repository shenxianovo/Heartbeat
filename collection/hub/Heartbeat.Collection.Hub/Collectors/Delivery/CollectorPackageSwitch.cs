using Heartbeat.Collection.Hub.Collectors.Packages;
using Heartbeat.Collection.Hub.Collectors.Protocol;
using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// How one attempt to take the Approved Collector Package Candidate into use ended. Every value is a
/// fact about this Collector Instance only; a switch never speaks for another Instance that happens to
/// share the same Collector Installation.
/// </summary>
public enum CollectorPackageSwitchOutcome
{
    /// <summary>Nothing is approved for this Collector Instance, so there is nothing to take into use.</summary>
    NoApprovedCandidate,

    /// <summary>The approved candidate is already the effective Collector Package of this Instance.</summary>
    AlreadyCurrent,

    /// <summary>
    /// The approved candidate was refused before the running Activation was touched: it is no longer a
    /// real Collector Installation, or the Runtime rejected it on identity or compatibility grounds.
    /// </summary>
    Rejected,

    /// <summary>The candidate reached Ready. It is now the effective Package and the Last-Known-Good.</summary>
    Switched,

    /// <summary>The candidate failed before Ready; the previous Package is running again.</summary>
    RolledBack,

    /// <summary>
    /// The candidate failed before Ready and the previous Package could not be reactivated either, so
    /// this Collector Instance is not collecting and needs the owner.
    /// </summary>
    RollbackFailed
}

/// <summary>
/// The outcome of one switch attempt plus the resulting projection of the Collector Instance's
/// persisted update state, so a caller never has to read a second surface to learn what happened.
/// <see cref="Activation" /> is the Activation that ends up running — the candidate after
/// <see cref="CollectorPackageSwitchOutcome.Switched" />, the restored Last-Known-Good after
/// <see cref="CollectorPackageSwitchOutcome.RolledBack" /> — and is <c>null</c> whenever the running
/// Activation was left untouched or could not be restored.
/// </summary>
public sealed record CollectorPackageSwitchResult(
    CollectorPackageSwitchOutcome Outcome,
    CollectorPackageUpdateStatus Status,
    ManagedProcessCollectorActivation? Activation = null);

/// <summary>
/// The single authority on taking an Approved Collector Package Candidate into use for a ManagedProcess
/// Collector Instance, and on deciding which Collector Package that Instance starts with at all.
///
/// <para>
/// It owns exactly one rule, applied in both directions: a candidate becomes this Instance's effective
/// Collector Package only by reaching Ready. <see cref="SwitchToApprovedAsync" /> hands the approved
/// exact reference to the Runtime and lets Ready decide; <see cref="ResolveEffectivePackage" /> answers
/// the mirror-image question at host start, where the only candidate that may come back is the one that
/// already reached Ready and is recorded as the Instance's effective Package. An approved candidate that
/// was never Ready therefore never sneaks in through a restart, and a host restart cannot produce a
/// second Fact Stream writer, because the Instance simply starts on the Package it was already running.
/// </para>
///
/// <para>
/// The exact reference is used verbatim. Nothing here re-reads the Registry, re-resolves a channel or
/// consults "latest": the approved PackageId, Version and artifact SHA-256 select one content-isolated
/// directory, and <see cref="CollectorInstallationStore.OpenInstallation" /> remains the only function
/// that may call that directory a Collector Installation. A directory that lost its completion marker,
/// or whose marker names another candidate, is refused here exactly as it would be at approval time.
/// </para>
///
/// <para>
/// Failure is recorded, never acted on. A refused or failed switch writes one structured last error into
/// the Collector Instance's own update state — the same record the management surface projects — and
/// leaves the approved candidate, the Installation and the Last-Known-Good untouched. There is no retry,
/// no backoff, no timer and no un-approval: the next attempt is the owner's next explicit act. Only a
/// candidate that really reached Ready clears the last error.
/// </para>
///
/// <para>
/// This class never writes Collector Desired State, never deletes an Installation and never touches the
/// System/BuiltIn or ExternalHost drivers, whose artifacts are not delivered this way at all.
/// </para>
/// </summary>
public sealed class CollectorPackageSwitch
{
    private readonly CollectorRuntime _runtime;
    private readonly CollectorInstallationStore _installations;
    private readonly TimeProvider _timeProvider;

    public CollectorPackageSwitch(
        CollectorRuntime runtime,
        CollectorInstallationStore installations,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _installations = installations ?? throw new ArgumentNullException(nameof(installations));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// The Collector Package this Collector Instance must start with right now.
    ///
    /// It is the approved candidate only when that candidate is both this Instance's effective Package —
    /// which is to say it once reached Ready — and still a real Collector Installation. In every other
    /// case, including an approved candidate that has not been Ready yet, the answer is
    /// <paramref name="hostPackage" />: the Package the host itself delivers, which for the first vertical
    /// slice is the previous Last-Known-Good.
    ///
    /// A candidate that was effective but whose directory is no longer an Installation is refused rather
    /// than started, and the reason is recorded as the last error before falling back, because starting
    /// content that no longer matches its completion marker would run something nobody approved.
    /// </summary>
    public LocalCollectorPackage ResolveEffectivePackage(
        Guid collectorInstanceId,
        LocalCollectorPackage hostPackage)
    {
        ArgumentNullException.ThrowIfNull(hostPackage);
        var status = _runtime.GetPackageUpdateStatus(collectorInstanceId);
        if (status.ApprovedCandidate is not { } approved)
            return hostPackage;

        var opened = _installations.OpenInstallation(approved);
        if (opened.IsSuccess)
        {
            var installation = opened.Require();
            return IsEffective(installation, status) ? installation.Package : hostPackage;
        }

        // Only complain when this Instance really was running that candidate. An approved candidate that
        // never reached Ready has nothing to do with what starts now, and the check that installed it
        // already recorded its own outcome.
        if (string.Equals(approved.Version, status.CurrentVersion, StringComparison.Ordinal))
            Record(collectorInstanceId, opened.Reason!.Value, opened.Detail!);
        return hostPackage;
    }

    /// <summary>
    /// Runs exactly one switch attempt for one Collector Instance: open the approved exact reference as a
    /// Collector Installation, then let the Runtime replace the running Activation with it and let Ready
    /// decide the outcome. Rejections and failures come back as
    /// <see cref="CollectorPackageSwitchResult" /> values carrying the resulting projection, so the caller
    /// has one response shape and one authority for "why did this not work".
    /// </summary>
    public async Task<CollectorPackageSwitchResult> SwitchToApprovedAsync(
        Guid collectorInstanceId,
        ManagedProcessUpdateOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var status = _runtime.GetPackageUpdateStatus(collectorInstanceId);
        if (status.ApprovedCandidate is not { } approved)
            return new CollectorPackageSwitchResult(
                CollectorPackageSwitchOutcome.NoApprovedCandidate,
                status);

        var opened = _installations.OpenInstallation(approved);
        if (!opened.IsSuccess)
            return new CollectorPackageSwitchResult(
                CollectorPackageSwitchOutcome.Rejected,
                Record(collectorInstanceId, opened.Reason!.Value, opened.Detail!));

        var installation = opened.Require();
        if (IsEffective(installation, status))
            return new CollectorPackageSwitchResult(CollectorPackageSwitchOutcome.AlreadyCurrent, status);

        try
        {
            var result = await _runtime
                .UpdateManagedProcessAsync(collectorInstanceId, installation.Package, options, cancellationToken)
                .ConfigureAwait(false);
            if (result.Outcome == ManagedProcessUpdateOutcome.Updated)
                return new CollectorPackageSwitchResult(
                    CollectorPackageSwitchOutcome.Switched,
                    _runtime.RecordPackageSwitchOutcome(collectorInstanceId, null),
                    result.Activation);

            var candidateFailure = result.CandidateFailure!;
            return new CollectorPackageSwitchResult(
                CollectorPackageSwitchOutcome.RolledBack,
                Record(
                    collectorInstanceId,
                    ReasonFor(candidateFailure.Code),
                    Detail(candidateFailure)),
                result.Activation);
        }
        catch (ManagedProcessUpdateException exception)
        {
            return new CollectorPackageSwitchResult(
                CollectorPackageSwitchOutcome.RollbackFailed,
                Record(
                    collectorInstanceId,
                    ReasonFor(exception.CandidateFailure.Code),
                    $"{Detail(exception.CandidateFailure)} " +
                    $"Reactivating the previous Collector Package failed as well: " +
                    $"{Detail(exception.RollbackFailure)}"));
        }
        catch (CollectorActivationException exception)
        {
            // The Runtime refused the candidate before the running Activation was stopped, so this
            // Instance is still collecting on the Package it had.
            return new CollectorPackageSwitchResult(
                CollectorPackageSwitchOutcome.Rejected,
                Record(
                    collectorInstanceId,
                    ReasonFor(exception.Error.Code),
                    $"{exception.Error.Code}: {exception.Message}"));
        }
    }

    /// <summary>
    /// Maps a Collector Runtime Activation failure code onto the closed set of delivery failure reasons
    /// the management surface already speaks. It is a translation, not a second taxonomy: the Runtime's
    /// own code always survives in the detail string, and anything not recognised as a compatibility
    /// verdict or a Ready timeout falls back to <see cref="CollectorRegistryFailureReason.StartupFailed" />
    /// rather than inventing a reason.
    /// </summary>
    internal static CollectorRegistryFailureReason ReasonFor(string activationFailureCode) =>
        activationFailureCode switch
        {
            "activation_start_timeout" => CollectorRegistryFailureReason.ReadyTimeout,
            "process_exited" or "process_start_failed" => CollectorRegistryFailureReason.StartupFailed,
            "protocol_invalid_message"
                or "protocol_no_common_major"
                or "capability_no_common_version"
                or "config_version_unsupported"
                or "package_mismatch"
                or "spec_revision_stale"
                or "output_not_declared" => CollectorRegistryFailureReason.Incompatible,
            _ => CollectorRegistryFailureReason.StartupFailed
        };

    private static bool IsEffective(CollectorInstallation installation, CollectorPackageUpdateStatus status) =>
        string.Equals(installation.Reference.Version, status.CurrentVersion, StringComparison.Ordinal) &&
        string.Equals(installation.PackageContentHash, status.CurrentPackageContentHash, StringComparison.Ordinal);

    private static string Detail(CollectorRuntimeFailure failure) => $"{failure.Code}: {failure.Message}";

    private CollectorPackageUpdateStatus Record(
        Guid collectorInstanceId,
        CollectorRegistryFailureReason reason,
        string detail) =>
        _runtime.RecordPackageSwitchOutcome(
            collectorInstanceId,
            new CollectorPackageUpdateFailure(reason, detail, _timeProvider.GetUtcNow()));
}
