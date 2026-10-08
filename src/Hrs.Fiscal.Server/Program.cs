// HRS Fiscal Server — one per property.
//
// Planned modules (phase 1 skeleton; endpoints return 501 until implemented):
//   Ofis/        OFIS Cloud endpoint: receives OPERA fiscal payloads (Folio Generation, Post Payment, Check Out)
//   Mapping/     OPERA transaction codes / payment methods -> ReceiptRequest
//   Routing/     OPERA Fiscal Terminal ID -> registered HRS Fiscal Client (PosId)
//   Archive/     PostgreSQL append-only store (db/migrations), audit log, hash-chain verification
//   Queue/       offline queue, re-send of signed coupons, 48h / 10th-of-month alerts
//   Admin/       admin UI + API: status, search, reprint ("KOPJE E KUPONIT"), CSV/PDF export, reports
//   Terminals/   client registry, certificate expiry monitoring

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok", component = "hrs-fiscal-server" }));

// OFIS Cloud delivery endpoint (configured in OPERA: Fiscal Management > OFIS Cloud Configuration > End Point URL).
// Contract pending Oracle's OFIS fiscal-partner specification.
app.MapPost("/ofis/fiscal-payload", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

app.Run();
