using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RemoteAgent.Security;

/// <summary>
/// The six-digit code both screens show during code pairing.
/// </summary>
/// <remarks>
/// <para>Derived from both certificate fingerprints of the TLS connection being paired: the PC's
/// own and the phone's. Each side knows both after the handshake, so each computes the code on its
/// own and nothing secret travels. The user checks the two screens agree before allowing.</para>
///
/// <para>This is what makes pairing without a QR code safe against something on the network
/// pretending to be the PC or the phone. An impostor in the middle holds different keys, so the
/// code the phone computes differs from the one the PC shows, and it would have to find keys whose
/// code happens to match: one chance in a million per attempt, with a person approving each
/// attempt. It is the same numeric-comparison idea Bluetooth pairing uses.</para>
///
/// <para>The mobile app implements the identical derivation; the two must never diverge.</para>
/// </remarks>
public static class PairingCode
{
    private const string Domain = "PC-Remote pairing code v1";

    /// <summary>Computes the code as six digits, e.g. <c>042917</c>.</summary>
    /// <param name="pcFingerprint">The PC's certificate fingerprint (base64url SHA-256).</param>
    /// <param name="phoneFingerprint">The phone's certificate fingerprint (base64url SHA-256).</param>
    public static string Compute(string pcFingerprint, string phoneFingerprint)
    {
        ArgumentException.ThrowIfNullOrEmpty(pcFingerprint);
        ArgumentException.ThrowIfNullOrEmpty(phoneFingerprint);

        byte[] input = Encoding.UTF8.GetBytes($"{Domain}\n{pcFingerprint}\n{phoneFingerprint}");
        byte[] hash = SHA256.HashData(input);

        uint value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>The code split for reading aloud, e.g. <c>042 917</c>.</summary>
    public static string ToDisplayForm(string code) =>
        code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
}
