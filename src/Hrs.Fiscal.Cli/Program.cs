// atk-test-console — test tool for ATK's fiscalization API (TEST environment by default).
//
//   atk-test-console onboard   --nui <NUI> --fiscal-no <EDI fiscalization no> --branch <unit no> --pos <POS id> --app <ApplicationId>
//                            [--env test|prod] [--dir <profile folder>] [--location <city>]
//   atk-test-console send      [--dir <profile folder>]                 one hotel test receipt
//   atk-test-console scenarios [--dir <profile folder>]                 the full test set, writes report.md
//   atk-test-console gui       [--dir <profile folder>] [--port 5299]  browser GUI (default when started without arguments)
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
var command = args.FirstOrDefault() ?? "gui"; // double-click opens the GUI
var dir = opts.Get("dir") ?? Path.Combine(Environment.CurrentDirectory, "atk-test-profile");

try
{
    return command switch
    {
        "onboard" => await OnboardAsync(),
        "import" => await ImportAsync(),
        "send" => await SendAsync(),
        "scenarios" => await ScenariosAsync(),
        "gui" => await GuiServer.RunAsync(dir, int.TryParse(opts.Get("port"), out var port) ? port : 5299, opts.Get("no-browser") is null),
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
        atk-test-console — ATK fiscalization test tool

          onboard   --nui <NUI> --fiscal-no <EDI no> --branch <unit no> --pos <POS id> --app <ApplicationId>
                    [--env test|prod] [--dir <folder>] [--location <city>]
                    Generates a P-256 key, verifies the business with ATK, sends the CSR, saves the certificate.
          import    --key <private-key.pem> --cert <signed-certificate.pem> --app <ApplicationId> [--env test|prod]
                    [--dir <folder>] [--location <city>]
                    Uses the key and certificate exported from ATK's onboarder tool (Certificate tab › Export).
                    NUI, POS ID and branch are read from the certificate.
          gui       [--dir <folder>] [--port 5299] [--no-browser]
                    Opens the ATK Test Console in your browser (also what a double-click does).
          send      [--dir <folder>]   Sends one signed hotel test receipt and prints the ATK transaction id.
          scenarios [--dir <folder>] [--citizen-id <personal no.>]   Runs the ATK test set (sales, return, duplicates, bad signature, QR check)
                                       and writes <folder>/report.md.
        """);
    return 0;
}

async Task<int> OnboardAsync()
{
    var env = (opts.Get("env") ?? "test").ToLowerInvariant() == "prod" ? AtkEnvironment.Production : AtkEnvironment.Test;
    if (env == AtkEnvironment.Production && opts.Get("i-understand-prod") is null)
        throw new ArgumentException("Refusing to onboard against PRODUCTION from the test tool. ATK forbids testing in production.");
    var profile = await Workstation.OnboardAsync(dir, opts.Require<ulong>("nui"), opts.Require<string>("fiscal-no"),
        opts.Require<ulong>("branch"), opts.Require<ulong>("pos"), opts.Require<ulong>("app"), env, opts.Get("location"), Console.WriteLine);
    Console.WriteLine($"Done. Certificate valid until {profile.CertificateExpiresUtc:yyyy-MM-dd}. Profile saved in {dir}");
    return 0;
}

async Task<int> ImportAsync()
{
    var env = (opts.Get("env") ?? "test").ToLowerInvariant() == "prod" ? AtkEnvironment.Production : AtkEnvironment.Test;
    var profile = await Workstation.ImportAsync(dir, await File.ReadAllTextAsync(opts.Require<string>("key")),
        await File.ReadAllTextAsync(opts.Require<string>("cert")), opts.Require<ulong>("app"), env, opts.Get("fiscal-no"), opts.Get("location"));
    Console.WriteLine($"Imported: {profile.BusinessName} · NUI {profile.Nui} · branch {profile.BranchId} · POS {profile.PosId} · " +
                      $"certificate valid until {profile.CertificateExpiresUtc:yyyy-MM-dd} · {profile.Environment}");
    Console.WriteLine($"Profile saved in {dir}. Next: atk-test-console scenarios --dir \"{dir}\"");
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
    var runner = new ScenarioRunner(profile, key, Atk(profile), dir, long.TryParse(opts.Get("citizen-id"), out var cid) ? cid : 38344000000L);
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
