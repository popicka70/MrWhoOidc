# MrWhoOidc OpenID Self-Certification — Continuation Plan (Handoff)

Last updated: 2026-06-21. This document is a self-contained handoff so another operator/LLM
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

In progress: **RP-Initiated Logout OP** (`oidcc-rp-initiated-logout-certification-test-plan`,
variant `[client_registration=dynamic_client][response_type=code]`).
- Current live plan: `qR5P5wmgHT1bp`, alias `mrwhooidc-public-rplogoutdyn-v5`, runner terminal id was
  `cb1c3cbf-197b-40d2-9933-dd53880c8535` (may be stale by the time you read this).
- Passing so far: `oidcc-rp-initiated-logout-discovery-endpoint-verification` (PASSED),
  `oidcc-rp-initiated-logout` (PASSED — this was the previously-stuck module, now fixed).
- Active/needs action: `oidcc-rp-initiated-logout-bad-post-logout-redirect-uri` (`IsRYH9zJZlvnQj8`) —
  WAITING; it is a REVIEW/screenshot module (OP correctly shows an error page; suite wants a screenshot).
- Remaining modules in this plan: `modified-id-token-hint`, `no-id-token-hint`, `no-params`,
  `no-post-logout-redirect-uri`, `no-state`, `only-state`, `query-added-to-post-logout-redirect-uri`,
  `bad-id-token-hint`.

Remaining certification profiles after RP-Initiated Logout (run in this order):
1. `oidcc-session-management-certification-test-plan` (Session Management OP)
2. `oidcc-frontchannel-rp-initiated-logout-certification-test-plan` (Front-Channel Logout OP)
3. `oidcc-backchannel-rp-initiated-logout-certification-test-plan` (Back-Channel Logout OP)

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

Unit validation for the product changes: `dotnet test MrWhoOidc.UnitTests/MrWhoOidc.UnitTests.csproj --filter "LogoutHandlerTests|LogoutPromptFlowTests"` → 13/13 passed.

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

1. **Finish RP-Initiated Logout (plan `qR5P5wmgHT1bp`).**
   - Handle `oidcc-rp-initiated-logout-bad-post-logout-redirect-uri` (`IsRYH9zJZlvnQj8`) per section 6.
   - Let the remaining 8 modules run. Most are non-interactive negative tests (no params / no state /
     bad id_token_hint, etc.) and should pass with the deployed OP. If the long-poll runner causes
     alias-conflict interrupts, repeat the interrupted module via the UI (`Repeat Test`) after stopping
     any follow-on WAITING module, OR rerun the whole plan on a fresh alias (`vN+1`) now that the OP and
     harness are fixed.
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
- Deployed OP commit at last check: `0ccaf8a3...` on `master` (has logout fixes). Re-check `/version`.
- Local branch/HEAD at last check: `feat/self-certification` / `09dbd850...`.
- Generated artifacts live under `tools/certification/.generated/public-*`.
- Next free alias suffix: use `v6+` for RP-Initiated Logout reruns; `v1` for each new profile.
