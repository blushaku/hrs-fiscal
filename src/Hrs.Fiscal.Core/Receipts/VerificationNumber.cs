using System.Security.Cryptography;

namespace Hrs.Fiscal.Core.Receipts;

/// <summary>
/// Generates the NUIKF / VerificationNo: unique per coupon, alphanumeric, max 16 chars.
/// Random (unguessable) so citizens cannot enumerate other receipts; uniqueness is
/// enforced by a UNIQUE constraint in the archive and a retry on collision.
/// Alphabet excludes look-alikes (0/O, 1/I/L) because it is printed and may be typed by hand.
/// </summary>
public static class VerificationNumber
{
    private const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ"; // 31 symbols, ~79 bits for 16 chars

    public static string New(int length = 16)
    {
        if (length is < 8 or > 16) throw new ArgumentOutOfRangeException(nameof(length));
        return RandomNumberGenerator.GetString(Alphabet, length);
    }
}
