// HRS Fiscal Client — installed on every workstation that can issue invoices (ATK requirement).
//
// Responsibilities (phase 1 skeleton):
//   - owns this workstation's ATK identity: PosId, non-exportable ECDSA P-256 key (CNG/TPM), ATK certificate
//   - onboarding: CSR -> ATK /ca/verify + /ca/signcsr
//   - signs PosCoupon + CitizenCoupon (QR) for receipts routed to it by the HRS Fiscal Server
//   - transmits to ATK (/pos/coupon); on failure hands the signed coupon to the server's offline queue
//   - shows the online/offline indicator (tray app, later)

using Hrs.Fiscal.Client;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "HRS Fiscal Client");
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
