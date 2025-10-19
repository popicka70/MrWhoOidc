# License Key System - Feature Specification

## Overview

Implement a comprehensive license key system for MrWhoOidc that enables tiered functionality based on purchased licenses. The system should provide flexible licensing models with clear feature differentiation between tiers, secure license validation, and easy license management.

## User Story

**As a** product owner of MrWhoOidc
**I want** a license key system that controls feature access based on purchased licenses
**So that** I can monetize the product with different tiers while providing value at each level

## Functional Requirements

### FR1: License Tiers and Features

The system must support multiple license tiers with progressive feature access:

#### **Community Edition (Free)**
- Basic OIDC/OAuth 2.0 flows (authorize, token, userinfo)
- Up to 100 users
- Single tenant only
- Basic admin UI
- Standard security features (PKCE, basic audit logging)
- Community support only

#### **Professional Edition**
- All Community features plus:
- Up to 10,000 users
- Multi-tenancy (up to 5 tenants)
- Advanced security features (JAR, JARM, TOTP)
- Client secret rotation
- Enhanced audit logging
- Email support

#### **Enterprise Edition**
- All Professional features plus:
- Unlimited users and tenants
- Advanced features (DPoP, Token Exchange/OBO, Backchannel Logout)
- LDAP/AD integration
- Custom claim mappings
- SLA with priority support
- Advanced monitoring and alerting

#### **Enterprise+ Edition**
- All Enterprise features plus:
- WebAuthn/FIDO2 support (when implemented)
- Risk-based authentication (when implemented)
- HSM integration (when implemented)
- Professional services and custom development

### FR2: License Key Management

#### License Generation
- Generate cryptographically signed license keys
- Include license tier, expiration date, feature flags, user/tenant limits
- Support offline license validation
- License keys should be tamper-resistant

#### License Validation
- Validate license signature on startup and periodically
- Graceful degradation when license expires or is invalid
- Clear error messages for license issues
- License status dashboard in admin UI

#### License Installation
- Simple license key input via admin UI
- Command-line license installation support
- Environment variable or configuration file support
- License file upload capability

### FR3: Feature Gating

#### Runtime Feature Control
- Feature flags based on current license
- Graceful feature disabling when limits exceeded
- Clear user messaging when features are unavailable
- No data loss when downgrading licenses

#### User/Tenant Limits
- Enforce user count limits per license tier
- Enforce tenant count limits per license tier
- Soft warnings approaching limits
- Hard enforcement when limits exceeded

### FR4: Admin Interface

#### License Status Dashboard
- Current license tier and status
- License expiration date and days remaining
- Current usage vs. limits (users, tenants)
- Available features for current license
- License renewal/upgrade prompts

#### License Management
- License key input/update interface
- License history and audit trail
- Feature availability matrix
- Usage statistics and reporting

## Non-Functional Requirements

### NFR1: Security
- License keys must be cryptographically signed
- Private key for signing must be securely managed
- License validation must be tamper-resistant
- No sensitive license information in client-side code

### NFR2: Performance
- License validation must not significantly impact startup time (<100ms)
- Feature checks must be optimized for runtime performance
- Cache license status to avoid repeated validation

### NFR3: Reliability
- System must be usable when license server is unavailable (offline validation)
- Graceful handling of license expiration (grace period)
- No data corruption when license changes

### NFR4: Usability
- Clear messaging about license status and limitations
- Easy upgrade path between license tiers
- Intuitive license installation process

## Technical Requirements

### TR1: Architecture Integration
- Integrate with existing MrWhoOidc.Auth domain layer
- License validation service in core domain
- License-aware feature services
- Admin UI integration in MrWhoOidc.WebAuth

### TR2: Database Schema
- License information storage in existing AuthDbContext
- License history and audit trail
- Feature usage tracking
- Efficient queries for license validation

### TR3: Configuration
- License key storage in configuration or database
- Feature flag configuration based on license
- Environment-specific license management

## Acceptance Criteria

### AC1: License Tiers
- [ ] All four license tiers are defined with clear feature differentiation
- [ ] Feature matrix clearly shows what's available in each tier
- [ ] Smooth upgrade path between tiers without data loss

### AC2: License Key System
- [ ] Cryptographically signed license keys can be generated
- [ ] License keys can be validated offline
- [ ] License keys include all necessary information (tier, expiration, limits)
- [ ] Tamper detection prevents license modification

### AC3: Feature Gating
- [ ] All identified features are properly gated by license tier
- [ ] User and tenant limits are enforced
- [ ] Clear error messages when limits exceeded
- [ ] No functionality loss when within license limits

### AC4: Admin Interface
- [ ] License status is clearly visible in admin UI
- [ ] License keys can be easily installed/updated
- [ ] Current usage vs. limits is displayed
- [ ] License renewal notifications work correctly

### AC5: Security
- [ ] License validation cannot be bypassed
- [ ] Private signing keys are properly secured
- [ ] License information is not exposed to unauthorized users

## Success Metrics

- **License Adoption**: Track adoption rates across license tiers
- **Feature Usage**: Monitor which licensed features are most used
- **Conversion Rate**: Measure upgrades from Community to paid tiers
- **Support Reduction**: Fewer support requests due to clear licensing
- **Revenue Impact**: Successful monetization through tiered licensing

## Dependencies

- Existing MrWhoOidc architecture and authentication system
- Cryptographic libraries for license signing/validation
- Admin UI framework already in place
- Database migration capabilities

## Risks and Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| License bypass attempts | High | Strong cryptographic validation, code obfuscation |
| Performance impact | Medium | Efficient caching, optimized validation |
| User confusion about tiers | Medium | Clear documentation, intuitive UI |
| Migration complexity | Medium | Careful planning, gradual rollout |

## Out of Scope

- Payment processing and billing integration
- License server infrastructure (keys generated manually initially)
- Automatic license renewal
- Usage-based licensing (beyond user/tenant counts)

## Future Considerations

- Integration with license management platforms
- Usage-based pricing models
- Automatic license renewal and billing
- Partner/reseller license management
- Enterprise SSO for license management