using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace InvoiceFlow.Infrastructure.Identity;

public sealed class SessionOptions
{
    public const string SectionName = "Auth:Session";

    /// <summary>HMAC-SHA256 key, at least 32 bytes (base64 or plain text). Secret: Key Vault / user-secrets / env only.</summary>
    public string? SigningKey { get; set; }

    public string Issuer { get; set; } = "invoiceflow";
    public string Audience { get; set; } = "invoiceflow";
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Signed out after this long without activity.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(8);

    /// <summary>Signed out after this long, whatever the activity.</summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromDays(14);

    /// <summary>AES-256 key (32 bytes, base64) that encrypts authenticator secrets. Secret: Key Vault / user-secrets / env only.
    /// Keep it stable: losing it means every user has to set up two-step sign-in again.</summary>
    public string? MfaEncryptionKey { get; set; }

    /// <summary>Anyone who can administer or approve must use two-step sign-in (ADR-0009). Local accounts only; SSO users get MFA from Entra.</summary>
    public bool RequireMfaForPrivileged { get; set; } = true;

    /// <summary>How long the "now enter your code" step stays open after a correct password.</summary>
    public TimeSpan MfaChallengeLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public int MaxFailedLogins { get; set; } = 5;
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    public SymmetricSecurityKey Key()
    {
        if (string.IsNullOrWhiteSpace(SigningKey))
        {
            throw new InvalidOperationException($"Configure {SectionName}:SigningKey (32+ random bytes, kept secret).");
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(SigningKey);
        }
        catch (FormatException)
        {
            bytes = Encoding.UTF8.GetBytes(SigningKey);
        }

        if (bytes.Length < 32)
        {
            throw new InvalidOperationException($"{SectionName}:SigningKey must be at least 32 bytes.");
        }

        return new SymmetricSecurityKey(bytes);
    }
}

public static class SessionClaims
{
    public const string Stamp = "sstamp";
    public const string Permission = "perm";
    public const string MustChangePassword = "mcp";
}

/// <summary>Short-lived access tokens (JWT) and opaque refresh tokens.</summary>
public sealed class TokenIssuer(SessionOptions options, TimeProvider time)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <summary>Identity + security stamp only. Permissions are looked up per request, so changes apply within seconds.</summary>
    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(UserRow user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = time.GetUtcNow();
        var expires = now + options.AccessTokenLifetime;
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
            [JwtRegisteredClaimNames.Email] = user.Email,
            ["name"] = user.DisplayName,
            [SessionClaims.Stamp] = user.SecurityStamp,
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
        };
        if (user.MustChangePassword)
        {
            claims[SessionClaims.MustChangePassword] = "1";
        }

        var token = _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(options.Key(), SecurityAlgorithms.HmacSha256),
        });
        return (token, expires);
    }

    private const string MfaAudienceSuffix = ":mfa";

    /// <summary>
    /// Proof that the password step passed, valid for a few minutes, only accepted by the code step (own audience,
    /// so it can never be used as an access token). Bound to the security stamp: a password change voids it.
    /// </summary>
    public string CreateMfaChallenge(UserRow user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var now = time.GetUtcNow();
        return _handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience + MfaAudienceSuffix,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = (now + options.MfaChallengeLifetime).UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                [JwtRegisteredClaimNames.Sub] = user.Id.ToString(),
                [SessionClaims.Stamp] = user.SecurityStamp,
                [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            },
            SigningCredentials = new SigningCredentials(options.Key(), SecurityAlgorithms.HmacSha256),
        });
    }

    public async Task<(Guid UserId, string Stamp)?> ReadMfaChallengeAsync(string? challenge)
    {
        if (string.IsNullOrWhiteSpace(challenge))
        {
            return null;
        }

        var parameters = ValidationParameters();
        parameters.ValidAudience = options.Audience + MfaAudienceSuffix;
        parameters.ClockSkew = TimeSpan.Zero;
        var result = await _handler.ValidateTokenAsync(challenge, parameters).ConfigureAwait(false);
        if (!result.IsValid ||
            !result.Claims.TryGetValue(JwtRegisteredClaimNames.Sub, out var sub) || !Guid.TryParse(sub as string, out var id) ||
            !result.Claims.TryGetValue(SessionClaims.Stamp, out var stampValue) || stampValue is not string stamp)
        {
            return null;
        }

        return (id, stamp);
    }

    public TokenValidationParameters ValidationParameters()
    {
        var p = new TokenValidationParameters
        {
            ValidIssuer = options.Issuer,
            ValidAudience = options.Audience,
            IssuerSigningKey = options.Key(),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = ClaimTypes.Role,
        };

        // Check expiry with the same clock that issued the token (TimeProvider), not the machine clock.
        p.LifetimeValidator = (notBefore, expires, _, v) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            return (notBefore is null || notBefore.Value <= now + v.ClockSkew) && expires is not null && expires.Value > now - v.ClockSkew;
        };
        return p;
    }

    /// <summary>256 random bits, URL-safe. Only its hash is stored.</summary>
    public static string NewRefreshToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string refreshToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
}
