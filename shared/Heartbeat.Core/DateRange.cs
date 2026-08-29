namespace Heartbeat.Core;

/// <summary>
/// Generic half-open UTC instant window used by fact queries and projections. Calendar consumers must
/// enter through Analytics' validated Local Calendar Window and pass its exact endpoints here; this type
/// deliberately performs no civil-date, timezone or fixed-duration derivation.
/// </summary>
public readonly record struct DateRange(DateTime UtcStart, DateTime UtcEnd)
;
