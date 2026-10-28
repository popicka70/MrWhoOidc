"""Unit tests for the destructive-reset safety interlock (review finding H3).

The E2E session fixture removes Docker volumes. These tests pin the interlock that decides whether that
is allowed: an explicit opt-in, the authorized Docker context, a local daemon and volumes that belong to
the expected compose project.

They are pure unit tests, so they are marked ``no_docker_reset`` and never touch Docker.
"""

from __future__ import annotations

import pytest

from utils.docker_safety import (
    ALLOW_RESET_ENV,
    DOCKER_CONTEXT_ENV,
    DestructiveResetRefused,
    authorize_reset,
    expected_context,
    is_local_endpoint,
    opt_in_present,
)

pytestmark = pytest.mark.no_docker_reset

_OWN_VOLUME = "mrwhooidc_postgres-data"


def _fake_docker(monkeypatch, *, context="default", endpoint="unix:///var/run/docker.sock", project="mrwhooidc"):
    """Point the interlock at a fake, verified local daemon."""
    monkeypatch.setattr("utils.docker_safety.current_context", lambda: context)
    monkeypatch.setattr("utils.docker_safety.context_endpoint", lambda ctx: endpoint)
    monkeypatch.setattr(
        "utils.docker_safety.inspect_volume",
        lambda volume: {"Labels": {"com.docker.compose.project": project}},
    )


# ── opt-in ───────────────────────────────────────────────────────────────────


def test_reset_is_refused_without_an_opt_in():
    with pytest.raises(DestructiveResetRefused) as exc:
        authorize_reset([_OWN_VOLUME], env={})
    assert ALLOW_RESET_ENV in str(exc.value)


@pytest.mark.parametrize("value", ["1", "true", "yes", "on", "TRUE"])
def test_opt_in_values(value):
    assert opt_in_present({ALLOW_RESET_ENV: value})


@pytest.mark.parametrize("value", ["0", "false", "", "no", None])
def test_non_opt_in_values(value):
    assert not opt_in_present({ALLOW_RESET_ENV: value} if value is not None else {})


def test_expected_context_default_and_override():
    assert expected_context({}) == "default"
    assert expected_context({DOCKER_CONTEXT_ENV: "staging"}) == "staging"


# ── docker target ────────────────────────────────────────────────────────────


def test_a_different_active_context_is_refused(monkeypatch):
    _fake_docker(monkeypatch, context="remote-build")
    with pytest.raises(DestructiveResetRefused) as exc:
        authorize_reset([_OWN_VOLUME], env={ALLOW_RESET_ENV: "1", DOCKER_CONTEXT_ENV: "default"})
    assert "remote-build" in str(exc.value)


def test_a_remote_daemon_is_refused(monkeypatch):
    _fake_docker(monkeypatch, endpoint="tcp://10.0.0.5:2375")
    with pytest.raises(DestructiveResetRefused) as exc:
        authorize_reset([_OWN_VOLUME], env={ALLOW_RESET_ENV: "1"})
    assert "not a local daemon" in str(exc.value)


@pytest.mark.parametrize(
    "endpoint,is_local",
    [
        ("unix:///var/run/docker.sock", True),
        ("npipe:////./pipe/docker_engine", True),
        ("tcp://127.0.0.1:2375", True),
        ("tcp://localhost:2375", True),
        ("tcp://10.0.0.5:2375", False),
        ("ssh://user@build.example.com", False),
        ("", False),
    ],
)
def test_local_endpoint_detection(endpoint, is_local):
    assert is_local_endpoint(endpoint) is is_local


# ── volumes ──────────────────────────────────────────────────────────────────


def test_a_volume_of_another_project_is_refused(monkeypatch):
    _fake_docker(monkeypatch, project="somebody-else")
    with pytest.raises(DestructiveResetRefused) as exc:
        authorize_reset([_OWN_VOLUME], env={ALLOW_RESET_ENV: "1"})
    assert "somebody-else" in str(exc.value)


def test_a_volume_without_a_project_label_is_refused(monkeypatch):
    _fake_docker(monkeypatch)
    monkeypatch.setattr("utils.docker_safety.inspect_volume", lambda volume: {"Labels": {}})
    with pytest.raises(DestructiveResetRefused) as exc:
        authorize_reset([_OWN_VOLUME], env={ALLOW_RESET_ENV: "1"})
    assert "compose project" in str(exc.value)


def test_our_own_volume_is_authorized(monkeypatch):
    _fake_docker(monkeypatch)
    authorize_reset([_OWN_VOLUME], env={ALLOW_RESET_ENV: "1"})


def test_a_missing_volume_is_not_a_problem(monkeypatch):
    _fake_docker(monkeypatch)
    monkeypatch.setattr("utils.docker_safety.inspect_volume", lambda volume: None)
    authorize_reset([_OWN_VOLUME], env={ALLOW_RESET_ENV: "1"})
