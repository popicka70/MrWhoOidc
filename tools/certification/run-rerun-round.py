#!/usr/bin/env python3
"""Drive one OIDC re-run round (waiver bundle, all profiles at commit bf7e789b).

Recreates the lost /tmp/opencode/run-public-round.py driver: creates a fresh plan
for a profile using the prepared re-run runner config, creates every module from
the Nov 13 module matrix (exact variants), starts it, waits with per-module
timeout tolerance, and records results against the Nov 13 expected outcomes.

Module variants are passed verbatim from the stored plan JSON (suite quirk: only
the plan's stored keys are accepted). INTERRUPTED modules (OP-side OOM) are
retried by creating a fresh instance in the same plan. Suite restarts are
tolerated via ServerUnavailableError retries.

Usage:
  python3 run-rerun-round.py <config|basic|formpost|rp-logout|frontchannel-logout|backchannel-logout> \
      [--resume-plan-id <id>] [--max-module-retries 3] [--module-timeout 300]
"""

import argparse
import asyncio
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from conformance import Conformance, ServerUnavailableError  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
GEN = os.path.join(REPO, 'tools', 'certification', '.generated')
EXPORTS = os.path.join(GEN, 'exports')
TOKEN_FILE = os.path.join(REPO, 'tools', 'certification', 'conformance-token.txt')

SUITE_BASE = 'https://www.certification.openid.net/'

# profile slug -> (planName, plan-level variant, re-run runner config file, matrix key)
PROFILES = {
    'config': {
        'plan_name': 'oidcc-config-certification-test-plan',
        'plan_variant': {},
        'config': 'runner-config-op-rerun.json',
        'spec': 'config',
    },
    'basic': {
        'plan_name': 'oidcc-basic-certification-test-plan',
        'plan_variant': {"server_metadata": "discovery", "client_registration": "dynamic_client"},
        'config': 'runner-basic-rerun.json',
        'spec': 'basic',
    },
    'formpost': {
        'plan_name': 'oidcc-formpost-basic-certification-test-plan',
        'plan_variant': {"server_metadata": "discovery", "client_registration": "dynamic_client"},
        'config': 'runner-formpost-rerun.json',
        'spec': 'formpost',
    },
    'rp-logout': {
        'plan_name': 'oidcc-rp-initiated-logout-certification-test-plan',
        'plan_variant': {"response_type": "code", "client_registration": "dynamic_client"},
        'config': 'runner-rp-initiated-logout-rerun.json',
        'spec': 'rp-logout',
    },
    'frontchannel-logout': {
        'plan_name': 'oidcc-frontchannel-rp-initiated-logout-certification-test-plan',
        'plan_variant': {"response_type": "code", "client_registration": "dynamic_client"},
        'config': 'runner-frontchannel-logout-rerun.json',
        'spec': 'fc-logout',
    },
    'backchannel-logout': {
        'plan_name': 'oidcc-backchannel-rp-initiated-logout-certification-test-plan',
        'plan_variant': {"response_type": "code", "client_registration": "dynamic_client"},
        'config': 'runner-backchannel-logout-rerun.json',
        'spec': 'bc-logout',
    },
}


async def wait_for_op_health(op_base_url, timeout=120):
    """Wait until the OP /health/ready responds 200 before driving the next module."""
    deadline = time.time() + timeout
    url = op_base_url.rstrip('/') + '/health/ready'
    import urllib.request
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=10) as resp:
                if resp.status == 200:
                    return True
        except Exception:
            pass
        time.sleep(5)
    print('  OP health check timed out — proceeding anyway')
    return False


async def start_and_wait(conformance, module_id, module_timeout):
    """Start a module and wait for a terminal state, tolerating suite restarts."""
    deadline = time.time() + 120
    while True:
        try:
            await conformance.start_test(module_id)
            break
        except ServerUnavailableError as e:
            if time.time() >= deadline:
                raise
            print(f'  start_test retryable failure ({e}); backing off 15s')
            await asyncio.sleep(15)
    # WAITING is included so long-poll resolves when a module waits on browser work,
    # but we keep waiting until a real terminal state (the loop below re-waits).
    required = ['FINISHED', 'INTERRUPTED']
    state = None
    while True:
        state = await conformance.wait_for_state(module_id, required, timeout=module_timeout)
        if state in ('FINISHED', 'INTERRUPTED'):
            return state
        # WAITING/other transient state observed mid-flight; keep waiting.
        info = await conformance.get_module_info(module_id)
        if info.get('status') in ('FINISHED', 'INTERRUPTED'):
            return info['status']


async def run_module(conformance, plan_id, name, variant, expected, module_timeout, max_retries,
                    op_base_url):
    attempts = 0
    while attempts <= max_retries:
        attempts += 1
        if not await wait_for_op_health(op_base_url):
            pass
        try:
            info = await conformance.create_test_from_plan_with_variant(plan_id, name, variant)
        except Exception as e:
            # Creation failure (bad variant, suite 5xx after retries): mark and move on.
            print(f'  [{attempts}] create {name} failed: {e}')
            return {'module': name, 'id': None, 'state': 'FAILED', 'result': None,
                    'expected': expected, 'note': f'create failed: {e}'}
        module_id = info['id']
        print(f'  [{attempts}] created {name} -> {module_id}')
        try:
            state = await start_and_wait(conformance, module_id, module_timeout)
        except Exception as e:
            print(f'  [{attempts}] {name} error: {e}')
            state = 'ERROR'
        if state == 'ERROR':
            # Suite-side error (restart lost the test, etc.) — retry with fresh module.
            continue
        detail = await conformance.get_module_info(module_id)
        result = detail.get('result')
        print(f'  [{attempts}] {name}: {state} / result={result} (expected={expected})')
        if state == 'INTERRUPTED':
            print(f'  [{attempts}] INTERRUPTED (OP-side interruption, likely OOM/restart window) - backing off 75s')
            await asyncio.sleep(75)
            continue
        return {'module': name, 'id': module_id, 'state': state, 'result': result, 'expected': expected}
    return {'module': name, 'id': None, 'state': 'FAILED', 'result': None, 'expected': expected,
            'note': f'gave up after {attempts} attempts'}


async def get_finished_modules(conformance, plan_id):
    """Return the set of testModule names already FINISHED in the plan (any result)."""
    resp = await conformance.httpclient.get(f'{SUITE_BASE}api/plan/{plan_id}')
    if resp.status_code != 200:
        print(f'plan doc fetch failed (HTTP {resp.status_code}) — cannot skip finished modules')
        return set()
    doc = resp.json()
    return {m['testModule'] for m in doc.get('modules', []) if m.get('status') == 'FINISHED'}


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('profile', choices=sorted(PROFILES.keys()))
    ap.add_argument('--resume-plan-id')
    ap.add_argument('--max-module-retries', type=int, default=3)
    ap.add_argument('--module-timeout', type=int, default=300)
    ap.add_argument('--pace', type=int, default=45,
                    help='Seconds to idle between module starts (lets the OP GC settle).')
    ap.add_argument('--include-finished', action='store_true',
                    help='Re-run modules that already finished in the plan (default: skip them).')
    ap.add_argument('--only-module', action='append', default=[],
                    help='Restrict this session to the named module(s). Repeatable.')
    ap.add_argument('--op-base-url', default='https://mrwho.onrender.com')
    ap.add_argument('--plan-only', action='store_true',
                    help='Create the plan and exit immediately. The hosted suite runs plan '
                         'modules on its own (auto-runner); use this to avoid double-driving '
                         'and alias conflicts, then repair leftovers with a later resume run.')
    args = ap.parse_args()

    prof = PROFILES[args.profile]
    spec = json.load(open(os.path.join(GEN, 'rerun-module-matrices.json')))[prof['spec']]
    config_path = os.path.join(GEN, prof['config'])
    configuration = open(config_path).read()
    token = open(TOKEN_FILE).read().strip()
    conformance = Conformance(SUITE_BASE, token, False)

    if args.resume_plan_id:
        plan_id = args.resume_plan_id
        print(f'Resuming plan {plan_id} for profile {args.profile}')
        finished = set() if args.include_finished else await get_finished_modules(conformance, plan_id)
        if finished:
            print(f'Skipping {len(finished)} already-FINISHED modules: {sorted(finished)}')
    else:
        print(f'Creating plan {prof["plan_name"]} (variant {json.dumps(prof["plan_variant"])})')
        plan = await conformance.create_test_plan(prof['plan_name'], configuration, prof['plan_variant'])
        plan_id = plan.get('id') or plan['_id']
        print(f'Plan created: {plan_id}')
        finished = set()

    if args.plan_only:
        print(f'plan-only: plan {plan_id} created for the suite auto-runner; exiting.')
        print(f'plan URL: {SUITE_BASE}plan-detail.html?plan={plan_id}')
        await conformance.close_client()
        return 0

    started_at = time.time()
    results = []
    for m in spec['modules']:
        if args.only_module and m['module'] not in args.only_module:
            continue
        if m['module'] in finished:
            print(f'Module {m["module"]}: already FINISHED — skipping')
            results.append({'module': m['module'], 'state': 'FINISHED', 'result': 'SKIP-ALREADY-DONE',
                            'expected': m.get('result')})
            continue
        print(f'Module {m["module"]} (variant {json.dumps(m["variant"])})')
        r = await run_module(conformance, plan_id, m['module'], m['variant'],
                             m.get('result'), args.module_timeout, args.max_module_retries,
                             args.op_base_url)
        results.append(r)
        if args.pace > 0:
            await asyncio.sleep(args.pace)

    # Drift report vs Nov 13 expected results (SKIP-ALREADY-DONE entries are ignored)
    print('\n=== ROUND SUMMARY ===')
    drift = []
    counts = {}
    skipped_already = 0
    for r in results:
        if r['result'] == 'SKIP-ALREADY-DONE':
            skipped_already += 1
            continue
        counts[r['result'] or 'NONE'] = counts.get(r['result'] or 'NONE', 0) + 1
        if r['result'] != r['expected']:
            drift.append(f"  DRIFT {r['module']}: got {r['result']}, expected {r['expected']}")
    print(f'plan_id: {plan_id}')
    print(f'profile: {args.profile} ({prof["plan_name"]})')
    print(f'module result counts (this session): {json.dumps(counts)}')
    print(f'modules skipped (already finished earlier): {skipped_already}')
    print(f'duration: {time.time() - started_at:.0f}s')
    if drift:
        print('result drift vs Nov 13:')
        print('\n'.join(drift))
    else:
        print('no drift vs Nov 13 expected results')

    # Save the round log next to the exports
    os.makedirs(EXPORTS, exist_ok=True)
    log_path = os.path.join(EXPORTS, f'{args.profile}-rerun-round-{plan_id}.json')
    with open(log_path, 'w') as f:
        json.dump({'profile': args.profile, 'planName': prof['plan_name'], 'planId': plan_id,
                   'startedAt': started_at, 'durationSec': time.time() - started_at,
                   'results': results, 'counts': counts}, f, indent=1)
    print(f'round log: {log_path}')
    print(f'plan URL: {SUITE_BASE}plan-detail.html?plan={plan_id}')

    await conformance.close_client()
    # Non-zero exit if anything did not reach FINISHED with a recorded result
    bad = [r for r in results if r['state'] != 'FINISHED' or not r['result']]
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(asyncio.run(main()))
