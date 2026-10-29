using Orisumi.Core;

namespace Orisumi.Observer.Runtime.Store;

public enum SubmissionState { Draft, Frozen, Saved, Rejected }
public enum SubmissionStrategy { Immediate, OnCompletion }
public sealed record LocalVersion(VersionSubmission Version, SubmissionState State, SubmissionStrategy Strategy,
    long? FreezeOrder, string? Rule, string? Detail);
public sealed record RejectedVersion(LocalVersion Version, IReadOnlyList<LocalVersion> Dependents);
public sealed record DeliveryStatus(int Pending, int Drafts, int Saved, RejectedVersion? Rejection, string? Error);
