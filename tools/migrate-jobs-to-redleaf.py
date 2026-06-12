"""One-time backfill: redcompute.db Jobs -> RedLeaf compute-job entities.

Jobs from the last 90 days upsert by slug (safe to re-run). Live jobs are
mirrored by RelayServer via JobCreated/JobUpdated from then on.

Usage: python migrate-jobs-to-redleaf.py [--dry-run] [--days N]
"""

import json
import os
import sqlite3
import sys
import time
import urllib.request
from datetime import datetime, timedelta, timezone

REDLEAF = "http://localhost:18804"
DB = os.path.join(os.environ["LOCALAPPDATA"], "RedCompute", "redcompute.db")
DRY_RUN = "--dry-run" in sys.argv
DAYS = int(sys.argv[sys.argv.index("--days") + 1]) if "--days" in sys.argv else 90


def api(method, path, body=None):
    data = json.dumps(body).encode() if body is not None else None
    for attempt in range(6):
        req = urllib.request.Request(
            REDLEAF + path, data=data,
            headers={"Content-Type": "application/json"}, method=method,
        )
        try:
            with urllib.request.urlopen(req, timeout=60) as resp:
                return resp.status, json.loads(resp.read() or b"null")
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read() or b"null")
        except (urllib.error.URLError, TimeoutError, ConnectionError) as e:
            if attempt == 5:
                raise
            wait = 5 * (attempt + 1)
            print(f"  connection failed ({e}); retrying in {wait}s...")
            time.sleep(wait)


def ensure_type():
    status, _ = api("GET", "/api/entity-types/compute-job")
    if status == 404:
        status, body = api("POST", "/api/entity-types", {
            "name": "Compute Job",
            "description": "RedCompute job lifecycle record",
            "icon": "fa-solid fa-microchip",
            "color": "pink",
            "versioning": False,
        })
        assert status in (200, 201), f"create compute-job type failed: {status} {body}"
        print("created entity type compute-job")


def main():
    conn = sqlite3.connect(DB)
    conn.row_factory = sqlite3.Row
    cutoff = (datetime.now(timezone.utc) - timedelta(days=DAYS)).strftime("%Y-%m-%d")
    jobs = conn.execute("SELECT * FROM Jobs WHERE QueuedAt >= ?", (cutoff,)).fetchall()
    print(f"{len(jobs)} job(s) within {DAYS}d")

    ok, failed = 0, 0
    for j in jobs:
        slug = f"compute-job-{j['Id'].replace('-', '').lower()}"
        duration = None
        data = {
            "job_id": j["Id"],
            "capability": j["CapabilitySlug"],
            "provider": j["ProviderName"],
            "status": j["Status"],
            "queued_at": j["QueuedAt"],
            "started_at": j["StartedAt"],
            "completed_at": j["CompletedAt"],
            "input_json": j["InputJson"],
            "output_location": j["OutputLocation"],
            "output_size_bytes": j["OutputSizeBytes"],
            "output_content_type": j["OutputContentType"],
            "result_json": j["ResultJson"],
            "error_message": j["ErrorMessage"],
            "caller_info": j["CallerInfo"],
            "rationale": j["Rationale"],
            "cost_usd": j["CostUsd"],
            "user_id": j["UserId"],
            "user_name": j["UserName"],
            "app": "redcompute",
        }
        name = j["Name"] or f"{j['CapabilitySlug']} ({j['ProviderName']})"
        if DRY_RUN:
            ok += 1
            continue
        status, body = api("PUT", f"/api/entities/by-slug/{slug}",
                           {"name": name, "type_slug": "compute-job", "data": data})
        if status in (200, 201):
            ok += 1
        else:
            failed += 1
            print(f"  job {j['Id']} failed: {status} {body}")

    print(f"jobs upserted: {ok}, failed: {failed}")
    conn.close()


if __name__ == "__main__":
    if not DRY_RUN:
        ensure_type()
    main()
    print("done" + (" (dry run)" if DRY_RUN else ""))
