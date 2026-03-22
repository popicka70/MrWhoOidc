#!/bin/sh
set -eu

SCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
VENV_DIR="$SCRIPT_DIR/.venv"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required to create the E2E virtualenv." >&2
  exit 1
fi

python3 -m venv --without-pip "$VENV_DIR"

if ! "$VENV_DIR/bin/python" -m pip --version >/dev/null 2>&1; then
  site_packages=$(
    "$VENV_DIR/bin/python" - <<'PY'
import sysconfig

print(sysconfig.get_path("purelib"))
PY
  )
  python3 -m pip install --upgrade --target "$site_packages" pip setuptools wheel
fi

"$VENV_DIR/bin/python" -m pip install --upgrade pip
"$VENV_DIR/bin/python" -m pip install -r "$SCRIPT_DIR/requirements.txt"

cat <<'EOF'

E2E virtualenv is ready.

Activate it with:
  source e2e/.venv/bin/activate

Run tests with:
  cd e2e
  pytest -v

Install Playwright browsers once per machine with:
  e2e/.venv/bin/playwright install chromium
EOF