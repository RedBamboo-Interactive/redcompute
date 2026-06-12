"""Reconcile session-messages records in RedLeaf against the plugin SQLite DBs.

Compares per-(session, timestamp) multisets and repairs drift:
- records in RedLeaf beyond the source count are deleted (highest Ids first —
  those are migration re-imports of live-mirrored messages),
- source rows missing from RedLeaf are re-imported with original timestamps.

Messages newer than 10 minutes are ignored (the live mirror is still
shipping them). Postgres access goes through `docker exec cms-postgres-1`.

Usage: python reconcile-session-messages.py [--dry-run]
"""

import json
import os
import re
import sqlite3
import subprocess
import sys
import urllib.request
from collections import Counter
from datetime import datetime, timedelta, timezone

REDLEAF = "http://127.0.0.1:18804"
CUTOFF_DAYS = 180          # matches the original migration window
RECENT_GRACE_MIN = 10      # ignore messages still in flight via live mirror
PG = ["docker", "exec", "cms-postgres-1", "psql", "-U", "redleaf", "-d", "redleaf", "-t", "-A", "-F", "\t"]

PLUGINS = os.path.join(os.environ["LOCALAPPDATA"], "RedCompute", "plugins")
PROVIDERS = [
    ("claude-code", os.path.join(PLUGINS, "claude-code", "claude.db")),
    ("opencode", os.path.join(PLUGINS, "opencode", "opencode.db")),
    ("codex", os.path.join(PLUGINS, "codex", "codex.db")),
]

DRY_RUN = "--dry-run" in sys.argv
_opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def psql(query):
    out = subprocess.run(PG + ["-c", query], capture_output=True, text=True, check=True)
    return [line.split("\t") for line in out.stdout.splitlines() if line.strip()]


def api(method, path, body=None):
    req = urllib.request.Request(
        REDLEAF + path,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Content-Type": "application/json"}, method=method)
    with _opener.open(req, timeout=60) as resp:
        return json.loads(resp.read() or b"null")


def session_entity_id(provider, session_id):
    slug = f"ai-session-{provider}-" + re.sub(r"[^a-z0-9]", "-", session_id.lower())
    try:
        return api("GET", f"/api/entities/{slug}")["id"]
    except Exception:
        return None


def reconcile(provider, db_path):
    if not os.path.exists(db_path):
        return 0, 0

    now = datetime.now(timezone.utc)
    cutoff_old = (now - timedelta(days=CUTOFF_DAYS)).strftime("%Y-%m-%d")
    cutoff_recent = (now - timedelta(minutes=RECENT_GRACE_MIN)).strftime("%Y-%m-%dT%H:%M:%S")

    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row

    src = {}  # sid -> Counter(ts)
    for sid, ts in conn.execute(
            "SELECT SessionId, Timestamp FROM Messages WHERE Timestamp >= ? AND Timestamp < ?",
            (cutoff_old, cutoff_recent)):
        src.setdefault(sid, Counter())[ts] += 1

    # The legacy pre-plugin-split tables in the main redcompute.db hold a few
    # messages that exist nowhere else (imported separately) — count them as
    # source too, max-merged since most rows are exact duplicates.
    if provider == "claude-code":
        legacy_db = os.path.join(os.environ["LOCALAPPDATA"], "RedCompute", "redcompute.db")
        if os.path.exists(legacy_db):
            legacy_conn = sqlite3.connect(legacy_db)
            try:
                legacy = {}
                for sid, ts in legacy_conn.execute(
                        "SELECT SessionId, Timestamp FROM ClaudeMessages WHERE Timestamp >= ? AND Timestamp < ?",
                        (cutoff_old, cutoff_recent)):
                    legacy.setdefault(sid, Counter())[ts] += 1
                for sid, counts in legacy.items():
                    dst_counts = src.setdefault(sid, Counter())
                    for ts, n in counts.items():
                        dst_counts[ts] = max(dst_counts[ts], n)
            except sqlite3.OperationalError:
                pass  # legacy tables dropped — nothing to merge
            finally:
                legacy_conn.close()

    dst = {}  # sid -> ts -> [record ids]
    rows = psql(f"""
        SELECT "Data"->>'session_id', "Data"->>'timestamp', "Id"
        FROM "Records"
        WHERE "Stream"='session-messages' AND "Data"->>'provider'='{provider}'
          AND "Data"->>'timestamp' < '{cutoff_recent}';""")
    for sid, ts, rid in rows:
        dst.setdefault(sid, {}).setdefault(ts, []).append(int(rid))

    to_delete = []
    to_import = []  # (sid, ts, how_many)
    for sid in set(src) | set(dst):
        src_counts = src.get(sid, Counter())
        dst_counts = dst.get(sid, {})
        for ts in set(src_counts) | set(dst_counts):
            have = len(dst_counts.get(ts, []))
            want = src_counts.get(ts, 0)
            if have > want:
                # delete the later copies (migration re-imports)
                to_delete += sorted(dst_counts[ts])[want:]
            elif want > have:
                to_import.append((sid, ts, want - have))

    print(f"[{provider}] surplus records to delete: {len(to_delete)}, missing to import: {sum(n for _,_,n in to_import)}")

    if DRY_RUN:
        conn.close()
        return len(to_delete), sum(n for _, _, n in to_import)

    if to_delete:
        for i in range(0, len(to_delete), 500):
            ids = ",".join(map(str, to_delete[i:i+500]))
            psql(f'DELETE FROM "Records" WHERE "Stream"=\'session-messages\' AND "Id" IN ({ids});')
        print(f"[{provider}] deleted {len(to_delete)}")

    imported = 0
    entity_cache = {}
    batch = []

    def flush():
        nonlocal imported, batch
        if not batch:
            return
        result = api("POST", "/api/streams/session-messages/records", {"records": batch})
        imported += result["created"]
        batch = []

    for sid, ts, count in to_import:
        rows = conn.execute(
            "SELECT * FROM Messages WHERE SessionId=? AND Timestamp=? ORDER BY Id LIMIT ?",
            (sid, ts, count)).fetchall()
        if sid not in entity_cache:
            entity_cache[sid] = session_entity_id(provider, sid)
        for m in rows:
            # Postgres jsonb rejects NUL — replace with U+FFFD (the server
            # sanitizes too since the JsonText.SanitizeNul fix; belt-and-braces)
            clean = lambda v: v.replace("\x00", "�") if isinstance(v, str) else v
            record = {
                "data": {
                    "provider": provider,
                    "session_id": m["SessionId"],
                    "role": m["Role"],
                    "event_type": m["EventType"],
                    "content": clean(m["Content"]),
                    "tool_name": m["ToolName"],
                    "tool_input": clean(m["ToolInput"]),
                    "tool_result": clean(m["ToolResult"]),
                    "message_id": m["MessageId"],
                    "timestamp": m["Timestamp"],
                },
                "created_at": m["Timestamp"],
            }
            if entity_cache[sid]:
                record["entity_id"] = entity_cache[sid]
            batch.append(record)
            if len(batch) >= 200:
                flush()
    flush()
    if imported:
        print(f"[{provider}] imported {imported}")

    conn.close()
    return len(to_delete), imported


if __name__ == "__main__":
    total_del, total_imp = 0, 0
    for prov, path in PROVIDERS:
        d, i = reconcile(prov, path)
        total_del += d
        total_imp += i
    print(f"done{' (dry run)' if DRY_RUN else ''}: {total_del} deleted, {total_imp} imported")
