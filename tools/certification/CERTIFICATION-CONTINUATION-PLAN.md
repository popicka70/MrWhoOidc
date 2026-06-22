# MrWhoOidc OpenID Self-Certification — Continuation Plan (Handoff)

Last updated: 2026-06-22. This document is a self-contained handoff so another operator/LLM
can continue the OpenID Foundation self-certification of `MrWhoOidc.WebAuth` without re-discovering
context. Pair it with the canonical runbook in `tools/certification/README.md`, the readiness doc in
`docs/oidc-openid-certification-readiness.md`, and repo memory `/memories/repo/mrwhooidc-certification.md`.

---

## 1. Targets, environment, and secrets

- Public OP under test (issuer): `https://mrwho.onrender.com/t/default`
  - Discovery: `/.well-known/openid-configuration` · Health: `/health` · Version: `/version`
- Hosted conformance suite: `https://www.certification.openid.net`
- Local official runner checkout: `C:\Users\rum2c\source\repos\conformance-suite`
  - Runner script: `scripts/run-test-plan.py`
  - **Local patch already applied**: `scripts/conformance.py` `wait_for_state(timeout=None)` now reads
    env `CONFORMANCE_STATE_TIMEOUT` (default 240). Always export `CONFORMANCE_STATE_TIMEOUT='900'`.
- Repo: `C:\Users\rum2c\source\repos\MrWhoProjects\MrWhoOidc`
  - Working branch: `feat/self-certification`. Deployments build from `master` on Render.

Secrets (DO NOT hardcode in committed files; obtain from the user / secure store at runtime):
- Hosted suite API bearer token — get a fresh one from the authenticated browser session at
  `https://www.certification.openid.net/api/token`, or reuse the operator's current token. Used as
  `Authorization: Bearer <token>` for all `/api/...` calls and as `CONFORMANCE_TOKEN` for the runner.
  - **Persisted for reuse on this machine** (both are gitignored / not committed):
    - User env var `CONFORMANCE_TOKEN` (set via `[Environment]::SetEnvironmentVariable('CONFORMANCE_TOKEN', <tok>, 'User')`).
      New PowerShell sessions get it automatically as `$env:CONFORMANCE_TOKEN`.
    - File `tools/certification/.generated/.suite-token` (the `.generated` dir is gitignored).
  - Suite tokens can expire; if `/api/...` calls start returning 401/403, mint a fresh one at `/api/token`
    and re-run the two persistence commands above.
- Google login for the suite (the suite logs into the OP browser flows): `MrWhoOidc@gmail.com` —
  password is operator-held. Suite login is via `/oauth2/authorization/google`.
- Seeded OP browser user (used by generated runner configs): `oidf-cert-user` / `OidfCertUser123!`.

---

## 2. Where we are (status snapshot)

Hosted profiles completed cleanly:
- **Config OP** — clean pass.
- **Basic OP** (dynamic client) — full matrix, no protocol failures; REVIEW screenshots uploaded;
  remaining non-pass states are expected SKIPs.
- **Form Post OP** (dynamic client) — full matrix, no condition failures; REVIEW screenshots uploaded
  (`prompt-login`, `max-age-1`, `ensure-registered-redirect-uri`); remaining non-pass are expected SKIPs.
- **RP-Initiated Logout OP** (dynamic client) — DONE. Clean full run on plan `x5WpxffzGWzG4`
  (alias `mrwhooidc-public-rplogoutdyn-v6`, deployed commit `022e58cd`): all 11 modules FINISHED,
  3 PASSED + 8 REVIEW, **648 successes, 0 failures, 0 warnings**, ran in ~228s. The 8 REVIEW
  screenshots were auto-captured by the harness (no manual uploads). Export ZIP is in
  `tools/certification/.generated/public-rplogoutdyn-v6-run/exports/`.
  - Key enabler: the `*/endsession*` browser block now has a `Capture OP Logout Or Error Page` task
    matching `*/connect/endsession*` that fills the module's logout placeholder via
    `update-image-placeholder-optional`; combined with the deployed visible error/“signed out” pages,
    every screenshot module completes automatically. Reuse this pattern for the other logout profiles.
- **Back-Channel Logout OP** (dynamic client) — DONE. Plan `qTbSGMpNjZsSE`
  (alias `mrwhooidc-public-bclogout-v1`, variant `[response_type=code][client_registration=dynamic_client]`):
  both modules PASSED, **101 successes, 0 failures, 0 warnings**, ~26s. Fully automated server-to-server
  (`logout_token` POST to the registered `backchannel_logout_uri`). Export ZIP in
  `tools/certification/.generated/public-bclogout-v1-run/exports/`.
- **Front-Channel Logout OP** (dynamic client) — DONE. Plan `tj3c0qM3zUwmW`
  (alias `mrwhooidc-public-fclogout-v1`, variant `[response_type=code][client_registration=dynamic_client]`):
  both modules PASSED, **96 successes, 0 failures, 0 warnings**, ~23s. The suite loads the OP logout page
  which embeds the RP `frontchannel_logout_uri` iframe; the deployed `<meta refresh>`/iframe page satisfies it.
  Export ZIP in `tools/certification/.generated/public-fclogout-v1-run/exports/`.

Remaining certification work:
1. **Session Management OP** — `oidcc-session-management-certification-test-plan`. Discovery module PASSES,
   but `oidcc-session-management-rp-initiated-logout` CANNOT be completed by the automated long-poll runner:
   it stalls at "Redirecting to our session check page" because the suite's htmlunit browser cannot run the
   `check_session_iframe`'s `crypto.subtle.digest` + `postMessage` JS (the module summary itself warns
   "this test may not work in some browsers"). The deployed `/t/default/connect/checksession` iframe is
   correct per spec. **Complete this module interactively in a real browser via the suite UI**, not the runner.

---

## 3. Product + harness fixes already made (context for failures you may see)

All of these are committed; the first two are already DEPLOYED (verify `/version`).
1. **Logout sign-out + invalid redirect error page** (`MrWhoOidc.WebAuth/Handlers/Logout/EndSessionHandler.cs`):
   `end_session` explicitly signs out both `Cookies` and `preauth`; an invalid `post_logout_redirect_uri`
   returns an explicit HTTP 400 error page (not the blank logout shell).
2. **Non-JS logout redirect** (same file + `FrontChannelPageBuilder.cs`): when there are no front-channel
   iframes, `end_session` returns a real HTTP 302 to `/logout/final?ref=...` instead of a JS-only redirect
   (the suite's htmlunit browser cannot run JS); the iframe path also emits a `<meta http-equiv="refresh">`
   fallback. This is what unblocked the perpetual `WAITING` on `oidcc-rp-initiated-logout`.
3. **Harness post-logout task** (`tools/certification/prepare-conformance-suite.ps1`): the `*/endsession*`
   browser block no longer waits for a non-existent `submission_complete` element (which timed out and
   failed the module). It now uses a single non-failing `Capture Logout Result Page` task
   (`wait xpath //* 5 .* update-image-placeholder-optional`).
4. **Signed-out confirmation page** (`FrontChannelPageBuilder.cs`): the terminal logout case (no
   post-logout redirect, no iframes) now renders a visible "You have been signed out" page instead of a
   blank body. This makes the REVIEW screenshots for `no-id-token-hint` / `no-params` /
   `no-post-logout-redirect-uri` meaningful for the OIDF reviewer. **Committed locally; NOT yet deployed
   as of this writing — deploy to `master`/Render and verify `/version` before the next logout rerun.**

Unit validation for the product changes: `dotnet test MrWhoOidc.UnitTests/MrWhoOidc.UnitTests.csproj --filter "LogoutHandlerTests|LogoutPromptFlowTests"` → 14/14 passed.

Before trusting any hosted rerun, confirm the deployment includes your latest fixes:
`Invoke-RestMethod https://mrwho.onrender.com/version` and compare `commit` to the commit that contains
the change (the deployed branch is `master`; your work branch may be ahead/behind).

---

## 4. Operating rules learned the hard way

- **One module per alias at a time.** The suite uses a single alias per plan. If the long-poll runner
  times out a still-`WAITING` interactive module, it starts the next module and the suite interrupts the
  prior one with an "alias conflict". For interactive logout/session modules, prefer driving via the
  suite UI (`Repeat Test` / `Continue Plan`) over the long-poll runner, or always export
  `CONFORMANCE_STATE_TIMEOUT='900'`.
- **REVIEW/screenshot modules cannot be auto-completed by the runner.** Handle them via the browser:
  reproduce the OP page, screenshot it (≤500 KB; JPEG ~40-50% if needed), upload to the module, then
  `Continue Plan`. See section 6.
- **Always inspect the page/log before clicking.** Read `/api/info/<id>` and `/api/log/<id>` (or the
  shared browser page) to decide the next action; never click blindly.
- **Reuse screenshots already captured** at
  `tools/certification/.generated/review-screenshots/2026-04-20/` when a module asks for the same evidence.

---

## 5. Commands

### 5.1 Poll a plan's module states
```powershell
$headers=@{ Authorization = "Bearer <SUITE_TOKEN>" }
$planId='qR5P5wmgHT1bp'   # current logout plan; change as needed
$plan = Invoke-RestMethod -Uri ("https://www.certification.openid.net/api/plan/$planId") -Headers $headers
$rows = foreach($m in $plan.modules){ foreach($id in $m.instances){ if($id){
  $info = Invoke-RestMethod -Uri ("https://www.certification.openid.net/api/info/$id") -Headers $headers
  [pscustomobject]@{ id=[string]$id; testName=$info.testName; status=$info.status; result=$info.result } } } }
$rows | Format-Table -Auto
```

### 5.2 Inspect a single module log tail
```powershell
$id='IsRYH9zJZlvnQj8'
(Invoke-RestMethod -Uri ("https://www.certification.openid.net/api/log/$id") -Headers $headers |
  Select-Object -Last 20 | ForEach-Object { [pscustomobject]@{ msg=$_.msg; src=$_.src; result=$_.result } }) |
  Format-Table -Auto
```

### 5.3 Prepare a fresh runner config (new alias each run)
```powershell
Set-Location 'C:\Users\rum2c\source\repos\MrWhoProjects\MrWhoOidc'
pwsh ./tools/certification/prepare-conformance-suite.ps1 `
  -SuiteAlias 'mrwhooidc-public-<profile>-vN' `
  -SuiteHost 'www.certification.openid.net' `
  -BaseUrl 'https://mrwho.onrender.com' -TenantSlug default `
  -OutputDir '.\tools\certification\.generated\public-<profile>-vN'
```

### 5.4 Run a plan with the official runner (longer timeout!)
```powershell
$dir='C:\Users\rum2c\source\repos\MrWhoProjects\MrWhoOidc\tools\certification\.generated\public-<profile>-vN-run'
New-Item -ItemType Directory -Path (Join-Path $dir 'exports') -Force | Out-Null
Set-Content (Join-Path $dir 'expected-failures.json') '[]'; Set-Content (Join-Path $dir 'expected-skips.json') '[]'
Set-Location 'C:\Users\rum2c\source\repos\conformance-suite'
$env:CONFORMANCE_SERVER='https://www.certification.openid.net/'
$env:CONFORMANCE_SERVER_LOCAL='https://www.certification.openid.net/'
$env:CONFORMANCE_SERVER_MTLS='https://www.certification.openid.net/'
$env:CONFORMANCE_TOKEN='<SUITE_TOKEN>'
$env:CONFORMANCE_STATE_TIMEOUT='900'
py -3 scripts/run-test-plan.py `
  --export-dir "$dir/exports" `
  --expected-failures-file "$dir/expected-failures.json" `
  --expected-skips-file "$dir/expected-skips.json" `
  '<PLAN_NAME>[<VARIANTS>]' `
  '../MrWhoProjects/MrWhoOidc/tools/certification/.generated/public-<profile>-vN/official-runner-dynamic-op-config.json'
```

Profile → plan name / variants:
- RP-Initiated Logout: `oidcc-rp-initiated-logout-certification-test-plan[client_registration=dynamic_client][response_type=code]`
- Session Management: `oidcc-session-management-certification-test-plan[client_registration=dynamic_client]` (confirm variants via `--list`)
- Front-Channel Logout: `oidcc-frontchannel-rp-initiated-logout-certification-test-plan[client_registration=dynamic_client]`
- Back-Channel Logout: `oidcc-backchannel-rp-initiated-logout-certification-test-plan[client_registration=dynamic_client]`

---

## 6. Handling a REVIEW / screenshot module (e.g. `bad-post-logout-redirect-uri`)

The OP must show an error page (it does, after the deployed fix) and the suite wants a screenshot.
1. From the module log (`/api/log/<id>`), find `BuildRedirectToEndSessionEndpoint` →
   `redirect_to_end_session_endpoint` (the invalid end-session URL).
2. In the shared, logged-in suite browser, first drive the OP login (authorize → `oidf-cert-user` →
   consent) so an OP session exists, then navigate to that exact end-session URL.
3. Screenshot the resulting OP error page (save under
   `tools/certification/.generated/review-screenshots/2026-04-20/`; keep ≤500 KB).
4. Open `https://www.certification.openid.net/upload.html?log=<id>&continue` in the logged-in browser,
   set the file on the hidden `input[type=file]`, click `Upload` (use a direct element click;
   the styled button can be flaky), confirm an image entry appears.
5. Return to the module log page and click `Continue Plan`.

Reusable existing screenshots: `oidcc-prompt-login.png`, `oidcc-max-age-1.png`,
`oidcc-ensure-registered-redirect-uri.png` in the review-screenshots folder.

---

## 7. Step-by-step continuation

1. **Finish RP-Initiated Logout.** The flow is already conformant (0 failures). What remains is REVIEW
   screenshots for the 8 modules listed in section 2.
   - First deploy the `FrontChannelPageBuilder` signed-out confirmation page (fix #4) to `master`/Render
     and confirm `/version`, so the "successful logout page" screenshots are meaningful.
   - Start a fresh plan on a new alias (`mrwhooidc-public-rplogoutdyn-v6`). Let the 3 auto-pass modules
     run. For each of the 8 screenshot modules, drive it in the suite UI: open the module log, observe the
     OP page (error page for bad/`modified`/`bad-id-token-hint`/`bad-post-logout-redirect-uri`; the
     "You have been signed out" page for `no-id-token-hint`/`no-params`/`no-post-logout-redirect-uri`;
     the redirect for `only-state`/`query-added`), capture a screenshot, `Upload Images`, then
     `Continue Plan`. Do NOT let the long-poll runner drive these (it cannot upload and will alias-conflict).
   - Reproduce the OP page for a screenshot by replaying the module's `redirect_to_end_session_endpoint`
     URL (from its log) in the logged-in browser after first establishing an OP session
     (authorize → `oidf-cert-user` → consent).
   - Goal: a plan where every module is PASSED, or REVIEW/SKIPPED with evidence. Record the plan id.
2. **Session Management OP** — prepare config (`-SuiteAlias mrwhooidc-public-sessionmgmt-v1`), run plan,
   drive interactive modules. Watch for OP `check_session_iframe` / session_state behavior; if a module
   hangs or fails, inspect the log, fix the OP if it is a real defect (add a regression unit test under
   `MrWhoOidc.UnitTests`), redeploy, verify `/version`, rerun on a fresh alias.
3. **Front-Channel Logout OP** — needs a client with `frontchannel_logout_uri`. The generated config's
   front-channel iframe path now has the `<meta refresh>` fallback. Expect screenshot/review steps.
4. **Back-Channel Logout OP** — exercises `/backchannel-logout` receivers and `logout_token`. Verify the
   OP enqueues/sends back-channel notifications (`BackChannelLogoutEnqueuer`).
5. **Assemble the certification package** once each profile has a clean plan: in a normal interactive
   browser (not the automation harness), use the suite's `Publish for certification` / `Create
   Certification Package` and confirm the ZIP downloads. The VS Code browser may not persist the download.
6. **Declaration of Conformance** + payment are operator/business steps, out of scope for automation.

---

## 8. Definition of done

- Every in-scope profile (`Config`, `Basic`, `Form Post`, `RP-Initiated Logout`, `Session Management`,
  `Front-Channel Logout`, `Back-Channel Logout`) has a hosted plan where all modules are PASSED, or
  REVIEW/SKIPPED with uploaded evidence and no unexpected condition failures.
- For any product fix made along the way: a regression unit test exists, the change is committed, the
  public deployment `/version` reflects it, and the hosted rerun was done on a fresh alias against that build.
- Certification packages generated per profile and handed to the operator for the signed Declaration of
  Conformance.

---

## 9. Quick reference — current live identifiers (will age out)

- Logout plan: `qR5P5wmgHT1bp` (alias `mrwhooidc-public-rplogoutdyn-v5`).
- Deployed OP commit at last check: `022e58cd` on `master` (has all logout fixes including confirmation page and non-JS redirect). Verified via `/version`.
- Local branch/HEAD at last check: `master` / `022e58cd`.
- Generated artifacts live under `tools/certification/.generated/public-*`.
- Next free alias suffix: use `v6` for RP-Initiated Logout reruns; `v1` for each new profile.

## 10. Current session status (2026-06-22)

### What was done this session

1. **Verified deployed state**: `https://mrwho.onrender.com` is at commit `022e58cd` on `master`. All logout fixes are deployed:
   - Non-JS redirect (HTTP 302) for end_session without front-channel iframes
   - `<meta http-equiv="refresh">` fallback in FrontChannelPageBuilder
   - Signed-out confirmation page rendered in FrontChannelPageBuilder for terminal logout case
   - Invalid post_logout_redirect_uri returns HTTP 400 error page
   - Unit tests: 14/14 logout tests pass

2. **Stashed accidental regression**: The HEAD commit `022e58cd` ("Refactor code structure") removed the `BuildLogoutConfirmationPage` method from `EndSessionHandler.cs`. The confirmation page logic was preserved in `FrontChannelPageBuilder.cs` (which handles the terminal logout case), so the deployed behavior is correct. The uncommitted diff that removed the `BuildLogoutConfirmationPage` call was stashed as `stash@{0}`.

3. **Ran certification verifier**: `verify-self-certification.ps1` against the public deployment reports **80 passed, 0 failed, 1 warning** (the warning is the expected missing fallback client `oidf-basic-client-secret-post` — an operational/deployment-seeding issue, not a product defect).

4. **Generated runner configs** for all remaining profiles:
   - `public-rplogoutdyn-v6` — RP-Initiated Logout OP (for screenshot work)
   - `public-sessionmgmt-v1` — Session Management OP
   - `public-frontchannel-v1` — Front-Channel Logout OP
   - `public-backchannel-v1` — Back-Channel Logout OP

### What still needs to be done (requires suite API token + browser access)

The next operator/LLM needs:

1. **Suite API token**: Obtain from `https://www.certification.openid.net/api/token` (authenticated browser session). Set as `$env:CONFORMANCE_TOKEN` and `Authorization: Bearer <token>` header.

2. **RP-Initiated Logout screenshots** (8 modules): Drive via suite UI on a fresh plan using alias `mrwhooidc-public-rplogoutdyn-v6`. For each module, reproduce the OP page, screenshot, upload. Existing screenshots in `tools/certification/.generated/review-screenshots/2026-04-20/` can be reused where applicable.

3. **Session Management OP**: Run plan `oidcc-session-management-certification-test-plan[client_registration=dynamic_client]` using `public-sessionmgmt-v1` config. Expect interactive/review modules.

4. **Front-Channel Logout OP**: Run plan `oidcc-frontchannel-rp-initiated-logout-certification-test-plan[client_registration=dynamic_client]` using `public-frontchannel-v1` config.

5. **Back-Channel Logout OP**: Run plan `oidcc-backchannel-rp-initiated-logout-certification-test-plan[client_registration=dynamic_client]` using `public-backchannel-v1` config.

6. **Certification packages**: Generate via suite UI `Publish for certification` once each profile has a clean plan.
