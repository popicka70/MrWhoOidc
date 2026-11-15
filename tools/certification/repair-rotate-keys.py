#!/usr/bin/env python3
"""Repair the oidcc-server-rotate-keys module in a hosted conformance plan.

The official runner auto-starts this module immediately, so it always fails unless a rotation
happens to occur during the fetch window. This script creates a fresh instance of the module in
the SAME plan, waits for it to fetch the original JWKS (CONFIGURED state), triggers the OP's
on-demand signing-key rotation, then starts the test and waits for the result.

Usage:
  python3 repair-rotate-keys.py <plan-id> [--op-tenant default]
"""

import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
import asyncio
from conformance import Conformance

REPO = '/home/pavel/GitHub/MrWhoProjects/MrWhoOidc'
TOKEN_FILE = os.path.join(REPO, 'tools/certification/conformance-token.txt')
ROTATION_TOKEN_FILE = os.path.join(REPO, 'tools/certification/.generated/bootstrap-rotation-token.txt')


def trigger_rotation(op_public_base_url, tenant_slug):
    token = open(ROTATION_TOKEN_FILE).read().strip()
    url = op_public_base_url.rstrip('/') + '/api/bootstrap/rotate-signing-keys'
    body = json.dumps({'tenantSlug': tenant_slug}).encode()
    req = urllib.request.Request(
        url, data=body, method='POST',
        headers={'X-Bootstrap-Token': token, 'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.load(resp)


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('plan_id')
    ap.add_argument('--op-base-url', default='https://within-bacteria-checkout-bottles.trycloudflare.com')
    ap.add_argument('--op-tenant', default='default')
    args = ap.parse_args()

    token = open(TOKEN_FILE).read().strip()
    conformance = Conformance('https://www.certification.openid.net/', token, False)

    print(f'Creating oidcc-server-rotate-keys in plan {args.plan_id} ...')
    info = await conformance.create_test_from_plan_with_variant(
        args.plan_id, 'oidcc-server-rotate-keys', {'server_metadata': 'discovery'})
    module_id = info['id']
    print(f'Created test module {module_id}')
    print(f'https://www.certification.openid.net/log-detail.html?log={module_id}')

    state = await conformance.wait_for_state(module_id, ['CONFIGURED', 'WAITING', 'FINISHED'])
    if state != 'CONFIGURED':
        print(f'Unexpected state before rotation: {state} (expected CONFIGURED).')
        return 1
    print('Module CONFIGURED (original JWKS fetched). Triggering OP signing-key rotation ...')

    result = trigger_rotation(args.op_base_url, args.op_tenant)
    print('Rotation result:', json.dumps(result, indent=2))

    print('Starting test ...')
    await conformance.start_test(module_id)
    state = await conformance.wait_for_state(module_id, ['FINISHED', 'INTERRUPTED'], timeout=120)

    detail = await conformance.get_module_info(module_id)
    print(f'Final state: {state}')
    print(f'Result: {detail.get("result")}')
    return 0 if state == 'FINISHED' else 1


if __name__ == '__main__':
    sys.exit(asyncio.run(main()))
