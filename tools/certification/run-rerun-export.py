#!/usr/bin/env python3
"""Export plan JSON + create the certification package for finished re-run plans.

Usage:
  python3 run-rerun-export.py <plan-id> [<plan-id> ...]

For each plan:
  1. fetch the plan JSON document (GET /api/plan/<id>) -> exports/<slug>-rerun-plan-<planId>.json
  2. exportjson  -> exports/<planName>-plan-export-<planId>.zip (renamed from the
     suite-supplied filename)
  3. create_certification_package -> publishes + marks immutable, writes
     exports/<planName>-certification-package-<planId>.zip

Package creation is irreversible (publish + immutable) — only run this once a
round's log has been reviewed.
"""

import argparse
import asyncio
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from conformance import Conformance  # noqa: E402

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXPORTS = os.path.join(REPO, 'tools', 'certification', '.generated', 'exports')
TOKEN_FILE = os.path.join(REPO, 'tools', 'certification', 'conformance-token.txt')
SUITE_BASE = 'https://www.certification.openid.net/'


async def fetch_plan_document(http, plan_id):
    resp = await http.get(f'{SUITE_BASE}api/plan/{plan_id}')
    if resp.status_code != 200:
        raise Exception(f'plan document fetch failed: HTTP {resp.status_code} {resp.content[:200]}')
    return resp.json()


async def export_one(conformance, plan_id):
    doc = await fetch_plan_document(conformance.httpclient, plan_id)
    profile = (doc.get('certificationProfileName') or ['unknown'])[0]
    plan_name = doc.get('planName', 'unknown-plan')
    counts = {}
    for m in doc.get('modules', []):
        counts[m.get('result') or 'NONE'] = counts.get(m.get('result') or 'NONE', 0) + 1
    print(f'plan {plan_id}: {plan_name} ({profile}) modules={len(doc.get("modules", []))} counts={json.dumps(counts)}')
    if doc.get('immutable'):
        print('  already immutable (published) — skipping package creation')
        return doc, counts

    # 1. plan JSON document (raw, for the exports archive)
    json_path = os.path.join(EXPORTS, f'{slugify(plan_name)}-rerun-plan-{plan_id}.json')
    with open(json_path, 'w') as f:
        json.dump(doc, f, indent=1, sort_keys=True)
    print(f'  plan json: {os.path.basename(json_path)}')

    # 2. exportjson zip (the suite names it <planName>-export.json.zip)
    zip_path = await conformance.exportjson(plan_id, EXPORTS)
    renamed = os.path.join(EXPORTS, f'{plan_name}-plan-export-{plan_id}.zip')
    os.replace(zip_path, renamed)
    print(f'  plan export: {os.path.basename(renamed)}')

    # 3. certification package (publish + immutable)
    await conformance.create_certification_package(plan_id, output_zip_directory=EXPORTS)
    pkg_src = os.path.join(EXPORTS, f'{plan_name}-export.json.zip')
    if os.path.exists(pkg_src):
        pkg_dst = os.path.join(EXPORTS, f'{plan_name}-certification-package-{plan_id}.zip')
        os.replace(pkg_src, pkg_dst)
        print(f'  certification package: {os.path.basename(pkg_dst)}')
    else:
        cands = sorted(
            [f for f in os.listdir(EXPORTS) if f.endswith('.zip')],
            key=lambda f: os.path.getmtime(os.path.join(EXPORTS, f)), reverse=True)
        print(f'  NOTE: package file not at expected name; newest zips: {cands[:3]}')
    return doc, counts


def slugify(name):
    return re.sub(r'[^a-z0-9]+', '-', name.lower()).strip('-')


async def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('plan_ids', nargs='+')
    args = ap.parse_args()
    token = open(TOKEN_FILE).read().strip()
    conformance = Conformance(SUITE_BASE, token, False)
    summary = {}
    for pid in args.plan_ids:
        try:
            doc, counts = await export_one(conformance, pid)
            summary[pid] = {'planName': doc.get('planName'), 'counts': counts,
                            'immutable': bool(doc.get('immutable')),
                            'publish': doc.get('publish')}
        except Exception as e:
            print(f'plan {pid}: ERROR {e}')
            summary[pid] = {'error': str(e)}
    print('\n=== EXPORT SUMMARY ===')
    print(json.dumps(summary, indent=1))
    path = os.path.join(EXPORTS, f'rerun-export-summary-{time.strftime("%Y%m%d-%H%M")}.json')
    with open(path, 'w') as f:
        json.dump(summary, f, indent=1)
    print(f'summary: {path}')
    await conformance.close_client()


if __name__ == '__main__':
    asyncio.run(main())
