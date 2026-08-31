using Heartbeat.Collection.Hub.Collectors.Runtime;

namespace Heartbeat.Collection.Hub.Collectors.Delivery;

/// <summary>
/// The last structured failure of a manual check, kept until a later check succeeds. Nothing else
/// clears it: reading <see cref="CollectorPackageUpdateStatus" />, approving a candidate or
/// restarting the Hub all leave it exactly where it was, because it is the only record of why the
/// owner is being asked to act again.
/// </summary>
public sealed record CollectorPackageUpdateFailure(
    CollectorRegistryFailureReason Reason,
    string Message,
    DateTimeOffset OccurredAt);

/// <summary>
/// Everything the owner-facing update management surface shows for one Collector Instance. It is a
/// projection of the Collector Instance's persisted state, not a second authority: every field is
/// read from the same Collector Runtime State record that <c>CheckNow</c> and <c>Approve</c> write,
/// so two views can never disagree.
///
/// The three candidate fields are deliberately separate facts. <see cref="RegistryCurrent" /> is
/// only what the Registry advertised at <see cref="RegistryCheckedAt" />;
/// <see cref="InstalledCandidate" /> is a Collector Installation this machine really holds; and
/// <see cref="ApprovedCandidate" /> is the exact reference the owner approved but which has not
/// become Ready yet. Until it does, <see cref="LastKnownGood" /> and the currently effective
/// Package stay untouched.
/// </summary>
public sealed record CollectorPackageUpdateStatus(
    Guid CollectorInstanceId,
    string PackageId,
    string CurrentVersion,
    string CurrentPackageContentHash,
    LastKnownGoodCollectorPackage? LastKnownGood,
    CollectorPackageReference? InstalledCandidate,
    CollectorPackageReference? ApprovedCandidate,
    CollectorPackageReference? RegistryCurrent,
    DateTimeOffset? RegistryCheckedAt,
    CollectorPackageUpdateFailure? LastFailure);
