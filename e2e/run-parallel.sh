#!/usr/bin/env bash
set -euo pipefail
# Run the E2E suite partially in parallel with pytest-xdist.
# Each test file runs wholly on one worker (intra-file order preserved).
# Usage: ./run-parallel.sh [pytest args...]
# Env:   E2E_WORKERS (default 3)
E2E_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKERS="${E2E_WORKERS:-3}"
cd "$E2E_DIR"
exec ./.venv/bin/python -m pytest -n "$WORKERS" --dist loadfile "$@"
