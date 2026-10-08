using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hrs.Fiscal.Core.Signing;

/// <summary>
/// Builds the PKCS#10 CSR ATK's CA expects:
///   C = RKS, O = BusinessId (NUI), OU = PosId, L = BranchId, CN = business name.
/// The CSR is signed with the workstation's own key; only the public key leaves the machine.
/// </summary>
public static class CsrFactory
{
    public static string CreatePem(ECDsa key, ulong businessId, ulong posId, ulong branchId, string businessName, string country = "RKS")
    {
        var subject = new X500DistinguishedNameBuilder();
        subject.Add("2.5.4.6", country, UniversalTagNumber.PrintableString); // C; ATK uses 3-letter "RKS", which AddCountryOrRegion rejects
        subject.AddOrganizationName(businessId.ToString());
        subject.AddOrganizationalUnitName(posId.ToString());
        subject.AddLocalityName(branchId.ToString());
        subject.AddCommonName(businessName);

        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);
        return request.CreateSigningRequestPem();
    }
}
