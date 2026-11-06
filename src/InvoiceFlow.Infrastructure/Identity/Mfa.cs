using System.Security.Cryptography;
using System.Text;

namespace InvoiceFlow.Infrastructure.Identity;

/// <summary>
/// Time-based one-time passwords (RFC 6238 / RFC 4226): HMAC-SHA1, 30-second steps, 6 digits — what Microsoft
/// Authenticator, Google Authenticator, 1Password etc. use by default.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    /// <summary>160-bit random secret (RFC 4226 recommends ≥ 128).</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // HMAC-SHA1 is what RFC 6238 and every authenticator app use; HMAC keeps SHA-1 safe here.
        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks a code against the current step ±1 (phone clocks drift). Returns the matching step so the caller can refuse
    /// it next time (a code works only once), or null.
    /// </summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var digits = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        long? match = null;
        for (var step = current - 1; step <= current + 1; step++)
        {
            // Check all three in constant time; don't stop early.
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)) &&
                (lastUsedStep is null || step > lastUsedStep))
            {
                match ??= step;
            }
        }

        return match;
    }

    /// <summary>The link authenticator apps read from the QR code.</summary>
    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={StepSeconds}";
}

/// <summary>RFC 4648 base32 without padding (the format authenticator apps expect).</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = text.Trim().TrimEnd('=').Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                throw new FormatException("Not base32.");
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}

/// <summary>
/// Encrypts authenticator secrets at rest with AES-256-GCM and a key from configuration (Key Vault / user-secrets).
/// A configured key (rather than ASP.NET Data Protection's machine-local key ring) keeps secrets readable across
/// container restarts and replicas. Format: "v1." + base64(nonce | tag | ciphertext); the user id is bound as AAD,
/// so a secret copied to another user's row doesn't decrypt.
/// </summary>
public sealed class MfaSecretProtector(SessionOptions options)
{
    private const string Prefix = "v1.";

    public bool IsConfigured => TryKey() is not null;

    public string Protect(byte[] secret, Guid userId)
    {
        var key = TryKey() ?? throw MissingKey();
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[secret.Length];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, secret, cipher, tag, userId.ToByteArray());
        return Prefix + Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    public byte[] Unprotect(string stored, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var key = TryKey() ?? throw MissingKey();
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new CryptographicException("Unknown secret format.");
        }

        var blob = Convert.FromBase64String(stored[Prefix.Length..]);
        var plain = new byte[blob.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(blob.AsSpan(0, 12), blob.AsSpan(28), blob.AsSpan(12, 16), plain, userId.ToByteArray());
        return plain;
    }

    private byte[]? TryKey()
    {
        if (string.IsNullOrWhiteSpace(options.MfaEncryptionKey))
        {
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(options.MfaEncryptionKey);
        }
        catch (FormatException)
        {
            return null;
        }

        return bytes.Length == 32 ? bytes : null;
    }

    private static InvalidOperationException MissingKey() =>
        new($"Configure {SessionOptions.SectionName}:MfaEncryptionKey (32 random bytes, base64) to use two-step sign-in.");
}

/// <summary>One-time recovery codes: 10 per user, "xxxxx-xxxxx", only their SHA-256 is stored (they are random, not passwords).</summary>
public static class RecoveryCodes
{
    public const int Count = 10;
    private const string Alphabet = "abcdefghjkmnpqrstuvwxyz23456789";

    public static IReadOnlyList<string> Generate()
    {
        var codes = new List<string>(Count);
        for (var n = 0; n < Count; n++)
        {
            var chars = new char[10];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            codes.Add($"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}");
        }

        return codes;
    }

    public static string Hash(string code) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(code))));

    /// <summary>Case, spaces and dashes don't matter when typing a code.</summary>
    public static string Normalize(string code) =>
        new((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static bool LooksLikeRecoveryCode(string? code) => Normalize(code ?? "").Length == 10;
}
