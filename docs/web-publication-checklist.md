# Website Publication Checklist

Updated: 2026-09-05. This is an internal completion checklist, not a published privacy policy or legal opinion.

## Completed

- [x] Replace the missing jsDelivr QR asset with the locally served `qrcode@1.5.4` browser bundle.
- [x] Include runtime licenses and a pinned PowerShell regeneration script.
- [x] Verify the actual portal payment renderer: a synthetic QR payload decoded back to the original string using `jsQR@1.4.0`, with a 160 x 160 canvas. No payment was initiated.
- [x] Verify portal loading without failed asset requests and the payment-instructions empty state.
- [x] Describe browser storage, sign-out cleanup, external asset providers, locally generated QR codes, and the privacy request contact.
- [x] Check public OpenID Foundation provider and logout directories and distinguish test reports from a certification listing.

## Owner Input Required

The owner was unavailable when these facts were requested. Do not fill these fields from an inferred GitHub identity or generic industry defaults. The public privacy notice is not ready to be treated as a complete legal notice until the relevant facts below are confirmed and incorporated.

| Required fact | Status | Where to obtain it |
| --- | --- | --- |
| Legal controller name, address, registration details and jurisdiction | Not provided | Website/portal operator |
| Confirmed privacy contact and any applicable DPO or representative | Existing general email only | Operator or privacy adviser |
| Purposes and legal bases for account, billing, support and security processing | Not confirmed | Operator/privacy adviser, using actual data flows |
| Retention periods or criteria for accounts, support email, audit/access logs, backups and accounting records | Not confirmed | Application settings, hosting contracts and accounting obligations |
| Hosting, database, email, payment and support providers; processing locations and international transfer arrangements | Incomplete | Deployed infrastructure and provider agreements |
| Request verification, deletion/export procedure and applicable supervisory authority | Not confirmed | Operator's actual request-handling procedure |
| Server-side login cookies and their actual lifetimes | Not audited | Deployed authentication configuration |

The visible `mrwho.onrender.com` sign-in hostname does not establish the controller's identity, the database region, retention periods, or a complete processor list.

## Certification Evidence

On 2026-09-05, no matching entry for MrWhoOidc was found in these public directories:

- [Certified OpenID Providers & Profiles](https://openid.net/certification/certified-openid-providers-profiles/)
- [Certified OpenID Providers for Logout Profiles](https://openid.net/certification/certified-openid-providers-for-logout-profiles/)

The [certification program page](https://openid.net/certification/) distinguishes free conformance testing from certification submission and use of the OpenID Certified mark. Absence from the retrieved directories is not proof that no submission exists or is pending. Request an accepted submission or a direct listing link from the owner before claiming certification. Preserve the published test reports and their individual outcomes regardless of listing status.

## Remaining Functional Verification

- [ ] Run a real registration/sign-in/sign-out cycle against the configured portal deployment using a test account.
- [ ] Verify payment instructions and license downloads using non-production records. The QR rendering round-trip check did not test bank processing or license issuance.
- [ ] Confirm the local vendor files are included in the production deployment and return the expected JavaScript content type.
