# MrWhoOidc Public Website Review

Date: 2026-09-05. Scope: `MrWhoOidc.Web`, its 14 HTML pages, shared navigation and CSS. English copy was retained. Protocol implementations, portal authentication, and payment logic were not changed.

## Assessment

The site already had useful technical substance: named protocols, installation instructions, public source links, and downloadable conformance evidence. The remaining problem was not a lack of information. It was repetition, unsupported certainty, and presentation that gave decoration and slogans as much weight as the product itself.

The revised site is more useful to an engineer evaluating a self-hosted IdP. Its introduction names the product, explains what it runs on, and offers installation and feature links. The design keeps IBM Plex typography and Bootstrap components, while reducing nested panels, ornamental grids, shadows, and competing calls to action.

These are editorial observations, not a claim that prose or design can reliably identify AI authorship.

## Findings And Changes

| Pattern | Previous example | Change |
| --- | --- | --- |
| Absolute claims | "Every OIDC flow", "No external calls", "every protocol flow" | Named capabilities and links to the implementation, without implying exhaustive support or coverage. |
| Defensive comparisons | "Not a wrapper around IdentityServer", "not a bolted-on afterthought" | Explain source availability, tenant configuration, and hosting responsibilities. |
| Unsubstantiated convenience claims | "3-5 min", "Docker image under 200 MB", "That is the whole path" | Remove timing and size promises; describe prerequisites and first-run downloads. |
| Generic consulting language | "Flexible engagement models", "accountable delivery", "not slide decks" | Describe technical reviews, implementation, maintenance, and how to contact the consultant. |
| Instructions addressed like a prompt | "Do not improvise", "advanced on purpose" | Explain why the repositories and Compose files differ. Preserve the installation distinction. |
| Repeated decisions | Deployment options, a second chooser, persona cards, and a hero recommendation | One installation options section, followed by the relevant technical details. |
| Decorative technical styling | Fake "issuer-runtime / protocol map", nested metrics and chips | Remove the fake console and use a shorter product introduction. |
| Missing product evidence | Abstract feature panels without an actual interface | Add an explicitly captioned screenshot of the public demo sign-in page to Features. |
| Customer-facing implementation jargon | Authorization-code exchange and OIDC end-session messages | Short sign-in and signed-out messages; do not claim that global logout was verified. |
| Privacy overstatement | "does not collect personal data", "No data ... any third-party service" | Distinguish public pages, self-hosted installations, external assets, and the customer portal. |

## Visual Review

- The original homepage used a tall split hero dominated by nested cards. Its dark helper text was difficult to read. The revised hero is a single, unframed introduction with two primary destinations.
- White and light neutral sections replace the beige grid background. Teal actions, blue and rust supporting icons, and a dark navigation/footer retain visual separation without turning every section into a card.
- Informational blocks no longer lift on hover as though they were controls. The feature catalog uses rows instead of a separate raised card for each capability.
- The main navigation is shorter. Installation guides retain an active Install section; mobile menu expansion and collapse were tested.
- Footer text contrast was improved. Long conformance table values now wrap rather than being clipped by their container.
- On mobile, installation step numbers sit above the text rather than consuming a narrow side column. Code blocks retain horizontal scrolling to preserve command formatting.
- The real sign-in screenshot is labeled as the public demo, captured in September 2026. It contains no entered credentials or customer data; it is not a mock admin interface.

### Screenshots

These are post-change, full-page screenshots. Widths refer to CSS viewports, not image pixel dimensions.

- [Homepage, desktop at 1440 px](images/web-review-2026-09-05/home-desktop.png)
- [Homepage, mobile at 390 px](images/web-review-2026-09-05/home-mobile.png)
- [Features, desktop at 1440 px](images/web-review-2026-09-05/features-desktop.png)
- [Installation options, mobile at 390 px](images/web-review-2026-09-05/install-mobile.png)

## Verification

- Opened the static pages directly through `file://` in the integrated Chromium browser, driven through Playwright. No application server was needed for the public pages.
- Checked all 14 HTML pages at 320, 390, 768, 1024, and 1440 CSS pixels: 70 layout checks passed for page overflow, a single H1, and overlapping visible main-content buttons.
- Checked local HTML link destinations and fragment IDs across the rendered pages: no missing destinations or anchors.
- Exercised mobile menu open/close and checked the active Install state on an installation guide.
- Verified that conformance table cells did not overflow or clip at mobile width, and that the new screenshot decoded successfully.
- After the final mobile step-layout adjustment, rechecked all three installation pages at 320 px: no page overflow, full-width step content, and valid anchors.
- Reviewed representative desktop/mobile screenshots. This is not a complete accessibility audit or a cross-browser certification.
- Editor diagnostics reported no new code errors. Existing `theme-color` browser-compatibility warnings remain.

## Remaining Publication Risks

1. **Privacy policy needs owner/legal completion.** Browser storage, external asset links and privacy request information have been added. Controller identity, retention periods, legal bases and provider arrangements still require owner input; see the [publication checklist](web-publication-checklist.md).
2. **Official certification listing not found.** The public OP and logout directories were checked on 2026-09-05 without a matching MrWhoOidc entry. The website now states the dated check result and distinguishes test reports from certification. An accepted submission or direct listing link is still needed before claiming certification.
3. **QR-code asset failure resolved in follow-up.** The nonexistent CDN path was replaced with a local `qrcode@1.5.4` browser bundle, with licenses and a regeneration script. Portal loading had no failed requests, and a QR generated by the payment component decoded to the original synthetic payload. No payment was initiated.
4. **Authenticated portal workflows remain untested.** The signed-out UI and status pages were checked. Registration, login callbacks with real authorization codes, billing operations, license issuance, and QR rendering need the configured server and test accounts.
5. **More product evidence would help.** A current admin screenshot with synthetic tenant/client data would be more useful than additional marketing claims. The new image shows only the public sign-in UI; it does not imply that an authenticated admin session was reviewed.

## Editorial Guidance

- State the capability and its limit before describing a benefit.
- Keep standard protocol names; remove invented slogans around them.
- Prefer one useful installation decision over repeated reassurance.
- Link performance, coverage, certification, and security claims to dated evidence.
- Explain operational warnings without reprimanding the reader.
- Use real product screenshots with provenance and synthetic or empty data.
