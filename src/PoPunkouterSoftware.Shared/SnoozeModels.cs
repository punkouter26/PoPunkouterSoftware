namespace PoPunkouterSoftware.Shared;

// Snooze/dismiss: lets the operator hide a finding from the priority queue / cleanup
// candidates list for a while without the server needing to know what a "finding" is —
// the client owns the opaque key (e.g. "Reliability|my-app-name"); the server only
// persists/retrieves/expires it.
// GoF: Value Object - all records are immutable data carriers with no behaviour.

/// <summary>Request body for <c>POST /api/diag/snooze</c>.</summary>
public record SnoozeRequest(string Key, int DurationDays, string? Reason);

/// <summary>Request body for <c>POST /api/diag/snooze/remove</c>.</summary>
public record SnoozeRemoveRequest(string Key);

/// <summary>A single active (or freshly created) snooze, as returned by the snooze endpoints.</summary>
public record SnoozeEntry(string Key, DateTimeOffset ExpiresAtUtc, string? Reason, DateTimeOffset CreatedAtUtc);
