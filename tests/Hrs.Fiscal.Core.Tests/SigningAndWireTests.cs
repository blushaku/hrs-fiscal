using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;
using Hrs.Fiscal.Core.Signing;

namespace Hrs.Fiscal.Core.Tests;

public class SigningAndWireTests
{
    // "details" example from github.com/fiskalizimi/pos-csharp README ("Sending POS Coupons")
    private const string AtkSampleDetails =
        "CIHI0p4CENIJGAEiCVByaXNodGluZSoJS3VzaHRyaW1pMAE4wMQHQhAxMjM0NTY3ODkwMTIzNDU2SAFQnL/DrgZaJAoKdWplIHJ1Z292ZRCWARoEY29wZSUAAEBAKMIDMgFDOgJUVFohCgdzZW5kdmlxEKwCGgRjb3BlJQAAAEAo2AQyAUU6AlRUWh0KBGJ1a2UQUBoEY29wZSUAAIBAKMACMgFEOgJUVFoqChBtYWNoaWF0byBlIG1hZGhlEJYBGgRjb3BlJQAAQEAowgMyAUU6AlRUYgUIARD0A2IFCAIQ6AdiBQgDEMACaJwOcgYKAUMQwgNyCAoBRBDAAhgacgkKAUUQmggYvQF41wGAAcUM";

    [Fact]
    public void Atk_sample_payload_round_trips_through_our_protobuf_model()
    {
        var bytes = Convert.FromBase64String(AtkSampleDetails);
        var coupon = PosCoupon.Parser.ParseFrom(bytes);

        Assert.Equal("Prishtine", coupon.Location);
        Assert.Equal("Kushtrimi", coupon.OperatorId);
        Assert.Equal("1234567890123456", coupon.VerificationNo);
        Assert.Equal(CouponType.Sale, coupon.Type);
        Assert.Equal(4, coupon.Items.Count);
        Assert.Equal(3, coupon.Payments.Count);
        Assert.Equal(3, coupon.TaxGroups.Count);
        Assert.Equal(AtkSampleDetails, Convert.ToBase64String(coupon.ToByteArray()));
    }

    [Fact]
    public void Signs_base64_text_with_der_ecdsa_and_verifies()
    {
        using var key = PemSigningKey.Generate();
        var coupon = new CouponBuilder().Build(CouponBuilderTests.AtkSampleRequest());

        var signed = new CouponSigner(key).Sign(coupon);

        Assert.Equal(Convert.ToBase64String(coupon.ToByteArray()), signed.Details);
        Assert.Equal(0x30, Convert.FromBase64String(signed.Signature)[0]); // DER SEQUENCE
        Assert.True(CouponSigner.Verify(signed, key.PublicKey));
        Assert.False(CouponSigner.Verify(signed with { Details = signed.Details.Replace('A', 'B') }, key.PublicKey));
    }

    [Fact]
    public void Qr_string_is_citizen_coupon_pipe_signature()
    {
        using var key = PemSigningKey.Generate();
        var citizen = CouponBuilder.ToCitizenCoupon(new CouponBuilder().Build(CouponBuilderTests.AtkSampleRequest()));

        var qr = new CouponSigner(key).Sign(citizen).ToQrString();

        var parts = qr.Split('|');
        Assert.Equal(2, parts.Length);
        Assert.Equal(citizen, CitizenCoupon.Parser.ParseFrom(Convert.FromBase64String(parts[0])));
    }

    [Fact]
    public void Pem_key_round_trips()
    {
        using var key = PemSigningKey.Generate();
        using var again = PemSigningKey.FromPem(key.ExportPrivateKeyPem());
        var sig = again.SignData("x"u8.ToArray());
        Assert.True(key.PublicKey.VerifyData("x"u8.ToArray(), sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public void Csr_has_atk_subject_layout()
    {
        using var key = PemSigningKey.Generate();
        var pem = CsrFactory.CreatePem(key.Ecdsa, businessId: 812345678, posId: 11, branchId: 5130484, businessName: "Hotel Test SH.P.K.");

        var csr = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256);
        var dn = csr.SubjectName.Name;

        Assert.Contains("C=RKS", dn);
        Assert.Contains("O=812345678", dn);
        Assert.Contains("OU=11", dn);
        Assert.Contains("L=5130484", dn);
        Assert.Contains("CN=Hotel Test SH.P.K.", dn);
    }
}
