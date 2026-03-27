# Page: Account – Linked Accounts
## Route: /account/linked-accounts

## Expectations
- Page heading "Linked Accounts" or "Connected Identities" with a link or chain icon
- Table or card list of external identity providers linked to this account: Provider name, External username/email, Linked date
- "Unlink" button per entry
- "Link Account" or "Connect another provider" button if additional providers are available
- Empty state if no accounts are linked: icon + explanation

## Actions
- Verify page loads without errors
- Verify the list of linked accounts (or empty state) is shown
- Verify Unlink button is present per linked provider
- Verify "Link Account" button exists if providers are configured

## Visual Checks
- Provider icons or logos shown next to provider name (Google, GitHub, etc.)
- Unlink button uses btn-outline-danger
- Empty state uses ph ph-link-break or ph ph-plugs icon
- Page header uses Phosphor icon (ph ph-link or ph ph-arrows-merge)
