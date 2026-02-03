---
sequence: 1
role: PromptRefinement
phase: prompt-refinement
iteration: 0
created: 2026-02-02T17:44:43.6053766Z
duration_ms: 69441
---

# PromptRefinement Agent Output

**Iteration:** 0
**Created:** 2026-02-02 17:44:43 UTC
**Duration:** 69441ms

---

Conduct a comprehensive code review of the `MrWhoOidc` solution. Analyze the codebase for security vulnerabilities (specifically OIDC/OAuth2 related), adherence to SOLID principles, and Clean Architecture boundaries. Evaluate C# code quality, including async/await usage, dependency injection, and error handling. Propose specific, actionable refactoring steps.

**Output format:** A structured report containing the following sections:
1.  **Executive Summary**: Overall code health and critical risk level.
2.  **Security Analysis**: Findings related to OIDC flows, token storage, input validation, and secret management (prioritized by severity).
3.  **Architecture & SOLID Review**: Violations of Clean Architecture (e.g., domain dependencies on infrastructure) and SOLID principles (e.g., SRP violations).
4.  **Code Quality Issues**: Specific C# anti-patterns (e.g., improper async handling, missing null checks, resource leaks).
5.  **Actionable Recommendations**: A prioritized list of specific changes with file paths and line numbers where applicable.

**Do NOT:**
- Provide generic advice that isn't tied to specific code locations in this project.
- Ignore the specific security requirements of an OIDC provider (e.g., token leakage, insecure redirect URIs).
- Suggest architectural changes that violate the existing Clean Architecture boundaries defined in the project.
- List every file in the repository; focus on areas with high complexity or security risk.
- Include raw code dumps in the report; summarize the issue and the proposed fix.
