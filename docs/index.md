# MrWhoOidc Documentation Hub

Welcome to the MrWhoOidc documentation. This index provides audience-based entry points to help you find the right documentation for your needs.

## 🎯 Find Your Path

### 👨‍💻 For Developers

Building applications with MrWhoOidc or extending the platform?

**Start Here:**
- **[15-Minute Quickstart](for-developers/quickstart-15-min.md)** - Get running locally in 15 minutes
- **[Developer Guide](developer-guide.md)** - Comprehensive development documentation
- **[API Reference](../MrWhoOidc.Auth/)** - Source code API documentation

**Common Tasks:**
- [Client Integration Examples](../Examples/)
- [Token Exchange Guide](reference/obo-client-policy.md)
- [IdP Chaining Configuration](reference/idp-chaining-client-configuration.md)

---

### 🔧 For Operators

Running MrWhoOidc in production?

**Start Here:**
- **[Production Setup Guide](production-setup-guide.md)** - Deploy to production
- **[Deployment Guide](deployment-guide.md)** - Complete deployment lifecycle
- **[Monitoring & Alerting](for-operators/monitoring/alerting-rules.md)** - Prometheus rules and alerts

**Common Tasks:**
- [Backup & Restore Procedures](for-operators/backup-restore/verification-testing.md)
- [Upgrade Guide](upgrade-guide.md)
- [Docker Security Best Practices](docker-security-best-practices.md)
- [Configuration Examples](docker-compose-examples.md)

---

### 👔 For Administrators

Managing tenants, users, and configurations?

**Start Here:**
- **[Admin Guide](admin-guide.md)** - Complete administrative reference
- **[Production Setup Guide](production-setup-guide.md)** - Initial bootstrap process

**Common Tasks:**
- [Tenant Management](admin-guide.md#tenant-management)
- [User Management](admin-guide.md#user-management)
- [Client Configuration](admin-guide.md#client-configuration)
- [Identity Provider Setup](admin-guide.md#identity-providers)
- [Audit Log Access](admin-guide.md#audit-logging)

---

### 🔒 For Security Teams

Security assessments, incident response, and compliance?

**Start Here:**
- **[Incident Response Plan](for-security-teams/incident-response.md)** - Security incident procedures
- **[Security Best Practices](docker-security-best-practices.md)** - Hardening guide

**Common Tasks:**
- [Security Assessment Checklist](security/)
- [Key Rotation Procedures](for-security-teams/incident-response.md#key-compromise-response)
- [Audit Trail Access](for-security-teams/incident-response.md#audit-trail-access-procedures)
- [OIDC Conformance Checklist](oidc-conformance-checklist.md)

---

## 📚 Documentation Categories

### Getting Started
| Document | Description |
|----------|-------------|
| [README](../README.md) | Project overview and quick start |
| [15-Minute Quickstart](for-developers/quickstart-15-min.md) | Local development setup |
| [Production Setup](production-setup-guide.md) | Production deployment guide |

### Deployment & Operations
| Document | Description |
|----------|-------------|
| [Deployment Guide](deployment-guide.md) | Complete deployment lifecycle |
| [Docker Compose Examples](docker-compose-examples.md) | Production deployment scenarios |
| [Upgrade Guide](upgrade-guide.md) | Upgrade procedures and rollback |
| [Monitoring & Alerting](for-operators/monitoring/alerting-rules.md) | Prometheus alerting rules |
| [Backup & Restore](for-operators/backup-restore/verification-testing.md) | Backup verification procedures |

### Security
| Document | Description |
|----------|-------------|
| [Incident Response Plan](for-security-teams/incident-response.md) | Security incident procedures |
| [Docker Security Best Practices](docker-security-best-practices.md) | Container hardening guide |
| [OIDC Conformance Checklist](oidc-conformance-checklist.md) | Protocol compliance checklist |

### Protocol Reference
| Document | Description |
|----------|-------------|
| [OIDC IdP Feature Reference](oidc-idp-feature-reference.md) | Spec-based feature catalog for an OpenID Connect Identity Provider |
| [OBO Client Policy](reference/obo-client-policy.md) | Token exchange configuration |
| [Token Exchange E2E](reference/obo-dpop-requiresamejkt-e2e.md) | DPoP with RequireSameJkt |
| [IdP Chaining Configuration](reference/idp-chaining-client-configuration.md) | Multi-level IdP setups |
| [JAR Replay Cache](reference/jar-replay-cache.md) | JWT-secured request caching |

### Administration
| Document | Description |
|----------|-------------|
| [Admin Guide](admin-guide.md) | Complete administrative reference |
| [Dynamic Client Registration](dynamic-client-autoregistration-spec.md) | Client auto-registration spec |

### Architecture & Design
| Document | Description |
|----------|-------------|
| [ADR Index](adr/) | Architecture Decision Records |
| [Feature Gap Analysis](oidc-feature-gap-analysis.md) | OIDC feature coverage |
| [Well-Known IdP Providers Plan](well-known-idp-providers-plan.md) | IdP discovery planning |

---

## 🔍 Search Guidance

### Finding Information

1. **By Task:** Look in the audience section above for your role
2. **By Topic:** Use the category tables to find specific topics
3. **By Document Type:** 
   - Guides: Step-by-step instructions
   - Reference: Technical specifications
   - Examples: Code samples and configurations
   - ADRs: Architecture decisions

### Using GitHub Search

For advanced searches, use GitHub's search:
```bash
# Search for specific topics in docs
path:docs/ "token exchange"

# Find configuration examples
path:docs/ "docker-compose" language:yaml

# Search ADRs
path:docs/adr/ "database"
```

---

## 📖 Contribution Guidelines

Want to improve the documentation? We welcome contributions!

### How to Contribute

1. **Fork the repository**
2. **Create a branch** for your changes
3. **Make your changes** following our style guide
4. **Test locally** - ensure links work and examples are valid
5. **Submit a pull request**

### Documentation Standards

- **Clarity:** Write for the intended audience
- **Completeness:** Include all necessary steps and context
- **Accuracy:** Test all commands and code examples
- **Consistency:** Follow existing formatting and style
- **Links:** Use relative paths for internal links

### File Organization

```
docs/
├── index.md                    # This file - documentation hub
├── for-developers/             # Developer-focused docs
├── for-operators/              # Operations and monitoring
├── for-administrators/         # Administrative tasks
├── for-security-teams/         # Security documentation
├── troubleshooting/            # Common issues and solutions
├── reference/                  # Protocol and API reference
├── adr/                        # Architecture Decision Records
├── security/                   # Security assessments
└── _archive/                   # Historical/internal documents
```

### Review Process

1. **Automated checks:** Links, spelling, formatting
2. **Technical review:** Subject matter expert review
3. **Editorial review:** Clarity and completeness
4. **Merge:** After approval from maintainers

---

## 📞 Getting Help

### Support Channels

- **GitHub Issues:** Bug reports and feature requests
- **GitHub Discussions:** Questions and community support
- **Security Issues:** Report via [security policy](../.github/SECURITY.md)

### Documentation Feedback

Found an issue or have suggestions?
- **Fix it:** Submit a pull request
- **Report it:** Open a GitHub issue with the "documentation" label

---

## 📋 Quick Reference

### Essential Links

| Resource | URL |
|----------|-----|
| GitHub Repository | https://github.com/popicka70/MrWhoOidc |
| Docker Images | https://ghcr.io/popicka70/mrwhooidc |
| Issue Tracker | https://github.com/popicka70/MrWhoOidc/issues |
| Security Policy | https://github.com/popicka70/MrWhoOidc/blob/main/.github/SECURITY.md |

### Key Endpoints

| Endpoint | Description |
|----------|-------------|
| `/.well-known/openid-configuration` | OIDC discovery document |
| `/health` | Health check endpoint |
| `/ready` | Readiness check endpoint |
| `/metrics` | Prometheus metrics |
| `/admin` | Administrative UI |

---

**Documentation Version:** 1.0  
**Last Updated:** 2025-01-XX  
**Maintained By:** MrWhoOidc Team
