# OpenID Connect Basic OP Dynamic-Client Submission Notes

Date: 2026-06-20

## Submission Scope

- Product: `MrWhoOidc.WebAuth`
- Certification profile: `oidcc-basic-certification-test-plan[server_metadata=discovery][client_registration=dynamic_client]`
- Hosted conformance plan id: `hfLvYIxzl7fFq`
- Hosted plan URL: `https://www.certification.openid.net/plan-detail.html?plan=hfLvYIxzl7fFq`

## Deployment Under Test

- Issuer: `https://mrwho.onrender.com/t/default`
- Discovery URL: `https://mrwho.onrender.com/t/default/.well-known/openid-configuration`
- JWKS URL: `https://mrwho.onrender.com/t/default/jwks`
- Dynamic registration endpoint: `https://mrwho.onrender.com/t/default/register`
- End-session endpoint: `https://mrwho.onrender.com/t/default/connect/endsession`
- Check-session iframe: `https://mrwho.onrender.com/t/default/connect/checksession`

## Runtime Version

Runtime metadata from `GET https://mrwho.onrender.com/version` on 2026-06-20:

- Service: `MrWhoOidc.WebAuth`
- Environment: `Production`
- Version: `1.0.0`
- Informational version header: `1.0.0+9a6f8d415d6e8af1ff97b2544a1b4297b8fac59e`
- Commit: `9a6f8d415d6e8af1ff97b2544a1b4297b8fac59e`

## Certification Result Summary

Hosted plan `hfLvYIxzl7fFq` is now clear of active blocking states:

- No modules remain in `FAILED`, `INTERRUPTED`, `WAITING`, or `NOT RUN`
- The previously failing `oidcc-prompt-none-logged-in` module now passes on the hosted deployment
- The previously interrupted `oidcc-server` and `oidcc-codereuse-30seconds` reruns also pass when run cleanly in isolation

Key blocker resolved during this rerun:

- `oidcc-prompt-none-logged-in`
  - Root cause: the intermediate `/Auth/Redirect` page could be cached and replay an earlier authorization callback URL during silent `prompt=none` flows
  - Fix: `MrWhoOidc.WebAuth/Pages/Auth/Redirect.cshtml.cs` now emits `Cache-Control: no-store, no-cache, max-age=0` and `Pragma: no-cache`
  - Regression coverage: `MrWhoOidc.UnitTests/RedirectPageTests.cs`

## Remaining Non-Pass Outcomes

These remaining outcomes are non-blocking for the current hosted plan:

### Review

- `oidcc-display-page`
- `oidcc-display-popup`
- `oidcc-prompt-login`
- `oidcc-max-age-1`
- `oidcc-ensure-registered-redirect-uri`

Notes:

- `oidcc-max-age-1` review evidence was exercised during this rerun and the login screenshot was uploaded to the hosted suite.
- The other review cases remain manual-review items in the hosted plan and should be preserved when preparing the formal certification request.

### Warning

- `oidcc-ensure-post-request-succeeds`

Reason summary:

- The hosted suite recorded the OpenID Connect Core `OIDCC-3.1.2.1` POST-to-authorization-endpoint expectation as a warning because it did not observe the redirect within its 30-second automation window.
- This is not a certification blocker in the current plan state.

### Expected Skips

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
- Unsigned request objects and unsigned external `request_uri` usage are intentionally unsupported; the deployment advertises signed request object support and the suite correctly skips the unsigned-only probes.

## Certification Package

The hosted suite's certification package endpoint was verified during this rerun from the authenticated browser session:

- Endpoint: `POST https://www.certification.openid.net/api/plan/hfLvYIxzl7fFq/certificationpackage`
- Response: `200 OK`
- Content-Type: `application/zip`
- Content-Disposition: `attachment; filename="oidcc-basic-certification-test-plan-discovery-dynamic_client-hfLvYIxzl7fFq-20-Jun-2026.zip"`
- Observed ZIP size: `2191054` bytes

Operational note:

- The hosted suite requires an authenticated browser session for this endpoint.
- In this VS Code browser automation environment, the ZIP response was successfully fetched but the browser-managed download was not persisted to a local file automatically.
- For the final submission handoff, use the hosted suite's `Publish for certification` / `Create Certification Package` UI in a normal interactive browser session and verify the ZIP lands in the browser download directory.

## Paste-Ready Submission Note

Use the following text as the certification submission note body, adjusting only if the portal requires different formatting:

> Submitting OpenID Connect Basic OP certification for `MrWhoOidc.WebAuth` using discovery metadata and dynamic client registration. The deployment under test is `https://mrwho.onrender.com/t/default` running version `1.0.0` in Production at commit `9a6f8d415d6e8af1ff97b2544a1b4297b8fac59e`. Hosted conformance plan `hfLvYIxzl7fFq` completed without any remaining failed, interrupted, waiting, or not-run modules. During this rerun, the previously failing `oidcc-prompt-none-logged-in` case was fixed by preventing caching of the intermediate `/Auth/Redirect` page used in the browser redirect flow. The remaining non-pass outcomes are manual `REVIEW` items (`oidcc-display-page`, `oidcc-display-popup`, `oidcc-prompt-login`, `oidcc-max-age-1`, `oidcc-ensure-registered-redirect-uri`), one non-blocking warning on `oidcc-ensure-post-request-succeeds`, and expected skips for intentionally unsupported optional features such as unsigned ID tokens and unsigned request objects / unsigned `request_uri` handling.

## Submission Checklist

- Download the certification package ZIP from the hosted suite using the authenticated browser session.
- Preserve the uploaded review evidence for `oidcc-max-age-1` and confirm the other `REVIEW` items are acceptable for submission.
- Confirm the deployment version in the submission metadata as `1.0.0` / commit `9a6f8d415d6e8af1ff97b2544a1b4297b8fac59e`.
- Include the hosted plan id `hfLvYIxzl7fFq` in reviewer notes or correspondence.
- Keep this exact deployment stable until certification review is complete.