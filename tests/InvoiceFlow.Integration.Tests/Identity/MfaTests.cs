using System.Security.Cryptography;
using System.Text;
using InvoiceFlow.Infrastructure.Identity;

namespace InvoiceFlow.Integration.Tests.Identity;

/// <summary>Pure tests (no database): TOTP against the RFC 6238 vectors, base32, secret encryption, recovery codes.</summary>
public class MfaTests
{
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    // RFC 6238 appendix B (SHA-1); the RFC prints 8 digits, authenticator apps use the last 6.
    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Totp_matches_the_rfc_test_vectors(long unixSeconds, string expected) =>
        Assert.Equal(expected, Totp.Code(RfcSecret, unixSeconds / Totp.StepSeconds));

    [Fact]
    public void Code_is_accepted_once_and_within_one_step_of_clock_drift()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        var previous = Totp.Code(RfcSecret, step - 1);

        Assert.Equal(step - 1, Totp.Verify(RfcSecret, previous, now, null));
        Assert.Equal(step - 1, Totp.Verify(RfcSecret, "  " + previous[..3] + " " + previous[3..], now, null)); // spaces typed
        Assert.Null(Totp.Verify(RfcSecret, previous, now, lastUsedStep: step - 1)); // replay
        Assert.Null(Totp.Verify(RfcSecret, Totp.Code(RfcSecret, step - 2), now, null)); // too old
        Assert.Null(Totp.Verify(RfcSecret, "12345", now, null));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_rfc4648(string plain, string encoded)
    {
        Assert.Equal(encoded, Base32.Encode(Encoding.ASCII.GetBytes(plain)));
        Assert.Equal(plain, Encoding.ASCII.GetString(Base32.Decode(encoded.ToLowerInvariant())));
    }

    [Fact]
    public void Secret_is_encrypted_and_bound_to_its_user()
    {
        var protector = new MfaSecretProtector(new SessionOptions { MfaEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var user = Guid.NewGuid();
        var secret = Totp.NewSecret();

        var stored = protector.Protect(secret, user);

        Assert.DoesNotContain(Base32.Encode(secret), stored, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(stored, user));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(stored, Guid.NewGuid())); // copied to another user
        Assert.False(new MfaSecretProtector(new SessionOptions()).IsConfigured);
        Assert.False(new MfaSecretProtector(new SessionOptions { MfaEncryptionKey = "too-short" }).IsConfigured);
    }

    [Fact]
    public void Recovery_codes_are_unique_and_forgiving_to_type()
    {
        var codes = RecoveryCodes.Generate();

        Assert.Equal(RecoveryCodes.Count, codes.Distinct().Count());
        Assert.All(codes, c => Assert.Matches("^[a-z2-9]{5}-[a-z2-9]{5}$", c));
        Assert.Equal(RecoveryCodes.Hash(codes[0]), RecoveryCodes.Hash(" " + codes[0].ToUpperInvariant().Replace("-", " ", StringComparison.Ordinal)));
        Assert.True(RecoveryCodes.LooksLikeRecoveryCode(codes[0]));
        Assert.False(RecoveryCodes.LooksLikeRecoveryCode("123456"));
    }

    [Fact]
    public void Otpauth_link_has_what_authenticator_apps_need()
    {
        var uri = Totp.OtpAuthUri("InvoiceFlow", "clara@example.com", RfcSecret);

        Assert.StartsWith("otpauth://totp/InvoiceFlow:clara%40example.com?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", uri, StringComparison.Ordinal);
        Assert.Contains("&issuer=InvoiceFlow", uri, StringComparison.Ordinal);
    }
}
