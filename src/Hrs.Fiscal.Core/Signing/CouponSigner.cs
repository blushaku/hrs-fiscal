using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Hrs.Fiscal.Core.Atk;

namespace Hrs.Fiscal.Core.Signing;

/// <summary>A signed coupon ready for transmission: Base64 protobuf + Base64 DER ECDSA signature.</summary>
public sealed record SignedPayload(string Details, string Signature)
{
    /// <summary>QR content format defined by ATK: "&lt;base64 CitizenCoupon&gt;|&lt;base64 signature&gt;".</summary>
    public string ToQrString() => $"{Details}|{Signature}";
}

/// <summary>
/// Signs ATK coupons exactly as the ATK reference implementations do:
///   details   = Base64(protobuf bytes)
///   signature = Base64( ECDSA-P256-SHA256( UTF8(details) ) ), DER (RFC 3279) encoded
/// Note: the bytes signed are the Base64 TEXT, not the raw protobuf.
/// DER is used because ATK's examples and Go reference use it (.NET defaults to IEEE P1363).
/// </summary>
public sealed class CouponSigner
{
    private readonly ISigningKey _key;

    public CouponSigner(ISigningKey key) => _key = key;

    public SignedPayload Sign(PosCoupon coupon) => SignMessage(coupon);

    public SignedPayload Sign(CitizenCoupon coupon) => SignMessage(coupon);

    private SignedPayload SignMessage(IMessage message)
    {
        var details = Convert.ToBase64String(message.ToByteArray());
        var signature = _key.SignData(Encoding.UTF8.GetBytes(details));
        return new SignedPayload(details, Convert.ToBase64String(signature));
    }

    /// <summary>Verifies a payload against a public key (used in tests and for archive integrity checks).</summary>
    public static bool Verify(SignedPayload payload, ECDsa publicKey)
    {
        var data = Encoding.UTF8.GetBytes(payload.Details);
        var sig = Convert.FromBase64String(payload.Signature);
        return publicKey.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
    }
}
