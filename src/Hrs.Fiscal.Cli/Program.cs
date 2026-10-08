// hrs-fiscal-cli — test tool for ATK's fiscalization API (TEST environment by default).
//
//   hrs-fiscal-cli onboard   --nui <NUI> --fiscal-no <EDI fiscalization no> --branch <unit no> --pos <POS id> --app <ApplicationId>
//                            [--env test|prod] [--dir <profile folder>] [--location <city>]
//   hrs-fiscal-cli send      [--dir <profile folder>]                 one hotel test receipt
//   hrs-fiscal-cli scenarios [--dir <profile folder>]                 the full test set, writes report.md
//
// The profile folder holds this test workstation's identity: profile.json, private-key.pem, certificate.pem.
// TEST ONLY: here the key is kept as a PEM file. The HRS Fiscal Client keeps production keys non-exportable (CNG/TPM).

using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Hrs.Fiscal.Cli;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Signing;

var opts = Args.Parse(args.Skip(1));
var command = args.FirstOrDefault() ?? "help";
var dir = opts.Get("dir") ?? Path.Combine(Environment.CurrentDirectory, "atk-test-profile");

try
{
    return command switch
    {
        "onboard" => await OnboardAsync(),
        "import" => await ImportAsync(),
        "send" => await SendAsync(),
        "scenarios" => await ScenariosAsync(),
        _ => Help(),
    };
}
catch (Exception ex) when (ex is AtkApiException or ArgumentException or FileNotFoundException or HttpRequestException or FormatException or System.Security.Cryptography.CryptographicException)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

int Help()
{
    Console.WriteLine("""
        hrs-fiscal-cli — ATK fiscalization test tool

          onboard   --nui <NUI> --fiscal-no <EDI no> --branch <unit no> --pos <POS id> --app <ApplicationId>
                    [--env test|prod] [--dir <folder>] [--location <city>]
                    Generates a P-256 key, verifies the business with ATK, sends the CSR, saves the certificate.
          import    --key <private-key.pem> --cert <signed-certificate.pem> --app <ApplicationId> [--env test|prod]
                    [--dir <folder>] [--location <city>]
                    Uses the key and certificate exported from ATK's onboarder tool (Certificate tab › Export).
                    NUI, POS ID and branch are read from the certificate.
          send      [--dir <folder>]   Sends one signed hotel test receipt and prints the ATK transaction id.
          scenarios [--dir <folder>]   Runs the ATK test set (sales, return, duplicates, bad signature, QR check)
                                       and writes <folder>/report.md.
        """);
    return 0;
}

async Task<int> OnboardAsync()
{
    var profile = new Profile
    {
        Environment = (opts.Get("env") ?? "test").ToLowerInvariant() == "prod" ? AtkEnvironment.Production : AtkEnvironment.Test,
        Nui = opts.Require<ulong>("nui"),
        FiscalizationNo = opts.Require<string>("fiscal-no"),
        BranchId = opts.Require<ulong>("branch"),
        PosId = opts.Require<ulong>("pos"),
        ApplicationId = opts.Require<ulong>("app"),
        Location = opts.Get("location") ?? "Prishtinë",
    };
    if (profile.Environment == AtkEnvironment.Production && opts.Get("i-understand-prod") is null)
        throw new ArgumentException("Refusing to onboard against PRODUCTION from the test tool. ATK forbids testing in production.");

    Directory.CreateDirectory(dir);
    var atk = Atk(profile);

    Console.WriteLine($"1/3 Verifying business {profile.Nui} with ATK ({profile.Environment})…");
    var verify = await atk.VerifyAsync(profile.Nui, new VerifyRequest(profile.FiscalizationNo, profile.PosId, profile.BranchId, profile.ApplicationId));
    profile.BusinessName = verify.BusinessName;
    Console.WriteLine($"    business: {verify.BusinessName}");

    Console.WriteLine("2/3 Generating P-256 key and CSR…");
    using var key = PemSigningKey.Generate();
    var csr = CsrFactory.CreatePem(key.Ecdsa, profile.Nui, profile.PosId, profile.BranchId, verify.BusinessName);

    Console.WriteLine("3/3 Sending CSR to ATK CA…");
    var cert = await atk.SignCsrAsync(new SignCsrRequest(verify.BusinessName, profile.Nui, profile.BranchId, verify.VerificationCodeText,
        profile.PosId, profile.ApplicationId, csr));

    await File.WriteAllTextAsync(Path.Combine(dir, "private-key.pem"), key.ExportPrivateKeyPem());
    await File.WriteAllTextAsync(Path.Combine(dir, "certificate.pem"), cert);
    profile.CertificateExpiresUtc = CertificateInfo.ExpiresUtc(cert);
    await profile.SaveAsync(dir);

    Console.WriteLine($"Done. Certificate valid until {profile.CertificateExpiresUtc:yyyy-MM-dd}. Profile saved in {dir}");
    return 0;
}

async Task<int> ImportAsync()
{
    var keyPem = await File.ReadAllTextAsync(opts.Require<string>("key"));
    var certPem = await File.ReadAllTextAsync(opts.Require<string>("cert"));
    using var key = PemSigningKey.FromPem(keyPem);
    var cert = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(certPem);

    // The certificate must belong to this key.
    using var certKey = cert.GetECDsaPublicKey() ?? throw new ArgumentException("Certificate has no ECDSA public key.");
    var probe = "hrs-fiscal-import"u8.ToArray();
    if (!certKey.VerifyData(probe, key.SignData(probe), System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence))
        throw new ArgumentException("The private key does not match the certificate.");

    // ATK subject layout: C=RKS, O=<NUI>, OU=<POS ID>, L=<branch>, CN=<business name>
    string Part(string oid) => cert.SubjectName.EnumerateRelativeDistinguishedNames()
        .FirstOrDefault(r => r.GetSingleElementType().Value == oid)?.GetSingleElementValue() ?? "";
    var profile = new Profile
    {
        Environment = (opts.Get("env") ?? "test").ToLowerInvariant() == "prod" ? AtkEnvironment.Production : AtkEnvironment.Test,
        Nui = ulong.Parse(Part("2.5.4.10"), CultureInfo.InvariantCulture),
        PosId = ulong.Parse(Part("2.5.4.11"), CultureInfo.InvariantCulture),
        BranchId = ulong.Parse(Part("2.5.4.7"), CultureInfo.InvariantCulture),
        BusinessName = Part("2.5.4.3"),
        ApplicationId = opts.Require<ulong>("app"),
        FiscalizationNo = opts.Get("fiscal-no") ?? "",
        Location = opts.Get("location") ?? "Prishtinë",
        CertificateExpiresUtc = cert.NotAfter.ToUniversalTime(),
    };

    Directory.CreateDirectory(dir);
    await File.WriteAllTextAsync(Path.Combine(dir, "private-key.pem"), keyPem);
    await File.WriteAllTextAsync(Path.Combine(dir, "certificate.pem"), certPem);
    await profile.SaveAsync(dir);
    Console.WriteLine($"Imported: {profile.BusinessName} · NUI {profile.Nui} · branch {profile.BranchId} · POS {profile.PosId} · " +
                      $"certificate valid until {profile.CertificateExpiresUtc:yyyy-MM-dd} · {profile.Environment}");
    Console.WriteLine($"Profile saved in {dir}. Next: hrs-fiscal-cli scenarios --dir \"{dir}\"");
    return 0;
}

async Task<int> SendAsync()
{
    var profile = await Profile.LoadAsync(dir);
    using var key = PemSigningKey.FromPem(await File.ReadAllTextAsync(Path.Combine(dir, "private-key.pem")));
    var runner = new ScenarioRunner(profile, key, Atk(profile), dir);
    var r = await runner.SaleAsync("single sale", ScenarioRunner.HotelStay());
    Console.WriteLine($"{r.Outcome} · HTTP {r.HttpStatus} · transaction {r.TransactionId} · {r.Message}");
    return r.Outcome == AtkOutcome.Accepted ? 0 : 1;
}

async Task<int> ScenariosAsync()
{
    var profile = await Profile.LoadAsync(dir);
    using var key = PemSigningKey.FromPem(await File.ReadAllTextAsync(Path.Combine(dir, "private-key.pem")));
    var runner = new ScenarioRunner(profile, key, Atk(profile), dir);
    var results = await runner.RunAllAsync();

    var md = new StringBuilder();
    md.AppendLine($"# ATK {profile.Environment} test run — {DateTime.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}");
    md.AppendLine();
    md.AppendLine($"NUI {profile.Nui} · unit {profile.BranchId} · POS {profile.PosId} · ApplicationId {profile.ApplicationId} · {profile.BusinessName}");
    md.AppendLine();
    md.AppendLine("| # | Scenario | Expected | HTTP | Result | ATK transaction | Message | Coupon id |");
    md.AppendLine("|---|---|---|---|---|---|---|---|");
    var i = 0;
    foreach (var r in results)
        md.AppendLine($"| {++i} | {r.Name} | {r.Expected} | {r.HttpStatus} | {(r.AsExpected ? "✅ " : "⚠️ ")}{r.Outcome} | {r.TransactionId} | {r.Message?.Replace("|", "/").Replace("\n", " ")} | {r.CouponId} |");
    md.AppendLine();
    md.AppendLine("Signed payloads and QR codes for each scenario are in the `runs/` folder.");
    var report = Path.Combine(dir, "report.md");
    await File.WriteAllTextAsync(report, md.ToString());

    Console.WriteLine(md.ToString());
    Console.WriteLine($"Report: {report}");
    return results.All(r => r.AsExpected) ? 0 : 2;
}

static AtkClient Atk(Profile p) => new(new HttpClient
{
    BaseAddress = AtkEndpoints.BaseUri(p.Environment),
    Timeout = TimeSpan.FromSeconds(20),
});

namespace Hrs.Fiscal.Cli
{
    public sealed class Profile
    {
        public AtkEnvironment Environment { get; set; }
        public ulong Nui { get; set; }
        public string FiscalizationNo { get; set; } = "";
        public ulong BranchId { get; set; }
        public ulong PosId { get; set; }
        public ulong ApplicationId { get; set; }
        public string BusinessName { get; set; } = "";
        public string Location { get; set; } = "";
        public DateTime? CertificateExpiresUtc { get; set; }

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public Task SaveAsync(string dir) => File.WriteAllTextAsync(Path.Combine(dir, "profile.json"), JsonSerializer.Serialize(this, Json));

        public static async Task<Profile> LoadAsync(string dir)
        {
            var path = Path.Combine(dir, "profile.json");
            if (!File.Exists(path)) throw new FileNotFoundException($"No profile in {dir}. Run 'onboard' first.");
            return JsonSerializer.Deserialize<Profile>(await File.ReadAllTextAsync(path))!;
        }
    }

    public sealed class Args
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

        public static Args Parse(IEnumerable<string> args)
        {
            var a = new Args();
            string? key = null;
            foreach (var arg in args)
            {
                if (arg.StartsWith("--")) { key = arg[2..]; a._values[key] = ""; }
                else if (key is not null) { a._values[key] = arg; key = null; }
            }
            return a;
        }

        public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : null;

        public T Require<T>(string name)
        {
            var v = Get(name);
            if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException($"--{name} is required.");
            return (T)Convert.ChangeType(v, typeof(T), CultureInfo.InvariantCulture);
        }
    }
}
