namespace PoShared.Auth;

// GoF: Value Object — an immutable record describing the current user principal.
// SOLID: Single Responsibility — this record carries only user-identity data;
//        authentication logic lives in the server's Infrastructure layer.

/// <summary>
/// Shared user-identity model transported between server API responses and the client.
/// The ANON identity is used during local development when OAuth is bypassed.
/// </summary>
public record UserInfo(
    string UserId,
    string DisplayName,
    bool IsAnonymous)
{
    /// <summary>Sentinel representing an unauthenticated/anonymous local-dev session.</summary>
    public static readonly UserInfo Anon = new("ANON", "Anonymous (Dev)", IsAnonymous: true);
}
