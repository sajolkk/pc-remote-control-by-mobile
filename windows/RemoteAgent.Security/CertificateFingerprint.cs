using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemoteAgent.Security;

/// <summary>
/// The one definition of a device fingerprint used everywhere (§7.1).
/// </summary>
/// <remarks>
/// A fingerprint is <c>SHA-256</c> over the whole DER-encoded certificate,
/// base64url-encoded without padding.
/// <para>
/// The DER of the certificate is hashed rather than the SPKI so that the mobile
/// client can compute it from the raw bytes its TLS stack already exposes, with no
/// ASN.1 parsing and therefore no extra dependency. Certificates are long-lived and
/// rotation is an explicit, signed operation, so tying the fingerprint to the
/// certificate rather than the key costs nothing in practice.
/// </para>
/// Base64url rather than hex because it goes into a QR code, where every character
/// counts.
/// </remarks>
public static class CertificateFingerprint
{
    /// <summary>Characters of the hex form shown in UI for human comparison.</summary>
    public const int ShortFormBytes = 8;

    /// <summary>Computes the fingerprint of a certificate.</summary>
    public static string Compute(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return Compute(certificate.RawDataMemory.Span);
    }

    /// <summary>Computes the fingerprint of a DER-encoded certificate.</summary>
    public static string Compute(ReadOnlySpan<byte> derEncodedCertificate)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(derEncodedCertificate, hash);
        return Base64Url.Encode(hash);
    }

    /// <summary>
    /// Compares two fingerprints in constant time.
    /// </summary>
    /// <remarks>
    /// Fingerprints are public values, so a timing side channel here leaks nothing
    /// of consequence — but comparing them properly costs nothing and removes the
    /// need for a reader to work out whether it matters.
    /// </remarks>
    public static bool Equal(string? a, string? b)
    {
        if (a is null || b is null || a.Length != b.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a),
            System.Text.Encoding.ASCII.GetBytes(b));
    }

    /// <summary>
    /// Formats the first bytes of a fingerprint as colon-separated upper-case hex,
    /// for display in the approval dialog and the Paired Devices page.
    /// </summary>
    public static string ToShortForm(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return string.Empty;
        }

        if (!Base64Url.TryDecode(fingerprint, out byte[]? bytes))
        {
            return fingerprint.Length > 16 ? fingerprint[..16] : fingerprint;
        }

        int take = Math.Min(ShortFormBytes, bytes.Length);
        return Convert.ToHexString(bytes.AsSpan(0, take)).Chunk(2)
            .Select(static pair => new string(pair))
            .Aggregate(static (left, right) => $"{left}:{right}");
    }
}

/// <summary>
/// Base64url (RFC 4648 §5) without padding, the encoding used for every binary
/// value on the wire and in QR payloads.
/// </summary>
public static class Base64Url
{
    /// <summary>Encodes bytes as unpadded base64url.</summary>
    public static string Encode(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes unpadded base64url, returning false on malformed input.</summary>
    public static bool TryDecode(string? value, out byte[] data)
    {
        data = [];
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        string padded = value.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
            case 1:
                return false; // Not a valid base64 length.
        }

        Span<byte> buffer = new byte[(padded.Length / 4) * 3];
        if (!Convert.TryFromBase64String(padded, buffer, out int written))
        {
            return false;
        }

        data = buffer[..written].ToArray();
        return true;
    }

    /// <summary>Generates <paramref name="byteCount"/> CSPRNG bytes as base64url.</summary>
    public static string GenerateToken(int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(byteCount, 16);
        return Encode(RandomNumberGenerator.GetBytes(byteCount));
    }
}
