using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Hrs.Fiscal.Core;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;
using QRCoder;

namespace Hrs.Fiscal.Cli;

/// <summary>
/// ATK Test Console: a local browser GUI over the same functions as the command line.
/// Listens on 127.0.0.1 only. Every API call must carry the per-session token embedded in the page,
/// so other websites open in the same browser cannot drive it.
/// </summary>
public static class GuiServer
{
    private static readonly SemaphoreSlim Busy = new(1, 1);

    public static async Task<int> RunAsync(string dir, int port, bool openBrowser)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        var app = builder.Build();

        var html = Resource("index.html").Replace("__TOKEN__", token).Replace("__DIR__", JsonEncodedText.Encode(Path.GetFullPath(dir)).ToString());
        app.MapGet("/", () => Results.Content(html, "text/html; charset=utf-8"));
        app.MapGet("/logo.svg", () => Results.Content(Resource("mark.svg"), "image/svg+xml"));

        var api = app.MapGroup("/api").AddEndpointFilter(async (ctx, next) =>
        {
            if (ctx.HttpContext.Request.Headers["X-Console-Token"] != token) return Results.StatusCode(StatusCodes.Status403Forbidden);
            try { return await next(ctx); }
            catch (Exception ex) when (ex is AtkApiException or ArgumentException or FormatException or FileNotFoundException
                                           or HttpRequestException or TaskCanceledException or CryptographicException or FiscalValidationException)
            {
                var msg = ex is FiscalValidationException fv ? string.Join(" · ", fv.Errors) : ex.Message;
                return Results.BadRequest(new { error = msg });
            }
        });

        api.MapGet("/state", async () => new
        {
            dir = Path.GetFullPath(dir),
            profile = File.Exists(Path.Combine(dir, "profile.json")) ? await Profile.LoadAsync(dir) : null,
        });

        api.MapPost("/import", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            var keyFile = form.Files["key"] ?? throw new ArgumentException("Choose the private key file (private-key.pem).");
            var certFile = form.Files["cert"] ?? throw new ArgumentException("Choose the certificate file (signed-certificate.pem).");
            using var kr = new StreamReader(keyFile.OpenReadStream());
            using var cr = new StreamReader(certFile.OpenReadStream());
            var profile = await Workstation.ImportAsync(dir, await kr.ReadToEndAsync(), await cr.ReadToEndAsync(),
                ParseU(form["app"], "ApplicationId"), AtkEnvironment.Test, form["fiscalNo"], form["location"]);
            return Results.Ok(profile);
        });

        api.MapPost("/onboard", async (OnboardInput i) =>
        {
            var log = new List<string>();
            var profile = await Workstation.OnboardAsync(dir, i.Nui, i.FiscalNo ?? "", i.Branch, i.Pos, i.App, AtkEnvironment.Test, i.Location, log.Add);
            return Results.Ok(new { profile, log });
        });

        api.MapPost("/send", async (SendInput i) =>
        {
            var (profile, key) = await Workstation.LoadAsync(dir);
            using (key)
            {
                var runner = new ScenarioRunner(profile, key, Workstation.Atk(profile.Environment), dir);
                var lines = i.Lines.Select(l => new ReceiptLine
                {
                    Name = l.Name, Unit = string.IsNullOrWhiteSpace(l.Unit) ? "cope" : l.Unit, Quantity = l.Qty, UnitPrice = l.Price,
                    Discount = l.Discount, TaxRate = l.Vat, Category = l.Category,
                }).ToList();
                var payments = i.Payments.Select(p => new ReceiptPayment(p.Type, p.Amount)).ToList();
                var sent = await runner.SendCustomAsync(lines, payments, i.Type == "return" ? CouponType.Return : CouponType.Sale,
                    i.Reference, string.IsNullOrWhiteSpace(i.Operator) ? "Test operator" : i.Operator);
                return Results.Ok(View(sent.Coupon, sent.Payload.Details, sent.Payload.Signature, sent.Qr,
                    new { sent.Outcome, sent.HttpStatus, transactionId = sent.TransactionId?.ToString(), sent.Message, sentAt = sent.SentAt }));
            }
        });

        api.MapPost("/verify", async (VerifyInput i) =>
        {
            var profile = await Profile.LoadAsync(dir);
            var (status, body) = await ScenarioRunner.VerifyQrRawAsync(profile.Environment, i.Qr, i.CitizenId);
            return Results.Ok(new { status, body });
        });

        api.MapPost("/scenarios", async (ScenarioInput i) =>
        {
            if (!await Busy.WaitAsync(0)) return Results.Conflict(new { error = "A test run is already in progress." });
            try
            {
                var (profile, key) = await Workstation.LoadAsync(dir);
                using (key)
                {
                    var runner = new ScenarioRunner(profile, key, Workstation.Atk(profile.Environment), dir, i.CitizenId ?? 38344000000L);
                    var results = await runner.RunAllAsync();
                    return Results.Ok(results.Select(r => new
                    {
                        r.Name, r.Expected, outcome = r.Outcome.ToString(), r.HttpStatus, transactionId = r.TransactionId?.ToString(),
                        r.Message, couponId = r.CouponId.ToString(), r.AsExpected,
                    }));
                }
            }
            finally { Busy.Release(); }
        });

        api.MapGet("/history", () =>
        {
            var runs = Path.Combine(dir, "runs");
            if (!Directory.Exists(runs)) return Results.Ok(Array.Empty<object>());
            var items = Directory.GetFiles(runs, "*.json", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(300)
                .Select(f =>
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(f.FullName));
                    var c = JsonParser.Default.Parse<PosCoupon>(doc.RootElement.GetProperty("coupon").GetString()!);
                    doc.RootElement.TryGetProperty("atk", out var atk);
                    return new
                    {
                        id = Path.GetRelativePath(runs, f.FullName).Replace('\\', '/'),
                        scenario = doc.RootElement.GetProperty("scenario").GetString(),
                        at = f.LastWriteTime,
                        couponId = c.CouponId.ToString(),
                        type = c.Type.ToString(),
                        total = c.Total,
                        outcome = atk.ValueKind == JsonValueKind.Object ? atk.GetProperty("Outcome").GetString() : null,
                    };
                }).ToList();
            return Results.Ok(items);
        });

        api.MapGet("/history/{**id}", (string id) =>
        {
            var runs = Path.GetFullPath(Path.Combine(dir, "runs"));
            var file = Path.GetFullPath(Path.Combine(runs, id));
            if (!file.StartsWith(runs, StringComparison.Ordinal) || !file.EndsWith(".json") || !File.Exists(file)) return Results.NotFound();
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var root = doc.RootElement;
            var c = JsonParser.Default.Parse<PosCoupon>(root.GetProperty("coupon").GetString()!);
            object? atk = root.TryGetProperty("atk", out var a) && a.ValueKind == JsonValueKind.Object ? JsonSerializer.Deserialize<object>(a.GetRawText()) : null;
            return Results.Ok(View(c, root.GetProperty("details").GetString()!, root.GetProperty("signature").GetString()!,
                root.GetProperty("qr").GetString()!, atk));
        });

        await app.StartAsync();
        var url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"Opera Cloud Fiscal Solution - ATK Test Console: {url}   (profile folder: {Path.GetFullPath(dir)})");
        Console.WriteLine("Keep this window open while you use the console. Press Ctrl+C to stop.");
        if (openBrowser)
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* no browser available */ }
        await app.WaitForShutdownAsync();
        return 0;
    }

    private static object View(PosCoupon c, string details, string signature, string qr, object? atk)
    {
        using var data = QRCodeGenerator.GenerateQrCode(qr, QRCodeGenerator.ECCLevel.M);
        return new
        {
            couponId = c.CouponId.ToString(),
            verificationNo = c.VerificationNo,
            sefId = $"{c.BranchId}-{c.BusinessId}-{c.PosId}",
            type = c.Type.ToString(),
            referenceNo = c.ReferenceNo == 0 ? null : c.ReferenceNo.ToString(),
            time = DateTimeOffset.FromUnixTimeSeconds(c.Time).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            operatorId = c.OperatorId,
            items = c.Items.Select(i => new { i.Name, i.Unit, i.Quantity, price = i.Price, total = i.Total, i.TaxRate, category = i.Type }),
            taxGroups = c.TaxGroups.Select(g => new { g.TaxRate, g.TotalForTax, g.TotalTax }),
            payments = c.Payments.Select(p => new { type = p.Type.ToString(), p.Amount }),
            c.Total, c.TotalTax, c.TotalNoTax, c.TotalDiscount,
            details, signature, qr,
            qrPng = Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(6)),
            atk,
        };
    }

    private static ulong ParseU(string? v, string name) =>
        ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new ArgumentException($"{name} must be a number.");

    private static string Resource(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Hrs.Fiscal.Cli.Gui.{name}")
                      ?? throw new InvalidOperationException($"Missing resource {name}");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public sealed record OnboardInput(ulong Nui, string? FiscalNo, ulong Branch, ulong Pos, ulong App, string? Location);
    public sealed record LineInput(string Name, string? Unit, decimal Qty, decimal Price, decimal Discount, string Vat, string Category);
    public sealed record PaymentInput(PaymentType Type, decimal Amount);
    public sealed record SendInput(string Type, ulong Reference, string? Operator, List<LineInput> Lines, List<PaymentInput> Payments);
    public sealed record VerifyInput(string Qr, long CitizenId);
    public sealed record ScenarioInput(long? CitizenId);
}
