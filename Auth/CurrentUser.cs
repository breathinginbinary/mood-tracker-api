using System.Security.Claims;

namespace MoodTracker.Api.Auth;

/// <summary>The signed-in user of the current request.</summary>
public interface ICurrentUser
{
    /// <summary>Null when the request is not authenticated.</summary>
    string? Id { get; }
}

public class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    // JWT claim names are not remapped (MapInboundClaims = false), so the id is "sub".
    public string? Id => accessor.HttpContext?.User.FindFirstValue("sub");
}
