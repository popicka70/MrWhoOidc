"""
OIDC HTTP client for E2E flow tests.

Provides direct HTTP calls to the OIDC provider's protocol endpoints:
discovery, token (all grant types), userinfo, and revocation.
Includes JWT decoding for claim validation.
"""

from __future__ import annotations

import base64
import hashlib
import json
import secrets
import urllib.parse
from dataclasses import dataclass, field
from typing import Any

import requests


@dataclass
class TokenResponse:
    """Parsed token endpoint response."""

    status_code: int
    raw: dict[str, Any]

    @property
    def ok(self) -> bool:
        return self.status_code == 200

    @property
    def access_token(self) -> str | None:
        return self.raw.get("access_token")

    @property
    def id_token(self) -> str | None:
        return self.raw.get("id_token")

    @property
    def refresh_token(self) -> str | None:
        return self.raw.get("refresh_token")

    @property
    def token_type(self) -> str | None:
        return self.raw.get("token_type")

    @property
    def expires_in(self) -> int | None:
        return self.raw.get("expires_in")

    @property
    def scope(self) -> str | None:
        return self.raw.get("scope")

    @property
    def error(self) -> str | None:
        return self.raw.get("error")

    @property
    def error_description(self) -> str | None:
        return self.raw.get("error_description")


def _b64url_decode(s: str) -> bytes:
    """Base64url-decode with padding."""
    s += "=" * (4 - len(s) % 4)
    return base64.urlsafe_b64decode(s)


def _build_oauth_basic_auth_header(client_id: str, client_secret: str) -> str:
    """Build RFC 6749-compliant client_secret_basic credentials."""
    encoded_client_id = urllib.parse.quote_plus(client_id)
    encoded_client_secret = urllib.parse.quote_plus(client_secret)
    credentials = f"{encoded_client_id}:{encoded_client_secret}".encode("utf-8")
    return f"Basic {base64.b64encode(credentials).decode('ascii')}"


def decode_jwt(token: str) -> tuple[dict[str, Any], dict[str, Any]]:
    """Decode a JWT without signature verification. Returns (header, payload)."""
    parts = token.split(".")
    if len(parts) < 2:
        raise ValueError("Not a valid JWT")
    header = json.loads(_b64url_decode(parts[0]))
    payload = json.loads(_b64url_decode(parts[1]))
    return header, payload


def generate_pkce() -> tuple[str, str]:
    """Generate PKCE code_verifier and code_challenge (S256)."""
    verifier = secrets.token_urlsafe(64)
    digest = hashlib.sha256(verifier.encode("ascii")).digest()
    challenge = base64.urlsafe_b64encode(digest).rstrip(b"=").decode("ascii")
    return verifier, challenge


class OidcClient:
    """HTTP client for OIDC protocol endpoints."""

    def __init__(self, issuer_url: str, *, verify_ssl: bool = False) -> None:
        self.issuer_url = issuer_url.rstrip("/")
        self.verify_ssl = verify_ssl
        self._session = requests.Session()
        self._session.verify = verify_ssl
        self._discovery: dict[str, Any] | None = None

    # ------------------------------------------------------------------
    # Discovery
    # ------------------------------------------------------------------

    def discover(self) -> dict[str, Any]:
        """Fetch and cache the OpenID Connect discovery document."""
        resp = self._session.get(
            f"{self.issuer_url}/.well-known/openid-configuration"
        )
        resp.raise_for_status()
        self._discovery = resp.json()
        return self._discovery

    def _endpoint(self, name: str) -> str:
        """Get an endpoint URL from the cached discovery document."""
        if not self._discovery:
            self.discover()
        url = self._discovery.get(name)  # type: ignore[union-attr]
        if not url:
            raise ValueError(f"Endpoint '{name}' not in discovery document")
        return url

    @property
    def token_endpoint(self) -> str:
        return self._endpoint("token_endpoint")

    @property
    def authorization_endpoint(self) -> str:
        return self._endpoint("authorization_endpoint")

    @property
    def userinfo_endpoint(self) -> str:
        return self._endpoint("userinfo_endpoint")

    @property
    def revocation_endpoint(self) -> str:
        return self._endpoint("revocation_endpoint")

    @property
    def issuer(self) -> str:
        if not self._discovery:
            self.discover()
        return self._discovery["issuer"]  # type: ignore[index]

    # ------------------------------------------------------------------
    # Authorization URL builder
    # ------------------------------------------------------------------

    def build_authorize_url(
        self,
        client_id: str,
        redirect_uri: str,
        scope: str = "openid profile",
        *,
        code_challenge: str | None = None,
        code_challenge_method: str = "S256",
        state: str | None = None,
        nonce: str | None = None,
        extra_params: dict[str, str] | None = None,
    ) -> str:
        """Build an authorization URL with PKCE and standard parameters."""
        params: dict[str, str] = {
            "response_type": "code",
            "client_id": client_id,
            "redirect_uri": redirect_uri,
            "scope": scope,
        }
        if code_challenge:
            params["code_challenge"] = code_challenge
            params["code_challenge_method"] = code_challenge_method
        if state:
            params["state"] = state
        if nonce:
            params["nonce"] = nonce
        if extra_params:
            params.update(extra_params)
        return f"{self.authorization_endpoint}?{urllib.parse.urlencode(params)}"

    # ------------------------------------------------------------------
    # Token endpoint calls
    # ------------------------------------------------------------------

    def _token_request(
        self,
        data: dict[str, str],
        *,
        client_id: str | None = None,
        client_secret: str | None = None,
        auth_method: str = "post",
        dpop_header: str | None = None,
    ) -> TokenResponse:
        """Issue a POST to the token endpoint."""
        headers: dict[str, str] = {
            "Content-Type": "application/x-www-form-urlencoded",
        }

        if auth_method == "basic" and client_id and client_secret:
            headers["Authorization"] = _build_oauth_basic_auth_header(client_id, client_secret)
        elif auth_method == "post" and client_id:
            data["client_id"] = client_id
            if client_secret:
                data["client_secret"] = client_secret

        if dpop_header:
            headers["DPoP"] = dpop_header

        resp = self._session.post(
            self.token_endpoint,
            data=data,
            headers=headers,
            allow_redirects=False,
        )
        try:
            body = resp.json()
        except Exception:
            body = {"raw_body": resp.text}
        return TokenResponse(status_code=resp.status_code, raw=body)

    def token_client_credentials(
        self,
        client_id: str,
        client_secret: str,
        scope: str = "openid",
        *,
        audience: str | None = None,
        resource: str | None = None,
        auth_method: str = "post",
        dpop_header: str | None = None,
    ) -> TokenResponse:
        """Acquire a token using the client_credentials grant."""
        data: dict[str, str] = {
            "grant_type": "client_credentials",
            "scope": scope,
        }
        if audience:
            data["audience"] = audience
        if resource:
            data["resource"] = resource
        return self._token_request(
            data,
            client_id=client_id,
            client_secret=client_secret,
            auth_method=auth_method,
            dpop_header=dpop_header,
        )

    def token_authorization_code(
        self,
        code: str,
        redirect_uri: str,
        client_id: str,
        *,
        client_secret: str | None = None,
        code_verifier: str | None = None,
        auth_method: str = "post",
        dpop_header: str | None = None,
    ) -> TokenResponse:
        """Exchange an authorization code for tokens."""
        data: dict[str, str] = {
            "grant_type": "authorization_code",
            "code": code,
            "redirect_uri": redirect_uri,
        }
        if code_verifier:
            data["code_verifier"] = code_verifier
        return self._token_request(
            data,
            client_id=client_id,
            client_secret=client_secret,
            auth_method=auth_method,
            dpop_header=dpop_header,
        )

    def token_refresh(
        self,
        refresh_token: str,
        client_id: str,
        *,
        client_secret: str | None = None,
        scope: str | None = None,
        auth_method: str = "post",
    ) -> TokenResponse:
        """Use a refresh token to get new tokens."""
        data: dict[str, str] = {
            "grant_type": "refresh_token",
            "refresh_token": refresh_token,
        }
        if scope:
            data["scope"] = scope
        return self._token_request(
            data,
            client_id=client_id,
            client_secret=client_secret,
            auth_method=auth_method,
        )

    def token_exchange(
        self,
        subject_token: str,
        client_id: str,
        client_secret: str,
        *,
        audience: str | None = None,
        resource: str | None = None,
        scope: str | None = None,
        subject_token_type: str = "urn:ietf:params:oauth:token-type:access_token",
        auth_method: str = "post",
        dpop_header: str | None = None,
    ) -> TokenResponse:
        """Perform token exchange (OBO)."""
        data: dict[str, str] = {
            "grant_type": "urn:ietf:params:oauth:grant-type:token-exchange",
            "subject_token": subject_token,
            "subject_token_type": subject_token_type,
        }
        if audience:
            data["audience"] = audience
        if resource:
            data["resource"] = resource
        if scope:
            data["scope"] = scope
        return self._token_request(
            data,
            client_id=client_id,
            client_secret=client_secret,
            auth_method=auth_method,
            dpop_header=dpop_header,
        )

    # ------------------------------------------------------------------
    # Userinfo
    # ------------------------------------------------------------------

    def userinfo(
        self,
        access_token: str,
        *,
        dpop_header: str | None = None,
    ) -> tuple[int, dict[str, Any]]:
        """Call the userinfo endpoint. Returns (status_code, json_body)."""
        headers: dict[str, str] = {
            "Authorization": f"Bearer {access_token}",
        }
        if dpop_header:
            headers["Authorization"] = f"DPoP {access_token}"
            headers["DPoP"] = dpop_header
        resp = self._session.get(self.userinfo_endpoint, headers=headers)
        try:
            body = resp.json()
        except Exception:
            body = {"raw_body": resp.text}
        return resp.status_code, body

    # ------------------------------------------------------------------
    # Revocation
    # ------------------------------------------------------------------

    def revoke(
        self,
        token: str,
        client_id: str,
        *,
        client_secret: str | None = None,
        token_type_hint: str | None = None,
    ) -> int:
        """Revoke a token. Returns the HTTP status code."""
        data: dict[str, str] = {
            "token": token,
            "client_id": client_id,
        }
        if client_secret:
            data["client_secret"] = client_secret
        if token_type_hint:
            data["token_type_hint"] = token_type_hint
        resp = self._session.post(
            self.revocation_endpoint,
            data=data,
            headers={"Content-Type": "application/x-www-form-urlencoded"},
        )
        return resp.status_code

    # ------------------------------------------------------------------
    # Introspection
    # ------------------------------------------------------------------

    def introspect(
        self,
        token: str,
        client_id: str,
        *,
        client_secret: str | None = None,
        token_type_hint: str | None = None,
        auth_method: str = "post",
    ) -> tuple[int, dict[str, Any]]:
        """Introspect a token (RFC 7662). Returns (status_code, json_body)."""
        data: dict[str, str] = {"token": token}
        if token_type_hint:
            data["token_type_hint"] = token_type_hint
        headers: dict[str, str] = {
            "Content-Type": "application/x-www-form-urlencoded",
        }
        if auth_method == "basic" and client_id and client_secret:
            headers["Authorization"] = _build_oauth_basic_auth_header(client_id, client_secret)
        else:
            data["client_id"] = client_id
            if client_secret:
                data["client_secret"] = client_secret

        endpoint = self._endpoint("introspection_endpoint")
        resp = self._session.post(endpoint, data=data, headers=headers)
        try:
            body = resp.json()
        except Exception:
            body = {"raw_body": resp.text}
        return resp.status_code, body

    # ------------------------------------------------------------------
    # Pushed Authorization Requests (PAR)
    # ------------------------------------------------------------------

    @property
    def par_endpoint(self) -> str | None:
        """Get the PAR endpoint from discovery, or None if not advertised."""
        if not self._discovery:
            self.discover()
        return self._discovery.get("pushed_authorization_request_endpoint")  # type: ignore[union-attr]

    def pushed_authorization_request(
        self,
        client_id: str,
        *,
        client_secret: str | None = None,
        redirect_uri: str,
        scope: str = "openid",
        code_challenge: str | None = None,
        code_challenge_method: str = "S256",
        state: str | None = None,
        nonce: str | None = None,
        extra_params: dict[str, str] | None = None,
        auth_method: str = "post",
    ) -> tuple[int, dict[str, Any]]:
        """Push an authorization request to the PAR endpoint (RFC 9126).

        Returns (status_code, json_body) where body contains request_uri and expires_in on success.
        """
        endpoint = self.par_endpoint
        if not endpoint:
            raise ValueError("PAR endpoint not advertised in discovery")

        data: dict[str, str] = {
            "response_type": "code",
            "redirect_uri": redirect_uri,
            "scope": scope,
        }
        if code_challenge:
            data["code_challenge"] = code_challenge
            data["code_challenge_method"] = code_challenge_method
        if state:
            data["state"] = state
        if nonce:
            data["nonce"] = nonce
        if extra_params:
            data.update(extra_params)

        headers: dict[str, str] = {
            "Content-Type": "application/x-www-form-urlencoded",
        }
        if auth_method == "basic" and client_id and client_secret:
            headers["Authorization"] = _build_oauth_basic_auth_header(client_id, client_secret)
        else:
            data["client_id"] = client_id
            if client_secret:
                data["client_secret"] = client_secret

        resp = self._session.post(endpoint, data=data, headers=headers)
        try:
            body = resp.json()
        except Exception:
            body = {"raw_body": resp.text}
        return resp.status_code, body

    # ------------------------------------------------------------------
    # End Session
    # ------------------------------------------------------------------

    @property
    def end_session_endpoint(self) -> str:
        return self._endpoint("end_session_endpoint")

    def end_session(
        self,
        *,
        id_token_hint: str | None = None,
        post_logout_redirect_uri: str | None = None,
        state: str | None = None,
        client_id: str | None = None,
    ) -> tuple[int, str]:
        """Call the end_session endpoint. Returns (status_code, response_body)."""
        params: dict[str, str] = {}
        if id_token_hint:
            params["id_token_hint"] = id_token_hint
        if post_logout_redirect_uri:
            params["post_logout_redirect_uri"] = post_logout_redirect_uri
        if state:
            params["state"] = state
        if client_id:
            params["client_id"] = client_id

        url = self.end_session_endpoint
        if params:
            url = f"{url}?{urllib.parse.urlencode(params)}"
        resp = self._session.get(url, allow_redirects=False)
        return resp.status_code, resp.text

    # ------------------------------------------------------------------
    # Raw request helper (for custom / malformed requests)
    # ------------------------------------------------------------------

    def raw_token_request(
        self,
        data: dict[str, str],
        *,
        headers: dict[str, str] | None = None,
    ) -> tuple[int, dict[str, Any]]:
        """Send an arbitrary POST to the token endpoint. For negative testing."""
        hdrs = {"Content-Type": "application/x-www-form-urlencoded"}
        if headers:
            hdrs.update(headers)
        resp = self._session.post(self.token_endpoint, data=data, headers=hdrs)
        try:
            body = resp.json()
        except Exception:
            body = {"raw_body": resp.text}
        return resp.status_code, body

    def raw_get(self, url: str, *, headers: dict[str, str] | None = None) -> tuple[int, Any]:
        """Send an arbitrary GET request. For negative testing."""
        resp = self._session.get(url, headers=headers or {})
        try:
            body = resp.json()
        except Exception:
            body = resp.text
        return resp.status_code, body

    def raw_post(
        self,
        url: str,
        *,
        data: dict[str, str] | None = None,
        json_body: Any | None = None,
        headers: dict[str, str] | None = None,
        allow_redirects: bool = True,
    ) -> tuple[int, Any]:
        """Send an arbitrary POST request. Returns (status_code, parsed_body)."""
        resp = self._session.post(
            url,
            data=data,
            json=json_body,
            headers=headers or {},
            allow_redirects=allow_redirects,
        )
        try:
            body = resp.json()
        except Exception:
            body = resp.text
        return resp.status_code, body

    @property
    def session(self) -> "requests.Session":
        """The underlying requests session (shares TLS/verify settings)."""
        return self._session

    @property
    def discovery(self) -> dict[str, Any]:
        """The cached discovery document (fetched on first access)."""
        if not self._discovery:
            self.discover()
        return self._discovery  # type: ignore[return-value]

    def endpoint(self, name: str) -> str | None:
        """Return a discovery endpoint URL, or None if not advertised."""
        if not self._discovery:
            self.discover()
        return self._discovery.get(name)  # type: ignore[union-attr]
