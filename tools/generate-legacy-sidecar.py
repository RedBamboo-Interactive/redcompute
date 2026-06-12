"""One-off: snapshot the legacy-only message counts into a sidecar JSON.

The legacy ClaudeSessions/ClaudeMessages tables were dropped after their
unique messages were imported into RedLeaf, so the reconcile script lost
its source-of-truth for those rows and now flags them as surplus. This
captures, for every (session, timestamp) currently flagged, the full
RedLeaf count — which equals max(plugin, legacy) from the last verified-
clean reconcile — as the permanent legacy allowlist.
"""

import json
import os
import sqlite3
import subprocess
from collections import Counter
from datetime import datetime, timedelta, timezone

PG = ["docker", "exec", "cms-postgres-1", "psql", "-U", "redleaf", "-d", "redleaf", "-t", "-A", "-F", "\t"]
PLUGINS = os.path.join(os.environ["LOCALAPPDATA"], "RedCompute", "plugins")
DB = os.path.join(PLUGINS, "claude-code", "claude.db")
OUT = os.path.join(os.path.dirname(__file__), "legacy-claude-messages.json")


def psql(query):
    out = subprocess.run(PG + ["-c", query], capture_output=True, text=True, check=True)
    return [line.split("\t") for line in out.stdout.splitlines() if line.strip()]


now = datetime.now(timezone.utc)
cutoff_old = (now - timedelta(days=180)).strftime("%Y-%m-%d")
cutoff_recent = (now - timedelta(minutes=10)).strftime("%Y-%m-%dT%H:%M:%S")

conn = sqlite3.connect(DB)
src = {}
for sid, ts in conn.execute(
        "SELECT SessionId, Timestamp FROM Messages WHERE Timestamp >= ? AND Timestamp < ?",
        (cutoff_old, cutoff_recent)):
    src.setdefault(sid, Counter())[ts] += 1

dst = {}
rows = psql(f"""
    SELECT "Data"->>'session_id', "Data"->>'timestamp', "Id"
    FROM "Records"
    WHERE "Stream"='session-messages' AND "Data"->>'provider'='claude-code'
      AND "Data"->>'timestamp' < '{cutoff_recent}';""")
for sid, ts, rid in rows:
    dst.setdefault(sid, {}).setdefault(ts, []).append(int(rid))

sidecar = {}
for sid in dst:
    for ts, ids in dst[sid].items():
        have = len(ids)
        want = src.get(sid, Counter()).get(ts, 0)
        if have > want:
            sidecar.setdefault(sid, {})[ts] = have

with open(OUT, "w", encoding="utf-8") as f:
    json.dump(sidecar, f, indent=2, sort_keys=True)

total = sum(c for tss in sidecar.values() for c in tss.values())
flagged = sum(c - src.get(sid, Counter()).get(ts, 0)
              for sid, tss in sidecar.items() for ts, c in tss.items())
print(f"wrote {OUT}: {len(sidecar)} sessions, {total} total counts ({flagged} legacy-only)")
