# Phase 5B: Advanced UX & Enhancements - Implementation Plan

## Overview
Phase 5B adds advanced user experience features and administrative enhancements to improve security, usability, and auditability.

**Estimated Total Effort:** 5-7 days (40-56 hours)  
**Priority:** Optional enhancements (core functionality already complete in Phase 5A)

## Features Breakdown

### Feature 1: Email Verification for Alternative Emails ⏳
**Priority:** Medium  
**Effort:** 1-2 days (8-16 hours)  
**Status:** Not Started

**Description:**
When users add alternative email addresses in `/Account/Emails`, send verification emails with time-limited tokens. Users must click verification link before email becomes "verified" status.

**Technical Components:**
1. **Email Verification Token Service**
   - Generate secure, time-limited tokens (6-hour expiry)
   - Store tokens in `EmailVerificationTokens` table
   - Token format: base64url-encoded GUID

2. **Email Sending Service**
   - SMTP integration (use existing email infrastructure)
   - Email template with verification link
   - Support for both HTML and plain text

3. **Verification Endpoint**
   - `/VerifyEmail?token={token}` (GET)
   - Validate token, mark email as verified
   - Show success/error page

4. **Database Changes**
   - Add `EmailVerificationTokens` table
   - Add `IsVerified` column to `AlternativeEmails` table
   - Add `VerifiedAt` timestamp

5. **UI Changes**
   - Show "Pending Verification" badge in `/Account/Emails`
   - "Resend Verification Email" button
   - "Verified" checkmark icon

**Files to Create/Modify:**
- `MrWhoOidc.Auth/Services/EmailVerificationService.cs` (new, ~100 lines)
- `MrWhoOidc.Auth/Persistence/Entities/EmailVerificationToken.cs` (new, ~30 lines)
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (update - add DbSet)
- `MrWhoOidc.WebAuth/Pages/VerifyEmail.cshtml[.cs]` (new, ~60 lines)
- `MrWhoOidc.WebAuth/Pages/Account/Emails/Index.cshtml[.cs]` (update - add verification status)
- Migration: Add `EmailVerificationTokens` table and `AlternativeEmails.IsVerified` column

**Dependencies:**
- Email sending infrastructure (SMTP configured)
- Base URL configuration for verification links

---

### Feature 2: External Identity Linking OAuth Flow ⏳
**Priority:** Low  
**Effort:** 2-3 days (16-24 hours)  
**Status:** Not Started

**Description:**
Allow users to link external identity providers (Google, Microsoft, GitHub) to their account. Once linked, they can log in using external provider without password.

**Technical Components:**
1. **External Provider Configuration**
   - OAuth client credentials for each provider
   - Callback URLs configured
   - Scope management (email, profile)

2. **Linking Flow**
   - "Link Google Account" button in `/Account/LinkedAccounts`
   - Redirect to OAuth provider
   - Callback handler validates and links account
   - Store `ExternalProviderUserId` in `LinkedIdentities` table

3. **Unlinking Flow**
   - "Unlink" button for each linked provider
   - Confirmation dialog
   - Remove from database

4. **Login Integration**
   - Update login page to show linked providers
   - Allow login via linked provider
   - Merge authentication logic

**Files to Create/Modify:**
- `MrWhoOidc.WebAuth/Pages/Account/LinkedAccounts/Link.cshtml[.cs]` (new, ~80 lines)
- `MrWhoOidc.WebAuth/Pages/Account/LinkedAccounts/Callback.cshtml[.cs]` (new, ~100 lines)
- `MrWhoOidc.WebAuth/Pages/Account/LinkedAccounts/Unlink.cshtml[.cs]` (new, ~40 lines)
- `MrWhoOidc.WebAuth/Pages/Account/LinkedAccounts/Index.cshtml` (update - add Link buttons)
- `MrWhoOidc.Auth/Services/ExternalProviderLinkingService.cs` (new, ~150 lines)

**Dependencies:**
- OAuth provider credentials (Google, Microsoft, GitHub)
- Existing `LinkedIdentities` table (already exists)

---

### Feature 3: Session Metadata Enhancement ✅
**Priority:** High  
**Effort:** 1 day (8 hours)  
**Status:** ✅ **COMPLETE** (October 6, 2025)

**Description:**
Enhance session list in `/Account/Sessions` to show IP address, User-Agent (browser/device), and "This device" indicator for current session.

**Technical Components:**
1. **Session Metadata Storage**
   - Add `IpAddress` column to `RefreshTokens` table
   - Add `UserAgent` column to `RefreshTokens` table
   - Parse User-Agent to extract browser/OS info

2. **Current Device Detection**
   - Compare current User-Agent with session User-Agent
   - Show "This device" badge if match

3. **IP Address Display**
   - Show IP address (with privacy consideration - optionally mask last octet)
   - Geolocation lookup (optional, via IP2Location or MaxMind)

4. **User-Agent Parsing**
   - Extract browser name (Chrome, Firefox, Safari, Edge)
   - Extract OS (Windows, macOS, Linux, iOS, Android)
   - Show icon for device type (desktop, mobile, tablet)

**Files to Create/Modify:**
- `MrWhoOidc.Auth/Persistence/Entities/RefreshToken.cs` (update - add IpAddress, UserAgent)
- `MrWhoOidc.WebAuth/Pages/Account/Sessions/Index.cshtml[.cs]` (update - display metadata)
- `MrWhoOidc.Auth/Services/UserAgentParser.cs` (new, ~80 lines)
- Migration: Add `IpAddress` and `UserAgent` columns to `RefreshTokens`

**Dependencies:**
- User-Agent parsing library (or manual parsing)
- IP address from HttpContext

---

### Feature 4: Read-Only Mode During Impersonation ⏳
**Priority:** High  
**Effort:** 1 day (8 hours)  
**Status:** Not Started

**Description:**
When platform admin is impersonating a tenant, disable all edit/delete/create operations to prevent accidental changes. Show warning banner on forms.

**Technical Components:**
1. **Impersonation Mode Detection**
   - Check session for `ImpersonatingTenantId`
   - Pass `IsImpersonating` flag to all pages

2. **UI Disabling**
   - Disable "Edit", "Delete", "Create" buttons
   - Add `disabled` attribute to form inputs
   - Show warning banner: "⚠️ Read-Only Mode (Impersonating)"

3. **Server-Side Enforcement**
   - Check impersonation in POST handlers
   - Return 403 Forbidden if impersonating
   - Log attempted changes during impersonation

4. **Allow List (Optional)**
   - Some actions might be allowed (e.g., viewing logs)
   - Configurable via `appsettings.json`

**Files to Create/Modify:**
- `MrWhoOidc.WebAuth/Pages/Admin/*/Edit.cshtml[.cs]` (update all - add read-only check)
- `MrWhoOidc.WebAuth/Pages/Admin/*/Create.cshtml[.cs]` (update all - add read-only check)
- `MrWhoOidc.WebAuth/Pages/Admin/*/Delete.cshtml[.cs]` (update all - add read-only check)
- `MrWhoOidc.WebAuth/Pages/Shared/_ReadOnlyBanner.cshtml` (new, ~30 lines)
- `MrWhoOidc.WebAuth/Pages/Shared/_Layout.cshtml` (update - include read-only banner)

**Dependencies:**
- Existing impersonation service (already implemented in Phase 5A)

---

### Feature 5: Database Audit Logging for Impersonation ⏳
**Priority:** Medium  
**Effort:** 1-2 days (8-16 hours)  
**Status:** Not Started

**Description:**
Log all impersonation events (start, stop) to database with timestamps, IP address, and tenant details. Create admin UI to view impersonation history.

**Technical Components:**
1. **Audit Log Table**
   - `ImpersonationAuditLogs` table
   - Columns: Id, UserId, TenantId, Action (Start/Stop), Timestamp, IpAddress, UserAgent, Duration

2. **Logging Integration**
   - Update `ImpersonationService.StartImpersonationAsync()` to log
   - Update `ImpersonationService.StopImpersonationAsync()` to log
   - Calculate duration on stop

3. **Admin UI**
   - `/PlatformAdmin/ImpersonationHistory` page
   - Filter by date range, user, tenant
   - Pagination (50 per page)
   - Export to CSV

4. **Security**
   - Only platform admins can view history
   - Audit logs are immutable (no deletion)

**Files to Create/Modify:**
- `MrWhoOidc.Auth/Persistence/Entities/ImpersonationAuditLog.cs` (new, ~40 lines)
- `MrWhoOidc.Auth/Persistence/AuthDbContext.cs` (update - add DbSet)
- `MrWhoOidc.WebAuth/Services/ImpersonationService.cs` (update - add logging)
- `MrWhoOidc.WebAuth/Pages/PlatformAdmin/ImpersonationHistory/Index.cshtml[.cs]` (new, ~120 lines)
- Migration: Add `ImpersonationAuditLogs` table

**Dependencies:**
- Existing impersonation service (already implemented in Phase 5A)

---

## Implementation Priority Recommendation

### Option 1: Security & Audit First (Recommended for Production)
1. ✅ **Feature 3: Session Metadata** (1 day) - Enhances security monitoring
2. ✅ **Feature 4: Read-Only Impersonation** (1 day) - Prevents accidental changes
3. ✅ **Feature 5: Audit Logging** (1-2 days) - Compliance and accountability
4. **Feature 1: Email Verification** (1-2 days) - User security
5. **Feature 2: External Identity Linking** (2-3 days) - User convenience

**Total Time:** 6-9 days

### Option 2: User Experience First
1. **Feature 1: Email Verification** (1-2 days) - User security
2. **Feature 3: Session Metadata** (1 day) - User awareness
3. **Feature 2: External Identity Linking** (2-3 days) - User convenience
4. **Feature 4: Read-Only Impersonation** (1 day) - Admin safety
5. **Feature 5: Audit Logging** (1-2 days) - Compliance

**Total Time:** 6-9 days

### Option 3: Quick Wins First
1. **Feature 3: Session Metadata** (1 day) - Low effort, high value
2. **Feature 4: Read-Only Impersonation** (1 day) - Low effort, high security value
3. **Feature 5: Audit Logging** (1-2 days) - Medium effort, high compliance value
4. **Feature 1: Email Verification** (1-2 days) - Medium effort
5. **Feature 2: External Identity Linking** (2-3 days) - High effort

**Total Time:** 6-9 days

---

## Risk Assessment

### Technical Risks
1. **Email Infrastructure** - SMTP configuration might be complex
   - **Mitigation:** Start with local dev SMTP (Papercut, MailHog)

2. **OAuth Provider Credentials** - Need Google/Microsoft app registrations
   - **Mitigation:** Use existing provider infrastructure if available

3. **User-Agent Parsing** - Browser detection can be fragile
   - **Mitigation:** Use established library (UAParser.NET)

4. **Read-Only Enforcement** - Need to update many pages
   - **Mitigation:** Create shared helper method/tag helper

### Security Risks
1. **Email Token Security** - Tokens must be cryptographically secure
   - **Mitigation:** Use GUID with HMAC signature

2. **Impersonation Logging** - Must be tamper-proof
   - **Mitigation:** Immutable audit logs, no DELETE permission

3. **External Provider Linking** - Account takeover risk if not validated
   - **Mitigation:** Require email match or explicit confirmation

---

## Testing Strategy

### Unit Tests (Per Feature)
- Email verification token generation/validation
- User-Agent parsing logic
- Read-only mode detection
- Audit log creation

### Integration Tests
- End-to-end email verification flow
- OAuth linking flow with mock provider
- Session metadata capture
- Read-only enforcement on all admin pages
- Audit log retrieval

### Manual Testing
- Email delivery and link clicking
- External provider linking (Google, Microsoft)
- Session list with different devices
- Impersonation with read-only mode active
- Audit history viewing and filtering

---

## Success Criteria

### Feature 1: Email Verification ✅
- [ ] Users can add alternative emails
- [ ] Verification emails sent automatically
- [ ] Email marked "Verified" after clicking link
- [ ] Expired tokens rejected
- [ ] Resend verification works

### Feature 3: Session Metadata ✅
- [ ] IP address displayed for each session
- [ ] Browser/OS detected and shown
- [ ] "This device" badge on current session
- [ ] No PII leakage (mask IPs if required)

### Feature 4: Read-Only Impersonation ✅
- [ ] All edit/delete buttons disabled during impersonation
- [ ] Warning banner visible on all admin pages
- [ ] POST handlers reject changes during impersonation
- [ ] Audit log shows attempted changes

### Feature 5: Audit Logging ✅
- [ ] All impersonation events logged
- [ ] Platform admin can view history
- [ ] Filtering works (date, user, tenant)
- [ ] Export to CSV functional
- [ ] Logs immutable

### Feature 2: External Identity Linking ✅
- [ ] Users can link Google/Microsoft accounts
- [ ] OAuth flow completes successfully
- [ ] Linked accounts shown in `/Account/LinkedAccounts`
- [ ] Users can unlink accounts
- [ ] Login via linked provider works

---

## Documentation Deliverables

1. **Feature Implementation Guides**
   - Email verification setup guide
   - OAuth provider configuration guide
   - Session metadata interpretation guide
   - Read-only mode configuration guide
   - Audit logging query examples

2. **Admin Guides**
   - How to review impersonation history
   - How to investigate suspicious sessions
   - Email verification troubleshooting

3. **User Guides**
   - How to verify alternative emails
   - How to link external accounts
   - How to review active sessions

---

## Next Steps

**Choose Implementation Order:**
1. Review the three options above
2. Select priority order based on business needs
3. Confirm dependencies (SMTP, OAuth credentials)
4. Begin with highest priority feature

**Recommended Start:** Feature 3 (Session Metadata) - Quick win, high value, no external dependencies.

Would you like me to proceed with:
- **Option 1:** Security & Audit First?
- **Option 2:** User Experience First?
- **Option 3:** Quick Wins First?
- **Custom Order:** Specify which features to implement

