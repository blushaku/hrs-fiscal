# ATK TEST environment — first integration run (08.10.2026)

Environment: `https://fiskalizimi-test.atk-ks.org` · test business **MARIGONA TOWER SH.P.K** (NUI 811159898), unit 1,
ApplicationId 356526644730. POS 1 was registered with ATK's own onboarder tool. **POS 901 was registered with
`hrs-fiscal-cli onboard`, so HRS's own onboarding works**: verify → P-256 key → CSR (C=RKS, O, OU, L, CN) → certificate
issued by the ATK test CA.

| # | Scenario | Expected | HTTP | Result | ATK message |
|---|---|---|---|---|---|
| 1 | Sale · room + F&B · card | accepted | 200 | ✅ accepted | Coupon received successfully |
| 2 | Sale · VAT A/C/D/E · cash + card split | accepted | 200 | ✅ accepted | Coupon received successfully |
| 3 | Sale · line discount | accepted | 200 | ✅ accepted | Coupon received successfully |
| 4 | Sale · 4-decimal price, quantity 1.5 | accepted | 200 | ✅ accepted | Coupon received successfully |
| 5 | Return referencing sale #1 | accepted | 200 | ✅ accepted | Coupon received successfully |
| 6 | Identical payload resent | observe | 200 | **accepted again, new transaction id** | Coupon received successfully |
| 7 | Same CouponId, different content | observe | 200 | **accepted** | Coupon received successfully |
| 8 | Payload changed after signing | rejected | 400 | ✅ rejected | details and signature don't match! |
| 9 | Return without ReferenceNo | rejected | 400 | ✅ rejected | ReferenceNo is missing. |
| 10 | Same lines with "truncate net" VAT rounding | observe | 200 | **accepted** | Coupon received successfully |
| 11 | Citizen QR verification of sale #1 | accepted | 200 | ✅ verified (on retry, see below) | full coupon returned |

## What this settles
- **Signing, protobuf, money units and QR format are correct.** ATK verified the signature and decoded the coupon
  (the citizen endpoint returned our totals and VAT groups exactly).
- **ATK does not de-duplicate.** An identical resend and a reused CouponId with new content were both accepted.
  HRS must therefore guarantee uniqueness itself: the archive's UNIQUE coupon_id plus "never resend a coupon that has an
  accepted transmission". After a timeout with an unknown result, a resend can create a second ATK transaction for the
  same coupon. **Question for ATK: how are duplicate transactions for one CouponId treated in their reporting?**
- **ATK does not check VAT rounding on intake.** Both rounding methods were accepted, so the official rule must come
  from ATK in writing (it may matter for later audits, not for acceptance).
- **Transaction ids use the full uint64 range** (e.g. 18113828467842754216). The archive's `numeric(20,0)` holds them.
- **`/citizen/coupon` needs `citizen_id` as a number.** A string gives `400 invalid request body`, although Swagger says string.

Signed payloads and QR PNGs for each scenario are kept outside the repo (they belong to the test profile).
