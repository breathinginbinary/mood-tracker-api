using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using MoodTracker.Api.Data;

namespace MoodTracker.Api.Auth;

public class JwtOptions
{
    public string Issuer { get; set; } = "MoodTracker.Api";
    public string Audience { get; set; } = "MoodTracker.App";
    public int ExpiryMinutes { get; set; } = 60 * 24;
    /// <summary>HMAC signing key. Keep it out of source control (user-secrets or the Jwt__Key environment variable).</summary>
    public string Key { get; set; } = "";

    public SymmetricSecurityKey SigningKey => new(Encoding.UTF8.GetBytes(Key));
}

public record TokenResult(string Token, DateTime ExpiresAt);

public class TokenService(IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public TokenResult Create(ApplicationUser user)
    {
        var expires = DateTime.UtcNow.AddMinutes(_options.ExpiryMinutes);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Expires = expires,
            SigningCredentials = new SigningCredentials(_options.SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = user.Id,
                ["email"] = user.Email!,
                ["jti"] = Guid.NewGuid().ToString(),
            },
        };
        return new TokenResult(new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }
}
