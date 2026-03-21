"""
DPoP proof builder for E2E tests.

Generates EC P-256 keypairs and creates DPoP proof JWTs per RFC 9449
for testing proof-of-possession token binding.
"""

from __future__ import annotations

import base64
import hashlib
import json
import time
import uuid

from cryptography.hazmat.primitives.asymmetric import ec
from cryptography.hazmat.primitives import serialization
import jwt  # PyJWT


def _b64url(data: bytes) -> str:
    """Base64url-encode without padding."""
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode("ascii")


def _int_to_b64url(n: int, length: int) -> str:
    """Encode an integer as a base64url-encoded big-endian byte string."""
    return _b64url(n.to_bytes(length, byteorder="big"))


class DPoPProofBuilder:
    """Generates DPoP proof JWTs using an EC P-256 keypair."""

    def __init__(self) -> None:
        self._private_key = ec.generate_private_key(ec.SECP256R1())
        self._public_key = self._private_key.public_key()
        self._jwk = self._build_jwk()

    def _build_jwk(self) -> dict:
        """Build the public JWK dictionary for the EC key."""
        public_numbers = self._public_key.public_numbers()
        return {
            "kty": "EC",
            "crv": "P-256",
            "x": _int_to_b64url(public_numbers.x, 32),
            "y": _int_to_b64url(public_numbers.y, 32),
        }

    @property
    def public_jwk(self) -> dict:
        return dict(self._jwk)

    def jwk_thumbprint(self) -> str:
        """Compute JWK Thumbprint (RFC 7638) using SHA-256, base64url-encoded."""
        # For EC keys, canonical JSON: {"crv":"...","kty":"EC","x":"...","y":"..."}
        canonical = json.dumps(
            {
                "crv": self._jwk["crv"],
                "kty": self._jwk["kty"],
                "x": self._jwk["x"],
                "y": self._jwk["y"],
            },
            separators=(",", ":"),
            sort_keys=True,
        )
        digest = hashlib.sha256(canonical.encode("ascii")).digest()
        return _b64url(digest)

    def create_proof(
        self,
        htm: str,
        htu: str,
        *,
        ath: str | None = None,
        nonce: str | None = None,
    ) -> str:
        """
        Create a DPoP proof JWT.

        Args:
            htm: HTTP method (e.g. "POST")
            htu: HTTP target URI (e.g. "https://as.example.com/token")
            ath: Access token hash (base64url SHA-256 of the access token)
            nonce: Server-provided DPoP nonce
        """
        headers = {
            "typ": "dpop+jwt",
            "alg": "ES256",
            "jwk": self._jwk,
        }
        payload: dict = {
            "jti": str(uuid.uuid4()),
            "iat": int(time.time()),
            "htm": htm,
            "htu": htu,
        }
        if ath:
            payload["ath"] = ath
        if nonce:
            payload["nonce"] = nonce

        # PyJWT's encode with algorithm="ES256" and the EC private key
        pem = self._private_key.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption(),
        )
        return jwt.encode(payload, pem, algorithm="ES256", headers=headers)

    @staticmethod
    def compute_ath(access_token: str) -> str:
        """Compute the access token hash (ath) for DPoP proof binding."""
        digest = hashlib.sha256(access_token.encode("ascii")).digest()
        return _b64url(digest)
