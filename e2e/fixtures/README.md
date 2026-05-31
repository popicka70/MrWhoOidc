# E2E Fixture Keys

`licensing-test-private-key.pem` and `licensing-test-public-key.pem` are deterministic test fixtures for local and CI end-to-end tests. They are intentionally tracked so the test suite is reproducible.

Do not use these keys for staging, production, customer licenses, or any environment that trusts real licensing decisions. Production key material must be generated outside the repository and supplied through a deployment secret or managed key store.