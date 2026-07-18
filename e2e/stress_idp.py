#!/usr/bin/env python3
"""Threaded MrWhoOidc IdP stress and memory-leak probe.

The harness intentionally uses only the local Docker/dev stack and a dedicated
M2M client credential. It mixes successful token issuance with negative protocol
requests and public endpoint reads, while sampling Docker stats for memory trend
analysis.
"""

from __future__ import annotations

import argparse
import base64
import concurrent.futures
import json
import math
import os
import queue
import random
import statistics
import subprocess
import threading
import time
from collections import Counter, defaultdict, deque
from dataclasses import dataclass, field
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import requests
import urllib3
from requests.auth import HTTPBasicAuth

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)


@dataclass(slots=True)
class OperationStats:
    count: int = 0
    ok: int = 0
    errors: int = 0
    latencies_ms: list[float] = field(default_factory=list)
    statuses: Counter[int] = field(default_factory=Counter)
    exceptions: Counter[str] = field(default_factory=Counter)


class Aggregator:
    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._stats: dict[str, OperationStats] = defaultdict(OperationStats)

    def record(self, op: str, status: int | None, elapsed_ms: float, ok: bool, exc: BaseException | None = None) -> None:
        with self._lock:
            s = self._stats[op]
            s.count += 1
            s.latencies_ms.append(elapsed_ms)
            if status is not None:
                s.statuses[status] += 1
            if ok:
                s.ok += 1
            else:
                s.errors += 1
            if exc is not None:
                s.exceptions[type(exc).__name__] += 1

    def snapshot(self) -> dict[str, OperationStats]:
        with self._lock:
            copy: dict[str, OperationStats] = {}
            for name, stats in self._stats.items():
                copy[name] = OperationStats(
                    count=stats.count,
                    ok=stats.ok,
                    errors=stats.errors,
                    latencies_ms=list(stats.latencies_ms),
                    statuses=Counter(stats.statuses),
                    exceptions=Counter(stats.exceptions),
                )
            return copy


def percentile(values: list[float], pct: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    idx = min(len(ordered) - 1, max(0, math.ceil((pct / 100.0) * len(ordered)) - 1))
    return ordered[idx]


def parse_mem_to_mb(mem_usage: str) -> float | None:
    # Docker stats MemUsage looks like "512.3MiB / 7.63GiB".
    raw = mem_usage.split("/")[0].strip()
    if not raw:
        return None
    units = [
        ("TiB", 1024 * 1024),
        ("GiB", 1024),
        ("MiB", 1),
        ("KiB", 1 / 1024),
        ("GB", 1000 * 1000 * 1000 / (1024 * 1024)),
        ("MB", 1000 * 1000 / (1024 * 1024)),
        ("kB", 1000 / (1024 * 1024)),
        ("B", 1 / (1024 * 1024)),
    ]
    for unit, factor in units:
        if raw.endswith(unit):
            try:
                return float(raw[: -len(unit)]) * factor
            except ValueError:
                return None
    return None


def docker_stats(containers: list[str]) -> list[dict[str, Any]]:
    cmd = [
        "docker",
        "stats",
        "--no-stream",
        "--format",
        "{{json .}}",
        *containers,
    ]
    proc = subprocess.run(cmd, capture_output=True, text=True, timeout=20)
    if proc.returncode != 0:
        return [{"error": proc.stderr.strip() or proc.stdout.strip()}]
    rows = []
    for line in proc.stdout.splitlines():
        try:
            item = json.loads(line)
            item["mem_mb"] = parse_mem_to_mb(item.get("MemUsage", ""))
            rows.append(item)
        except json.JSONDecodeError:
            rows.append({"raw": line})
    return rows


def read_client(path: Path) -> tuple[str, str]:
    with path.open() as f:
        data = json.load(f)
    return data["clientId"], data["initialSecret"]


class StressClient:
    def __init__(self, base_url: str, client_id: str, client_secret: str, timeout: float) -> None:
        self.base = base_url.rstrip("/")
        self.client_id = client_id
        self.client_secret = client_secret
        self.timeout = timeout
        self.session = requests.Session()
        self.session.verify = False
        self.auth = HTTPBasicAuth(client_id, client_secret)
        self.token_pool: deque[str] = deque(maxlen=2048)

    def get(self, path: str, *, allow_redirects: bool = False) -> requests.Response:
        return self.session.get(self.base + path, timeout=self.timeout, allow_redirects=allow_redirects)

    def post(self, path: str, *, data: dict[str, str], auth: HTTPBasicAuth | None = None) -> requests.Response:
        return self.session.post(self.base + path, data=data, auth=auth, timeout=self.timeout)

    def token(self) -> requests.Response:
        r = self.post(
            "/token",
            auth=self.auth,
            data={
                "grant_type": "client_credentials",
                "scope": "openid profile",
                "audience": "api",
            },
        )
        if r.status_code == 200:
            try:
                token = r.json().get("access_token")
                if token:
                    self.token_pool.append(token)
            except ValueError:
                pass
        return r

    def pick_token(self) -> str | None:
        if not self.token_pool:
            self.token()
        if not self.token_pool:
            return None
        return random.choice(list(self.token_pool))


def run_operation(client: StressClient, op: str) -> tuple[int | None, bool]:
    expected = {
        "home": {200},
        "login_page": {200},
        "privacy": {200},
        "discovery": {200},
        "jwks": {200},
        "token_valid": {200},
        "token_invalid": {400, 401},
        "introspect_valid": {200},
        "introspect_invalid": {200, 400, 401},
        "revoke_valid": {200},
        "userinfo_invalid": {401},
        "authorize_invalid": {400, 302, 401},
        "par_invalid": {400, 401},
        "device_invalid": {400, 401},
        "ciba_invalid": {400, 401},
        "endsession_invalid": {200, 302, 400},
        "not_found": {404},
    }

    if op == "home":
        r = client.get("/")
    elif op == "login_page":
        r = client.get("/login")
    elif op == "privacy":
        r = client.get("/privacy")
    elif op == "discovery":
        r = client.get("/.well-known/openid-configuration")
        if r.status_code == 404:
            r = client.get("/t/default/.well-known/openid-configuration")
    elif op == "jwks":
        r = client.get("/jwks")
        if r.status_code == 404:
            r = client.get("/t/default/jwks")
    elif op == "token_valid":
        r = client.token()
    elif op == "token_invalid":
        r = client.post("/token", data={"grant_type": "client_credentials", "client_id": "missing", "client_secret": "bad"})
    elif op == "introspect_valid":
        token = client.pick_token() or "missing"
        r = client.post("/introspect", auth=client.auth, data={"token": token})
    elif op == "introspect_invalid":
        r = client.post("/introspect", data={"token": "not-a-token"})
    elif op == "revoke_valid":
        token = client.pick_token() or "missing"
        r = client.post("/revoke", auth=client.auth, data={"token": token, "token_type_hint": "access_token"})
    elif op == "userinfo_invalid":
        r = client.session.get(client.base + "/userinfo", headers={"Authorization": "Bearer not-a-token"}, timeout=client.timeout)
    elif op == "authorize_invalid":
        r = client.get("/authorize?client_id=missing&response_type=code&redirect_uri=https%3A%2F%2Fbad.example%2Fcb&scope=openid", allow_redirects=False)
    elif op == "par_invalid":
        r = client.post("/par", data={"client_id": "missing", "response_type": "code"})
    elif op == "device_invalid":
        r = client.post("/device/authorize", data={"client_id": "missing", "scope": "openid"})
    elif op == "ciba_invalid":
        r = client.post("/bc-authorize", data={"client_id": "missing", "scope": "openid", "login_hint": "none"})
    elif op == "endsession_invalid":
        r = client.get("/connect/endsession?id_token_hint=bad", allow_redirects=False)
    elif op == "not_found":
        r = client.get(f"/stress-missing-{random.randint(1, 1_000_000)}")
    else:
        raise ValueError(op)

    return r.status_code, r.status_code in expected[op] or r.status_code == 429


def worker(worker_id: int, args: argparse.Namespace, agg: Aggregator, stop_at: float, progress: queue.Queue[str]) -> None:
    client_id, client_secret = read_client(Path(args.client_file))
    client = StressClient(args.base_url, client_id, client_secret, args.timeout)
    ops = [
        ("token_valid", 26),
        ("discovery", 9),
        ("jwks", 9),
        ("introspect_valid", 10),
        ("revoke_valid", 5),
        ("token_invalid", 8),
        ("authorize_invalid", 7),
        ("userinfo_invalid", 5),
        ("par_invalid", 5),
        ("device_invalid", 4),
        ("ciba_invalid", 3),
        ("home", 3),
        ("login_page", 2),
        ("privacy", 1),
        ("endsession_invalid", 2),
        ("not_found", 1),
    ]
    names = [name for name, _ in ops]
    weights = [weight for _, weight in ops]
    local_count = 0
    while time.monotonic() < stop_at:
        op = random.choices(names, weights=weights, k=1)[0]
        start = time.perf_counter()
        status = None
        exc: BaseException | None = None
        ok = False
        try:
            status, ok = run_operation(client, op)
        except BaseException as e:  # keep load running while recording failures
            exc = e
        elapsed_ms = (time.perf_counter() - start) * 1000
        agg.record(op, status, elapsed_ms, ok, exc)
        local_count += 1
        if local_count % 250 == 0:
            progress.put(f"worker={worker_id} ops={local_count}")


def monitor(args: argparse.Namespace, stop_at: float, samples: list[dict[str, Any]], progress: queue.Queue[str]) -> None:
    containers = args.containers.split(",")
    while time.monotonic() < stop_at:
        ts = datetime.now(timezone.utc).isoformat()
        rows = docker_stats(containers)
        samples.append({"ts": ts, "rows": rows})
        interesting = []
        for row in rows:
            name = row.get("Name") or row.get("Container") or row.get("Name")
            mem = row.get("mem_mb")
            cpu = row.get("CPUPerc")
            if name and mem is not None:
                interesting.append(f"{name}:cpu={cpu},mem={mem:.1f}MiB")
        if interesting:
            progress.put("stats " + "; ".join(interesting))
        time.sleep(args.sample_interval)


def summarize(agg: Aggregator, samples: list[dict[str, Any]], duration_s: float) -> dict[str, Any]:
    stats = agg.snapshot()
    ops_summary = {}
    total = 0
    total_errors = 0
    for name, s in sorted(stats.items()):
        total += s.count
        total_errors += s.errors
        lat = s.latencies_ms
        ops_summary[name] = {
            "count": s.count,
            "ok": s.ok,
            "errors": s.errors,
            "rate_limited": s.statuses.get(429, 0),
            "error_rate_pct": round((s.errors / s.count * 100) if s.count else 0, 3),
            "rps": round(s.count / duration_s, 3),
            "p50_ms": round(percentile(lat, 50) or 0, 2),
            "p95_ms": round(percentile(lat, 95) or 0, 2),
            "p99_ms": round(percentile(lat, 99) or 0, 2),
            "max_ms": round(max(lat), 2) if lat else 0,
            "statuses": dict(s.statuses),
            "exceptions": dict(s.exceptions),
        }

    mem_summary: dict[str, Any] = {}
    series: dict[str, list[tuple[float, float]]] = defaultdict(list)
    if samples:
        t0 = datetime.fromisoformat(samples[0]["ts"]).timestamp()
        for sample in samples:
            t = datetime.fromisoformat(sample["ts"]).timestamp() - t0
            for row in sample.get("rows", []):
                name = row.get("Name") or row.get("Container")
                mem = row.get("mem_mb")
                if name and mem is not None:
                    series[name].append((t, mem))
    for name, points in sorted(series.items()):
        values = [m for _, m in points]
        slope_mb_per_hour = None
        if len(points) >= 2:
            xs = [p[0] for p in points]
            ys = [p[1] for p in points]
            xbar = statistics.mean(xs)
            ybar = statistics.mean(ys)
            denom = sum((x - xbar) ** 2 for x in xs)
            if denom > 0:
                slope_per_second = sum((x - xbar) * (y - ybar) for x, y in zip(xs, ys)) / denom
                slope_mb_per_hour = slope_per_second * 3600
        mem_summary[name] = {
            "samples": len(values),
            "start_mb": round(values[0], 2),
            "end_mb": round(values[-1], 2),
            "min_mb": round(min(values), 2),
            "max_mb": round(max(values), 2),
            "delta_mb": round(values[-1] - values[0], 2),
            "slope_mb_per_hour": round(slope_mb_per_hour, 2) if slope_mb_per_hour is not None else None,
        }

    return {
        "duration_s": round(duration_s, 2),
        "total_requests": total,
        "total_rps": round(total / duration_s, 3) if duration_s > 0 else 0,
        "total_errors": total_errors,
        "total_error_rate_pct": round((total_errors / total * 100) if total else 0, 3),
        "operations": ops_summary,
        "memory": mem_summary,
    }


def write_markdown(path: Path, summary: dict[str, Any]) -> None:
    lines = [
        "# MrWhoOidc Stress Test Report",
        "",
        f"- Duration: {summary['duration_s']} s",
        f"- Total requests: {summary['total_requests']}",
        f"- Total RPS: {summary['total_rps']}",
        f"- Total harness-level errors: {summary['total_errors']} ({summary['total_error_rate_pct']}%)",
        "",
        "## Operation Latency",
        "",
        "| Operation | Count | OK | Errors | 429s | RPS | p50 ms | p95 ms | p99 ms | Max ms | Statuses | Exceptions |",
        "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|---|",
    ]
    for name, op in summary["operations"].items():
        lines.append(
            f"| {name} | {op['count']} | {op['ok']} | {op['errors']} | {op['rate_limited']} | {op['rps']} | "
            f"{op['p50_ms']} | {op['p95_ms']} | {op['p99_ms']} | {op['max_ms']} | "
            f"{op['statuses']} | {op['exceptions']} |"
        )
    lines.extend([
        "",
        "## Docker Memory Trend",
        "",
        "| Container | Samples | Start MiB | End MiB | Delta MiB | Min MiB | Max MiB | Slope MiB/hour |",
        "|---|---:|---:|---:|---:|---:|---:|---:|",
    ])
    for name, mem in summary["memory"].items():
        lines.append(
            f"| {name} | {mem['samples']} | {mem['start_mb']} | {mem['end_mb']} | {mem['delta_mb']} | "
            f"{mem['min_mb']} | {mem['max_mb']} | {mem['slope_mb_per_hour']} |"
        )
    path.write_text("\n".join(lines) + "\n")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-url", default=os.getenv("BASE_URL", "https://localhost:8443/t/default"))
    parser.add_argument("--client-file", default="/tmp/mrwho-stress-client.json")
    parser.add_argument("--duration", type=int, default=1800)
    parser.add_argument("--workers", type=int, default=64)
    parser.add_argument("--timeout", type=float, default=8.0)
    parser.add_argument("--sample-interval", type=int, default=15)
    parser.add_argument("--containers", default="mrwhooidc-webauth-1,mrwhooidc-postgres-1,mrwhooidc-redis-1")
    parser.add_argument("--out-dir", default="reports/stress")
    args = parser.parse_args()

    out_dir = Path(args.out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)
    run_id = datetime.now(timezone.utc).strftime("%Y%m%d_%H%M%S")
    raw_path = out_dir / f"stress_{run_id}.json"
    md_path = out_dir / f"stress_{run_id}.md"

    _ = read_client(Path(args.client_file))

    start = time.monotonic()
    stop_at = start + args.duration
    agg = Aggregator()
    samples: list[dict[str, Any]] = []
    progress: queue.Queue[str] = queue.Queue()

    mon = threading.Thread(target=monitor, args=(args, stop_at, samples, progress), daemon=True)
    mon.start()
    print(f"Starting stress run: duration={args.duration}s workers={args.workers} base={args.base_url}", flush=True)

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        futures = [pool.submit(worker, i, args, agg, stop_at, progress) for i in range(args.workers)]
        next_status = time.monotonic() + 30
        while time.monotonic() < stop_at:
            try:
                msg = progress.get(timeout=1)
                if msg.startswith("stats"):
                    print(f"[{datetime.now(timezone.utc).isoformat()}] {msg}", flush=True)
            except queue.Empty:
                pass
            if time.monotonic() >= next_status:
                snap = agg.snapshot()
                total = sum(s.count for s in snap.values())
                errors = sum(s.errors for s in snap.values())
                elapsed = time.monotonic() - start
                print(f"progress elapsed={elapsed:.0f}s total={total} rps={total/elapsed:.1f} errors={errors}", flush=True)
                next_status += 30
        for f in concurrent.futures.as_completed(futures):
            f.result()

    duration_s = time.monotonic() - start
    summary = summarize(agg, samples, duration_s)
    raw_path.write_text(json.dumps({"summary": summary, "samples": samples}, indent=2))
    write_markdown(md_path, summary)
    print(f"Wrote {raw_path}")
    print(f"Wrote {md_path}")
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())