#!/bin/sh
set -eu

if [ "${MRWHO_LOCALHOST_TO_HOST_GATEWAY:-false}" = "true" ] && [ -w /etc/hosts ]; then
  tmp_hosts=$(mktemp)
  grep -vE '^(127\.0\.0\.1|::1)[[:space:]].*localhost' /etc/hosts > "${tmp_hosts}"
  cat "${tmp_hosts}" > /etc/hosts
  rm -f "${tmp_hosts}"
fi

if [ -n "${DEV_PFX_TRUST_SOURCE:-}" ] && [ -f "${DEV_PFX_TRUST_SOURCE}" ]; then
  cert_target="/usr/local/share/ca-certificates/mrwhooidc-dev-ca.crt"
  cert_tmp=$(mktemp)
  rm -f "${cert_target}"
  if ! openssl pkcs12 \
    -legacy \
    -in "${DEV_PFX_TRUST_SOURCE}" \
    -cacerts \
    -chain \
    -nokeys \
    -nodes \
    -out "${cert_tmp}" \
    -passin "pass:${DEV_PFX_TRUST_PASSWORD:-changeit}" >/dev/null 2>&1 || [ ! -s "${cert_tmp}" ]; then
    openssl pkcs12 \
      -legacy \
      -in "${DEV_PFX_TRUST_SOURCE}" \
      -clcerts \
      -nokeys \
      -nodes \
      -out "${cert_tmp}" \
      -passin "pass:${DEV_PFX_TRUST_PASSWORD:-changeit}" >/dev/null 2>&1 || true
  fi

  if [ -s "${cert_tmp}" ]; then
    mv "${cert_tmp}" "${cert_target}"
    update-ca-certificates >/dev/null 2>&1
  else
    rm -f "${cert_tmp}"
  fi
fi

exec dotnet "$@"