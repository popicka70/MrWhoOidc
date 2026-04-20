# OpenID Connect Basic OP Dynamic-Client Submission Notes

Date: 2026-04-20

## Submission Scope

- Product: `MrWhoOidc.WebAuth`
- Certification profile: `oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=dynamic_client]`
- Hosted conformance plan id: `d7om1F1mxZ4m2`
- Hosted plan URL: `https://www.certification.openid.net/plan-detail.html?plan=d7om1F1mxZ4m2`
- Export bundle: `tools/certification/.generated/exports/oidcc-basic-certification-test-plan-discovery-dynamic_client-d7om1F1mxZ4m2-20-Apr-2026.zip`

## Deployment Under Test

- Issuer: `https://mrwho.onrender.com/t/default`
- Discovery URL: `https://mrwho.onrender.com/t/default/.well-known/openid-configuration`
- JWKS URL: `https://mrwho.onrender.com/t/default/jwks`
- Dynamic registration endpoint: `https://mrwho.onrender.com/t/default/register`
- End-session endpoint: `https://mrwho.onrender.com/t/default/connect/endsession`
- Check-session iframe: `https://mrwho.onrender.com/t/default/connect/checksession`
- Suite alias used for the hosted run: `mrwhooidc-0420151707-1f3730`

## Runtime Version

Runtime metadata from `GET https://mrwho.onrender.com/version` on 2026-04-20:

- Service: `MrWhoOidc.WebAuth`
- Environment: `Production`
- Version: `1.0.0`
- Informational version: `1.0.0`
- Commit: not reported by the deployment
- Response header: `X-MrWhoOidc-Version: 1.0.0`

## Certification Result Summary

Official runner summary for plan `d7om1F1mxZ4m2`:

- Overall totals: `2068 successes, 0 failures, 0 warnings`
- All tests ran to completion
- There are no unexpected warning or failure conditions remaining in the hosted run
- This rerun reflects the deployment currently serving the browser-friendly authorize error page used for the redirect URI review evidence

Previously blocking modules that now pass:

- `oidcc-scope-profile`
- `oidcc-codereuse`
- `oidcc-codereuse-30seconds`
- `oidcc-claims-essential`

## Manual Review Modules

The only remaining non-pass outcomes are `REVIEW` modules that require manual evidence upload in the certification portal.

1. `oidcc-prompt-login`
   - Log URL: `https://www.certification.openid.net/log-detail.html?log=Fb4B67YHXQm80XK`
   - Evidence requested by suite: upload a screenshot showing that the server asks the user to log in a second time when `prompt=login` is used.
   - Prepared screenshot: `tools/certification/.generated/review-screenshots/2026-04-20/oidcc-prompt-login.png`

2. `oidcc-max-age-1`
   - Log URL: `https://www.certification.openid.net/log-detail.html?log=4cz0kL0Sjs1kk5V`
   - Evidence requested by suite: upload a screenshot showing that the server asks the user to log in a second time when `max_age=1` forces reauthentication.
   - Prepared screenshot: `tools/certification/.generated/review-screenshots/2026-04-20/oidcc-max-age-1.png`

3. `oidcc-ensure-registered-redirect-uri`
   - Log URL: `https://www.certification.openid.net/log-detail.html?log=dc7S15c5qECQNFu`
   - Evidence requested by suite: upload a screenshot of the redirect URI error page shown for an unregistered redirect URI.
   - Prepared screenshot: `tools/certification/.generated/review-screenshots/2026-04-20/oidcc-ensure-registered-redirect-uri.png`

## Expected Skips

These skipped modules are expected for this certification run and correspond to intentionally unsupported optional features that are not advertised by the deployment:

- `oidcc-idtoken-unsigned`
- `oidcc-scope-address`
- `oidcc-scope-phone`
- `oidcc-scope-all`
- `oidcc-request-uri-unsigned-supported-correctly-or-rejected-as-unsupported`
- `oidcc-unsigned-request-object-supported-correctly-or-rejected-as-unsupported`
- `oidcc-ensure-request-object-with-redirect-uri`

Reason summary:

- Unsigned ID tokens (`alg=none`) are intentionally unsupported.
- `address` and `phone` scope coverage is intentionally unsupported and not advertised in discovery.
- Unsigned request objects and unsigned external `request_uri` usage are intentionally unsupported; PAR-backed behavior is implemented instead.

## Paste-Ready Submission Note

Use the following text as the certification submission note body, adjusting only if the portal requires different formatting:

> Submitting OpenID Connect Basic OP certification for `MrWhoOidc.WebAuth` using discovery metadata and dynamic client registration. The deployment under test is `https://mrwho.onrender.com/t/default` running version `1.0.0` in Production. Hosted conformance plan `d7om1F1mxZ4m2` completed on 2026-04-20 with `2068 successes, 0 failures, and 0 warnings`. The previously investigated `oidcc-scope-profile`, `oidcc-codereuse`, and `oidcc-codereuse-30seconds` modules all passed. The only remaining non-pass outcomes are manual `REVIEW` items for `oidcc-prompt-login`, `oidcc-max-age-1`, and `oidcc-ensure-registered-redirect-uri`; the requested screenshots will be uploaded with the submission. This rerun reflects the deployment that now serves the browser-facing HTML authorize error page for invalid redirect URIs. Expected skips correspond to intentionally unsupported optional features: unsigned ID tokens, `address`/`phone` scope coverage, and unsigned request object / unsigned `request_uri` coverage.

## Submission Checklist

- Upload the exported ZIP bundle from this run.
- Upload screenshots for the three `REVIEW` modules listed above.
- Confirm the deployment version in the submission metadata as `1.0.0`.
- Include the hosted plan id `d7om1F1mxZ4m2` in any reviewer notes or correspondence.
- Keep this exact deployment stable until review is complete.