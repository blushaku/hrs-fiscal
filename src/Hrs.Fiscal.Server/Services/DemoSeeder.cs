using Dapper;
using Hrs.Fiscal.Core.Atk;
using Hrs.Fiscal.Core.Receipts;
using Hrs.Fiscal.Core.Signing;
using Hrs.Fiscal.Server.Data;
using Npgsql;

namespace Hrs.Fiscal.Server.Services;

/// <summary>
/// Fills an EMPTY database with a demo property, workstations, mappings, users and genuinely signed receipts
/// (throw-away in-memory keys), so the admin UI can be evaluated before FLIP and ATK are connected.
/// Refuses to run when receipts already exist.
/// </summary>
public sealed class DemoSeeder(NpgsqlDataSource db, SettingsStore settings, UserStore users, ReceiptArchive archive, AuditLog audit, PropertyClock clock)
{
    private const string Actor = "demo-seed";

    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            if (await conn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM fiscal.receipt)"))
                throw new InvalidOperationException("Database already contains receipts; demo data is only for an empty database.");
        }

        if (await settings.BusinessAsync(ct) is null)
            await settings.SaveBusinessAsync(new BusinessInfo
            {
                Nui = 812345678, Name = "Hotel Demo SH.P.K.", FiscalizationNo = "DEMO-0001", VatNo = "330012345",
                BranchId = 5130484, BranchName = "Hotel Demo Prishtina", Location = "Prishtinë",
                Address = "Rr. Nëna Terezë 10, Prishtinë", OperaHotelCode = "DEMOPR",
            }, Actor, ct);

        var business = (await settings.BusinessAsync(ct))!;
        var terminals = new[] { ("FO1", "FRONTDESK-01", 11L), ("FO2", "FRONTDESK-02", 12L), ("NA1", "NIGHTAUDIT-01", 13L) };
        foreach (var (opera, host, pos) in terminals)
            await settings.SaveTerminalAsync(new TerminalEdit
            {
                PosId = pos, OperaTerminalId = opera, Hostname = host, ClientEndpoint = $"https://{host.ToLowerInvariant()}:5100",
                Status = "active", Description = "Demo workstation",
            }, Actor, ct);
        await using (var conn = await db.OpenConnectionAsync(ct))
        {
            await conn.ExecuteAsync("UPDATE fiscal.terminal SET certificate_expires = now() + interval '200 days' WHERE pos_id IN (11, 13)");
            await conn.ExecuteAsync("UPDATE fiscal.terminal SET certificate_expires = now() + interval '23 days' WHERE pos_id = 12");
        }
        var terminalRows = await settings.TerminalsAsync(ct);

        var catalog = new (string Code, string Name, string Unit, string Cat, string Vat, decimal Price)[]
        {
            ("1000", "Room night", "nate", "HT", "D", 110m),
            ("1010", "City tax", "nate", "HT", "A", 1m),
            ("2000", "Breakfast", "cope", "UR", "E", 9.5m),
            ("2100", "Restaurant", "cope", "UR", "E", 24.8m),
            ("3000", "Minibar", "cope", "UR", "E", 6.45m),
            ("4000", "Laundry", "cope", "SUA", "E", 12m),
        };
        foreach (var c in catalog)
            await settings.SaveTrxMappingAsync(new TrxMapping { TrxCode = c.Code, ItemName = c.Name, Unit = c.Unit, Category = c.Cat, VatLetter = c.Vat }, Actor, ct);
        await settings.SavePaymentMappingAsync(new PaymentMapping { PaymentCode = "CA", Description = "Cash EUR", AtkPaymentType = 1 }, Actor, ct);
        await settings.SavePaymentMappingAsync(new PaymentMapping { PaymentCode = "VA", Description = "Visa", AtkPaymentType = 2 }, Actor, ct);
        await settings.SavePaymentMappingAsync(new PaymentMapping { PaymentCode = "MC", Description = "Mastercard", AtkPaymentType = 2 }, Actor, ct);

        if (!(await users.AllAsync(ct)).Any(u => u.Username == "supervisor"))
        {
            if (!(await users.AllAsync(ct)).Any(u => u.Role == Roles.Admin))
                await users.CreateAsync("admin", "Administrator", Roles.Admin, "demo-password-0", Actor, ct);
            await users.CreateAsync("supervisor", "Arta Krasniqi", Roles.Supervisor, "demo-password-1", Actor, ct);
            await users.CreateAsync("cashier", "Blerim Hoxha", Roles.Cashier, "demo-password-2", Actor, ct);
            await users.CreateAsync("auditor", "External auditor", Roles.Auditor, "demo-password-3", Actor, ct);
        }

        var keys = terminalRows.ToDictionary(t => t.Id, _ => PemSigningKey.Generate());
        var builder = new CouponBuilder();
        var rnd = new Random(42);
        var guests = new[] { "Gashi, Arben", "Müller, Jana", "Rossi, Marco", "Berisha, Drita", "Smith, Daniel", "Krasniqi, Liridon", "Novak, Petra", "Hoti, Valon" };
        var cashiers = new[] { "Arta K.", "Blerim H." };
        var count = 0;
        var issued = new List<(ulong CouponId, decimal Total, TerminalEdit T)>();
        var start = clock.Time.GetUtcNow().UtcDateTime.AddDays(-6);

        for (var i = 0; i < 46; i++)
        {
            var t = terminalRows[rnd.Next(terminalRows.Count)];
            var at = start.AddMinutes(i * 185 + rnd.Next(0, 90));
            var isLast = i >= 44;                                  // last two: still waiting (offline)
            var nights = rnd.Next(1, 5);
            var lines = new List<ReceiptLine>
            {
                Line(catalog[0], nights), Line(catalog[1], nights * 2),
            };
            if (rnd.Next(2) == 0) lines.Add(Line(catalog[2], nights * 2));
            if (rnd.Next(3) == 0) lines.Add(Line(catalog[3], rnd.Next(1, 4)));
            if (rnd.Next(3) == 0) lines.Add(Line(catalog[4], rnd.Next(1, 6)));
            var gross = lines.Sum(l => l.UnitPrice * l.Quantity);
            var folio = 48100 + i;
            var request = Request(t, business, at, lines, gross, rnd.Next(3) == 0 ? PaymentType.Cash : PaymentType.CreditCard, cashiers[rnd.Next(2)]);
            await AppendAsync(request, t, keys[t.Id], at, isLast, $"Folio {folio} · {guests[rnd.Next(guests.Length)]}", i == 30);
            issued.Add((request.CouponId, gross, t));
            count++;

            if (i == 20)
            {
                // A return referring to an earlier sale (corrections are made only with return coupons).
                var original = issued[^3];
                var ret = Request(original.T, business, at.AddMinutes(20), [Line(catalog[4], 2)], 12.9m, PaymentType.Cash, "Arta K.") with
                {
                    Type = CouponType.Return, ReferenceNo = original.CouponId,
                };
                await AppendAsync(ret, original.T, keys[original.T.Id], at.AddMinutes(20), false, "Return · minibar correction", false);
                count++;
            }
        }

        await audit.WriteAsync("supervisor", AuditLog.Actions.Login, details: new { demo = true }, ct: ct);
        return count;

        static ReceiptLine Line((string Code, string Name, string Unit, string Cat, string Vat, decimal Price) c, decimal qty) =>
            new() { Name = c.Name, Unit = c.Unit, Quantity = qty, UnitPrice = c.Price, TaxRate = c.Vat, Category = c.Cat };

        ReceiptRequest Request(TerminalEdit t, BusinessInfo b, DateTime at, List<ReceiptLine> lines, decimal gross, PaymentType pay, string op) => new()
        {
            BusinessId = (ulong)b.Nui, BranchId = (ulong)b.BranchId, PosId = (ulong)t.PosId, ApplicationId = 9999,
            CouponId = (ulong)archive.NextCouponIdAsync(b.BranchId, ct).GetAwaiter().GetResult(),
            VerificationNo = VerificationNumber.New(), Location = b.Location, OperatorId = op,
            IssuedAt = new DateTimeOffset(at, TimeSpan.Zero), Lines = lines,
            Payments = [new ReceiptPayment(pay, gross)],
        };
    }

    private async Task AppendAsync(ReceiptRequest req, TerminalEdit t, PemSigningKey key, DateTime at, bool offline, string document, bool rejectedOnce)
    {
        var coupon = new CouponBuilder().Build(req);
        var signer = new CouponSigner(key);
        var signed = signer.Sign(coupon);
        var qr = signer.Sign(CouponBuilder.ToCitizenCoupon(coupon)).ToQrString();
        var payload = await archive.AppendSourcePayloadAsync("MANUAL", $"demo-{req.CouponId}", "text/plain",
            "[demo data — real receipts store the FLIP payload exactly as received]");
        var id = await archive.AppendReceiptAsync(coupon, signed, qr, t.Id, offline, payload, document);
        await audit.WriteAsync("server", AuditLog.Actions.ReceiptIssued, "receipt", req.CouponId.ToString(),
            new { document, total = coupon.Total }, t.Id);

        if (offline)
        {
            await archive.AppendTransmissionAsync(id, $"client:{t.Hostname}", AtkSendResult.Transient("ATK not reachable (timeout after 10 s)"), at.AddSeconds(10));
            await audit.WriteAsync("server", AuditLog.Actions.QueuedOffline, "receipt", req.CouponId.ToString(), new { reason = "ATK unreachable" }, t.Id);
            return;
        }
        if (rejectedOnce)
            await archive.AppendTransmissionAsync(id, $"client:{t.Hostname}", AtkSendResult.Transient("HTTP 503 Service Unavailable", 503), at.AddSeconds(1));
        var tx = (ulong)(9001870000 + req.CouponId % 100000);
        await archive.AppendTransmissionAsync(id, rejectedOnce ? "server" : $"client:{t.Hostname}",
            new AtkSendResult(AtkOutcome.Accepted, tx, "ok", 200), at.AddSeconds(rejectedOnce ? 125 : 1));
        await audit.WriteAsync($"client:{t.Hostname}", AuditLog.Actions.ReceiptAccepted, "receipt", req.CouponId.ToString(), new { atkTransaction = tx }, t.Id);
    }
}
