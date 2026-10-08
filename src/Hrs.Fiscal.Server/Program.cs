// HRS Fiscal Server — one per property.
//
// Planned modules (phase 1 skeleton; endpoints return 501 until implemented):
//   Flip/        LAN endpoint called by Oracle FLIP (OFIS on-premise): OPERA fiscal payloads
//                (Folio Generation, Post Payment, Check Out). Listens on a local IP:port; no internet exposure.
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

// Endpoint called by Oracle FLIP over the hotel LAN (OPERA: Fiscal Folio parameter "FLIP Server Address",
// Fiscal Terminals "Address and Port"). Protocol/contract pending Oracle's FLIP fiscal-partner specification.
app.MapPost("/flip/fiscal-payload", () => Results.StatusCode(StatusCodes.Status501NotImplemented));

app.Run();
