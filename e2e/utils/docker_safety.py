"""Safety interlock for the destructive E2E environment reset (review finding H3).

The session fixture in ``conftest.py`` drops the postgres/redis containers and DELETES their Docker
volumes before the suite runs. ``BASE_URL`` only selects where the tests send their HTTP requests; it says
nothing about which Docker daemon the volumes are removed from, so a local or remote Docker context with a
similarly named stack could lose data.

This module makes the reset an explicit, verified operation:

* the operator must opt in (``E2E_ALLOW_DESTRUCTIVE_RESET=1``),
* the active Docker context must be the one the operator authorized (``E2E_DOCKER_CONTEXT``),
* that context must point at a LOCAL daemon, and
* every volume that is about to be removed must belong to the expected compose project.

A loopback ``BASE_URL`` check alone is not a safety guarantee, which is why the Docker target itself is
verified here.
"""

from __future__ import annotations

import json
import os
import subprocess
from typing import Any, Mapping, Sequence

#: Explicit opt-in. The reset removes Docker volumes, so it must never happen by accident.
ALLOW_RESET_ENV = "E2E_ALLOW_DESTRUCTIVE_RESET"

#: The Docker context the reset is allowed to touch. "default" is the local daemon.
DOCKER_CONTEXT_ENV = "E2E_DOCKER_CONTEXT"
DEFAULT_DOCKER_CONTEXT = "default"

#: The compose project whose volumes the suite owns.
COMPOSE_PROJECT = "mrwhooidc"

_TRUTHY = {"1", "true", "yes", "on"}


class DestructiveResetRefused(RuntimeError):
    """Raised when the environment reset is not explicitly authorized for the current target."""


def _is_truthy(value: str | None) -> bool:
    return (value or "").strip().lower() in _TRUTHY


def opt_in_present(env: Mapping[str, str] | None = None) -> bool:
    """True when the operator explicitly allowed the destructive reset."""
    source = os.environ if env is None else env
    return _is_truthy(source.get(ALLOW_RESET_ENV))


def expected_context(env: Mapping[str, str] | None = None) -> str:
    """The Docker context the reset is allowed to use."""
    source = os.environ if env is None else env
    return (source.get(DOCKER_CONTEXT_ENV) or DEFAULT_DOCKER_CONTEXT).strip()


def _run(args: Sequence[str]) -> subprocess.CompletedProcess[str]:
    return subprocess.run(list(args), capture_output=True, text=True)


def current_context() -> str:
    """The name of the active Docker context."""
    result = _run(["docker", "context", "show"])
    if result.returncode != 0:
        raise DestructiveResetRefused(
            "Could not determine the active Docker context ('docker context show' failed). "
            "The reset refuses to guess which daemon it would delete volumes from."
        )
    return result.stdout.strip()


def context_endpoint(context: str) -> str:
    """The daemon endpoint of a Docker context, e.g. unix:///var/run/docker.sock."""
    result = _run(
        ["docker", "context", "inspect", context, "--format", "{{.Endpoints.docker.Host}}"]
    )
    if result.returncode != 0:
        raise DestructiveResetRefused(
            f"Could not inspect the Docker context '{context}'."
        )
    return result.stdout.strip()


def is_local_endpoint(endpoint: str) -> bool:
    """True when the endpoint addresses the local daemon (unix socket, named pipe or loopback host)."""
    if not endpoint:
        return False

    lowered = endpoint.strip().lower()
    if lowered.startswith(("unix://", "npipe://")):
        return True

    authority = lowered.split("://", 1)[-1]
    # strip credentials and the port
    if "@" in authority:
        authority = authority.rsplit("@", 1)[-1]
    if authority.startswith("["):
        host = authority[1 : authority.find("]")] if "]" in authority else authority[1:]
    else:
        host = authority.split(":", 1)[0]

    return host in {"localhost", "127.0.0.1", "::1"}


def inspect_volume(volume: str) -> dict[str, Any] | None:
    """Return the volume's metadata, or None when the volume does not exist."""
    result = _run(["docker", "volume", "inspect", volume])
    if result.returncode != 0:
        return None
    try:
        data = json.loads(result.stdout)
    except json.JSONDecodeError:
        return None
    if not isinstance(data, list) or not data:
        return None
    first = data[0]
    return first if isinstance(first, dict) else None


def volume_project(volume: str) -> str | None:
    """The compose project a volume belongs to, or None when it has no such label."""
    info = inspect_volume(volume)
    if info is None:
        return None
    labels = info.get("Labels") or {}
    if not isinstance(labels, dict):
        return None
    project = labels.get("com.docker.compose.project")
    return str(project) if project else None


def authorize_reset(
    volumes: Sequence[str],
    env: Mapping[str, str] | None = None,
    expected_project: str = COMPOSE_PROJECT,
) -> None:
    """Verify that the destructive reset is authorized for the current Docker target.

    Raises :class:`DestructiveResetRefused` when anything about the target is not verified.
    """
    if not opt_in_present(env):
        raise DestructiveResetRefused(
            "The E2E session fixture DELETES Docker volumes, so it requires an explicit opt-in. "
            f"Set {ALLOW_RESET_ENV}=1 to allow it (and set {DOCKER_CONTEXT_ENV} to the Docker context "
            "that the reset may use)."
        )

    wanted = expected_context(env)
    active = current_context()
    if active != wanted:
        raise DestructiveResetRefused(
            f"The active Docker context is '{active}' but the reset is only authorized for '{wanted}'. "
            f"Set {DOCKER_CONTEXT_ENV} to the context you want the reset to use, or switch with "
            f"'docker context use {wanted}'."
        )

    endpoint = context_endpoint(active)
    if not is_local_endpoint(endpoint):
        raise DestructiveResetRefused(
            f"The Docker context '{active}' points at '{endpoint}', which is not a local daemon. "
            "The reset refuses to delete volumes on a remote daemon."
        )

    for volume in volumes:
        info = inspect_volume(volume)
        if info is None:
            # The volume does not exist yet: nothing to delete.
            continue

        project = volume_project(volume)
        if project != expected_project:
            raise DestructiveResetRefused(
                f"The volume '{volume}' exists but does not belong to the compose project "
                f"'{expected_project}' (found: {project or 'no compose project label'}). "
                "The reset refuses to delete it."
            )
