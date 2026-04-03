#!/usr/bin/env bash

set -u

BASE_URL="${1:-https://localhost:8443}"
TENANT_SLUG="${2:-default}"
DISCOVERY_URL="${BASE_URL}/t/${TENANT_SLUG}/.well-known/openid-configuration"
ADMIN_URL="${BASE_URL}/admin/clients"
HEALTH_URL="${BASE_URL}/health"
DEMO_URL="https://localhost:5001"
RAZOR_URL="https://localhost:5003"
REACT_URL="http://localhost:5173"
TESTAPI_URL="https://localhost:7149/health"

if docker compose version >/dev/null 2>&1; then
    COMPOSE_CMD=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
    COMPOSE_CMD=(docker-compose)
else
    COMPOSE_CMD=()
fi

PASS_COUNT=0
FAIL_COUNT=0

pass() {
    printf 'PASS %s\n' "$1"
    PASS_COUNT=$((PASS_COUNT + 1))
}

fail() {
    printf 'FAIL %s\n' "$1"
    FAIL_COUNT=$((FAIL_COUNT + 1))
}

check_json_contains() {
    local url="$1"
    local field="$2"
    local label="$3"
    local response

    response="$(curl -k -fsS "$url" 2>/dev/null || true)"
    if printf '%s' "$response" | grep -q '"'"$field"'"'; then
        pass "$label"
    else
        fail "$label"
    fi
}

check_status() {
    local url="$1"
    local expected="$2"
    local label="$3"
    local code

    code="$(curl -k -s -o /dev/null -w '%{http_code}' -L "$url" 2>/dev/null)" || code="000"
    if [ "$code" = "$expected" ]; then
        pass "$label"
    else
        fail "$label (HTTP ${code}, expected ${expected})"
    fi
}

printf 'Verifying MrWhoOidc installation\n'
printf 'Base URL: %s\n' "$BASE_URL"
printf 'Tenant: %s\n\n' "$TENANT_SLUG"

check_json_contains "$DISCOVERY_URL" issuer "Tenant discovery returns issuer"
check_json_contains "$DISCOVERY_URL" authorization_endpoint "Tenant discovery returns authorization endpoint"
check_json_contains "$DISCOVERY_URL" token_endpoint "Tenant discovery returns token endpoint"
check_status "$ADMIN_URL" 200 "Admin route resolves to login or admin UI"
check_status "$HEALTH_URL" 200 "Health endpoint responds"
check_status "$DEMO_URL" 200 "OidcDemo responds"
check_status "$RAZOR_URL" 200 "RazorClient responds"
check_status "$REACT_URL" 200 "ReactOidcClient responds"
check_status "$TESTAPI_URL" 200 "TestApi health responds"

if [ ${#COMPOSE_CMD[@]} -gt 0 ] && [ -f docker-compose.dev.yml ]; then
    printf '\nCompose status:\n'
    "${COMPOSE_CMD[@]}" -f docker-compose.dev.yml ps || true
fi

printf '\nSummary: %s passed, %s failed\n' "$PASS_COUNT" "$FAIL_COUNT"

if [ "$FAIL_COUNT" -ne 0 ]; then
    printf 'See docs/troubleshooting/local-development.md for common fixes.\n'
    exit 1
fi

exit 0
