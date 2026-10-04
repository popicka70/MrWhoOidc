#!/bin/sh
# Entrypoint for the development/example images (MrWhoOidc.WebAuth/Dockerfile, Examples/*/Dockerfile).
# Runs as the non-root `app` user, so it changes nothing outside a private temp directory:
#
# - Dev CA trust: when DEV_PFX_TRUST_SOURCE points at the mounted dev PFX, its certificate is appended
#   to a copy of the system CA bundle and SSL_CERT_FILE points dotnet (OpenSSL) at that copy. The system
#   store (/etc/ssl, update-ca-certificates) is not touched.
# - "localhost" -> host: docker-compose.dev.yml adds `localhost:host-gateway` via extra_hosts. The resolver
#   returns 127.0.0.1 first and the host gateway after it; a port that is not served inside the container
#   is refused on loopback and dotnet's connect falls through to the gateway. /etc/hosts is not edited.
set -eu

if [ -n "${DEV_PFX_TRUST_SOURCE:-}" ] && [ -f "${DEV_PFX_TRUST_SOURCE}" ]; then
  trust_dir=$(mktemp -d "${TMPDIR:-/tmp}/mrwhooidc-dev-trust.XXXXXX")
  cert_tmp="${trust_dir}/dev-ca.pem"
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
    system_bundle="${SSL_CERT_FILE:-/etc/ssl/certs/ca-certificates.crt}"
    bundle="${trust_dir}/ca-bundle.crt"
    if [ -f "${system_bundle}" ]; then
      cat "${system_bundle}" "${cert_tmp}" > "${bundle}"
    else
      cp "${cert_tmp}" "${bundle}"
    fi
    export SSL_CERT_FILE="${bundle}"
  else
    echo "run-dotnet-with-dev-cert: could not read a certificate from ${DEV_PFX_TRUST_SOURCE}; dev CA not trusted" >&2
  fi
  rm -f "${cert_tmp}"
fi

exec dotnet "$@"
