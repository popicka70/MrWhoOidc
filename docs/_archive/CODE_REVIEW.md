# Code Review Guidelines

> **ARCHIVED DOCUMENT** - This document has been moved to the archive. Please refer to current development practices.

## Purpose

This document provides guidelines for code reviews in the MrWhoOidc project.

## Review Checklist

### Code Quality
- [ ] Code follows project conventions
- [ ] No compiler warnings
- [ ] Proper error handling
- [ ] Input validation present
- [ ] No hardcoded values

### Security
- [ ] No sensitive data in logs
- [ ] Proper authentication/authorization checks
- [ ] SQL injection prevention
- [ ] XSS prevention
- [ ] CSRF protection where applicable

### Testing
- [ ] Unit tests included
- [ ] Tests cover edge cases
- [ ] Integration tests for new endpoints
- [ ] Existing tests still pass

### Documentation
- [ ] XML comments for public APIs
- [ ] Updated README or guides if needed
- [ ] Changelog entry for significant changes

### Performance
- [ ] No obvious performance issues
- [ ] Database queries are efficient
- [ ] Caching used appropriately

## Review Process

1. **Author** creates pull request
2. **Automated checks** run (CI/CD)
3. **Reviewer** examines code
4. **Feedback** provided within 24 hours
5. **Author** addresses feedback
6. **Approval** from at least one maintainer
7. **Merge** by maintainer

## Reviewer Responsibilities

- Provide constructive feedback
- Explain reasoning for suggested changes
- Approve when requirements are met
- Flag security concerns immediately

## Author Responsibilities

- Respond to feedback promptly
- Make requested changes or explain why not
- Ensure all checks pass before requesting review
- Update documentation as needed

---

**Note:** This document is archived. Current practices may differ.
