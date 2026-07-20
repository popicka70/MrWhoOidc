# Page: Platform Admin – Read-Only Enforcement
## Route: /platform-admin/support-access/read-only

## Expectations
- During an active read-only support access session, attempting a mutation operation (POST/PUT/DELETE) to a tenant-admin API endpoint is denied
- The response must be 403 Forbidden or equivalent access-denied error
- The page should display a "Forbidden" or "Access Denied" message
- The error should clearly indicate that write operations are not allowed during read-only support access

## Actions
- Verify the mutation attempt results in a 403 Forbidden response
- Verify the error message mentions "read-only" or "support access" in the denial reason

## Visual Checks
- Error page or inline error with clear "Access Denied" / "Forbidden" heading
- No successful operation result is shown
