# ADR 002: Password hashing

Status: Accepted
Date: 2025-09-20

Context
- We need a modern password hashing algorithm with parameters suitable for developer machines and CI.

Decision
- Use Argon2id via Isopoh for password hashing. Store algorithm name along with hash to enable future upgrades.

Rationale
- Argon2id resists GPU attacks and offers balanced memory/time costs.

Operational guidance
- Algorithm id: `argon2id`
- Verify using our `IPasswordHasher` abstraction.
- Consider increasing cost parameters for production environments.
