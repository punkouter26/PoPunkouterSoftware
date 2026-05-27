namespace PoPunkouterSoftware.Shared.Session;

public sealed record PoUserSession(
    string LoginMode,
    string DisplayName,
    string Email,
    bool IsGuest,
    bool IsAuthenticated,
    string Status);