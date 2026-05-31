"""
P14 — mTLS / certificate-bound access tokens (gap G18).

The dev stack terminates TLS without requesting client certificates, so the full
RFC 8705 certificate-bound token ceremony cannot be driven end-to-end here. This
asserts the advertised metadata and skip-guards the cert-bound flow, which
requires a client certificate presented to the token endpoint.
"""

from __future__ import annotations

import pytest

from utils.oidc_client import OidcClient


class TestMutualTlsMetadata:
    def test_01_cert_bound_tokens_advertised(self, oidc_client: OidcClient):
        value = oidc_client.discovery.get(
            "tls_client_certificate_bound_access_tokens"
        )
        assert value is True, (
            "tls_client_certificate_bound_access_tokens should be advertised as true"
        )

    def test_02_token_endpoint_auth_methods_present(self, oidc_client: OidcClient):
        methods = oidc_client.discovery.get(
            "token_endpoint_auth_methods_supported", []
        )
        assert methods, "token_endpoint_auth_methods_supported should be present"

    def test_03_cert_bound_token_flow(self, oidc_client: OidcClient):
        # Driving a cert-bound token requires presenting a client certificate to
        # the token endpoint (mTLS). The dev edge does not request client certs,
        # so this end-to-end path is not exercisable in this environment.
        aliases = oidc_client.discovery.get("mtls_endpoint_aliases")
        if not aliases:
            pytest.skip(
                "mTLS endpoint aliases not advertised and no client-cert plumbing "
                "in the dev stack — cert-bound token flow not exercisable"
            )
        pytest.skip(
            "Cert-bound token issuance requires a client certificate at the TLS "
            "layer, which the dev edge does not request"
        )
