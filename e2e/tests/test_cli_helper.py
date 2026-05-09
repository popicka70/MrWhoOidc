from __future__ import annotations

import subprocess

from utils.cli_helper import CliHelper


def _make_helper() -> CliHelper:
    helper = object.__new__(CliHelper)
    helper.server_url = "https://localhost:8443"
    helper.timeout = 60
    helper._last_call = 0.0
    helper._cli_bin = "/usr/bin/mrwho-cli"
    helper._throttle = lambda: None
    return helper


def test_run_does_not_retry_on_non_rate_limit_429_substring(monkeypatch):
    helper = _make_helper()
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return subprocess.CompletedProcess(
            cmd,
            0,
            stdout="Client created successfully. e2e-cli-sec-123429\n",
            stderr="",
        )

    monkeypatch.setattr(subprocess, "run", fake_run)

    result = helper.run("client", "create")

    assert result.ok
    assert len(calls) == 1


def test_run_retries_on_explicit_rate_limit_signal(monkeypatch):
    helper = _make_helper()
    calls: list[list[str]] = []
    responses = iter(
        [
            subprocess.CompletedProcess(
                ["mrwho-cli", "client", "list"],
                1,
                stdout="",
                stderr="Error: Too Many Requests\n",
            ),
            subprocess.CompletedProcess(
                ["mrwho-cli", "client", "list"],
                0,
                stdout="[]\n",
                stderr="",
            ),
        ]
    )

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return next(responses)

    monkeypatch.setattr(subprocess, "run", fake_run)
    monkeypatch.setattr("utils.cli_helper.time.sleep", lambda *_args, **_kwargs: None)

    result = helper.run("client", "list")

    assert result.ok
    assert len(calls) == 2