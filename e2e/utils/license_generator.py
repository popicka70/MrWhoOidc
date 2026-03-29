"""
License JWT generator for E2E tests.

Generates signed Enterprise+ (or any tier) license tokens using a test
ECDSA P-256 keypair.  The corresponding public key must be trusted by
the WebAuth service via the Licensing__AdditionalPublicKeyPem setting.
"""

from __future__ import annotations

import json
import time
import uuid
from pathlib import Path
from typing import Sequence

from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives.serialization import load_pem_private_key
import jwt  # PyJWT

_FIXTURES_DIR = Path(__file__).resolve().parent.parent / "fixtures"
_DEFAULT_KEY_PATH = _FIXTURES_DIR / "licensing-test-private-key.pem"

# All 18 features (must match MrWhoOidc.Auth FeatureFlags)
ALL_FEATURES: list[str] = [
    "basic_oidc",
    "basic_admin_ui",
    "multi_tenancy",
    "advanced_security",
    "client_secret_rotation",
    "enhanced_audit_logging",
    "unlimited_scale",
    "dpop",
    "token_exchange",
    "backchannel_logout",
    "ldap_integration",
    "custom_claim_mappings",
    "advanced_monitoring",
    "webauthn",
    "risk_based_auth",
    "hsm_integration",
    "professional_services",
    "device_authorization_grant",
]


class LicenseGenerator:
    """Signs license JWTs with the E2E test private key."""

    def __init__(self, private_key_path: str | Path | None = None) -> None:
        key_path = Path(private_key_path) if private_key_path else _DEFAULT_KEY_PATH
        pem_bytes = key_path.read_bytes()
        self._private_key = load_pem_private_key(pem_bytes, password=None)
        if not isinstance(self._private_key, ec.EllipticCurvePrivateKey):
            raise TypeError("Expected an EC private key")

    def generate(
        self,
        *,
        tier: str = "enterprise+",
        organization: str = "E2E Test Organization",
        scope: str = "platform",
        deployment_mode: str = "multi_tenant",
        features: Sequence[str] | None = None,
        valid_seconds: int = 86400,
        issued_to: str | None = "E2E Test Suite",
        allowed_issuers: Sequence[str] | None = None,
        limits: dict | None = None,
    ) -> str:
        """
        Generate a signed license JWT.

        Returns the compact JWS string ready for installation.
        """
        now = int(time.time())
        jti = str(uuid.uuid4())
        feat = list(features) if features is not None else list(ALL_FEATURES)

        payload: dict = {
            "iss": "MrWhoOidc-KeyGen",
            "jti": jti,
            "iat": now,
            "nbf": now,
            "exp": now + valid_seconds,
            "tier": tier,
            "organization": organization,
            "license_scope": scope,
            "features": json.dumps(sorted(set(feat))),
        }

        if deployment_mode:
            payload["deployment_mode"] = deployment_mode

        if issued_to:
            payload["issued_to"] = issued_to

        if allowed_issuers:
            payload["allowed_issuers"] = json.dumps(list(allowed_issuers))

        if limits:
            payload["limits"] = json.dumps(limits)

        headers = {
            "typ": "JWT",
            "alg": "ES256",
            "kid": "licensing-key",
        }

        from cryptography.hazmat.primitives import serialization

        pem = self._private_key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
        return jwt.encode(payload, pem, algorithm="ES256", headers=headers)
