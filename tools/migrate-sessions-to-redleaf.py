"""One-time migration: plugin SQLite session DBs -> RedLeaf.

Sessions become `ai-session` entities (versioning off), messages become
`session-messages` records with original timestamps preserved. Safe to re-run:
sessions upsert by slug, but messages would duplicate — only run the message
phase once per machine (use --sessions-only to refresh entities).

Usage: python migrate-sessions-to-redleaf.py [--dry-run] [--sessions-only]
"""

import json
import os
import re
import sqlite3
import sys
import urllib.request
from datetime import datetime, timedelta, timezone

REDLEAF = "http://localhost:18804"
MESSAGE_CUTOFF_DAYS = 180  # matches session-messages stream retention
BATCH = 500

PLUGINS = os.path.join(os.environ["LOCALAPPDATA"], "RedCompute", "plugins")
PROVIDERS = [
    ("claude-code", os.path.join(PLUGINS, "claude-code", "claude.db"), "ClaudeSessionId"),
    ("opencode", os.path.join(PLUGINS, "opencode", "opencode.db"), "OpenCodeSessionId"),
    ("codex", os.path.join(PLUGINS, "codex", "codex.db"), None),
]

DRY_RUN = "--dry-run" in sys.argv
SESSIONS_ONLY = "--sessions-only" in sys.argv


def api(method, path, body=None):
    req = urllib.request.Request(
        REDLEAF + path,
        data=json.dumps(body).encode() if body is not None else None,
        headers={"Content-Type": "application/json"},
        method=method,
    )
    try:
        with urllib.request.urlopen(req) as resp:
            return resp.status, json.loads(resp.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, json.loads(e.read() or b"null")


def session_slug(provider, session_id):
    # must match RelayServer.SessionEntitySlug: lowercase, non-alnum -> '-'
    sanitized = re.sub(r"[^a-z0-9]", "-", session_id.lower())
    return f"ai-session-{provider}-{sanitized}"


def ensure_schema():
    status, _ = api("GET", "/api/entity-types/ai-session")
    if status == 404:
        status, body = api("POST", "/api/entity-types", {
            "name": "AI Session",
            "description": "AI coding/inference session (claude-code, opencode, codex)",
            "icon": "fa-solid fa-square-terminal",
            "color": "pink",
            "versioning": False,
        })
        assert status in (200, 201), f"create ai-session type failed: {status} {body}"
        print("created entity type ai-session")

    status, _ = api("GET", "/api/entities/session-messages")
    if status == 404:
        status, body = api("PUT", "/api/entities/by-slug/session-messages", {
            "name": "Session Messages",
            "type_slug": "stream",
            "data": {
                "description": "Messages and tool events from AI sessions",
                "retention_days": 180,
                "parent_type": "ai-session",
                "app": "redcompute",
            },
        })
        assert status in (200, 201), f"register session-messages stream failed: {status} {body}"
        print("registered stream session-messages")


def migrate_provider(provider, db_path, external_id_col):
    if not os.path.exists(db_path):
        print(f"[{provider}] no database, skipping")
        return

    conn = sqlite3.connect(db_path)
    conn.row_factory = sqlite3.Row
    sessions = conn.execute("SELECT * FROM Sessions").fetchall()
    print(f"[{provider}] {len(sessions)} session(s)")

    cutoff = (datetime.now(timezone.utc) - timedelta(days=MESSAGE_CUTOFF_DAYS)).strftime("%Y-%m-%d")
    entity_ids, failures = {}, 0

    for s in sessions:
        cols = s.keys()
        get = lambda c: s[c] if c in cols else None
        slug = session_slug(provider, s["Id"])
        data = {
            "provider": provider,
            "session_id": s["Id"],
            "project_name": s["ProjectName"],
            "project_path": s["ProjectPath"],
            "status": s["Status"],
            "stop_reason": get("StopReason"),
            "started_at": s["StartedAt"],
            "model": get("Model"),
            "external_session_id": get(external_id_col) if external_id_col else None,
            "message_count": s["MessageCount"],
            "cost_usd": get("CostUsd"),
            "input_tokens": get("InputTokens"),
            "output_tokens": get("OutputTokens"),
            "cache_read_input_tokens": get("CacheReadInputTokens") or get("CachedInputTokens"),
            "cache_creation_input_tokens": get("CacheCreationInputTokens"),
            "context_tokens": get("ContextTokens"),
            "context_window": get("ContextWindow"),
            "effort": get("Effort"),
            "job_id": get("JobId"),
            "dismissed": bool(s["Dismissed"]),
            "source": get("Source"),
            "app": "redcompute",
        }
        name = get("Title") or f"{s['ProjectName']} ({provider})"
        if DRY_RUN:
            entity_ids[s["Id"]] = "dry"
            continue
        status, body = api("PUT", f"/api/entities/by-slug/{slug}",
                           {"name": name, "type_slug": "ai-session", "data": data})
        if status in (200, 201):
            entity_ids[s["Id"]] = body["id"]
        else:
            failures += 1
            print(f"  session {s['Id']} failed: {status} {body}")

    print(f"[{provider}] sessions upserted: {len(entity_ids)}, failed: {failures}")
    if SESSIONS_ONLY:
        conn.close()
        return

    total = conn.execute(
        "SELECT COUNT(*) FROM Messages WHERE Timestamp >= ?", (cutoff,)).fetchone()[0]
    print(f"[{provider}] {total} message(s) within {MESSAGE_CUTOFF_DAYS}d cutoff")

    sent = 0
    batch = []

    def flush():
        nonlocal sent, batch
        if not batch or DRY_RUN:
            batch = []
            return
        status, body = api("POST", "/api/streams/session-messages/records", {"records": batch})
        if status != 200:
            print(f"  batch failed: {status} {body}")
        else:
            sent += body["created"]
        batch = []

    for m in conn.execute("SELECT * FROM Messages WHERE Timestamp >= ? ORDER BY Id", (cutoff,)):
        record = {
            "data": {
                "provider": provider,
                "session_id": m["SessionId"],
                "role": m["Role"],
                "event_type": m["EventType"],
                "content": m["Content"],
                "tool_name": m["ToolName"],
                "tool_input": m["ToolInput"],
                "tool_result": m["ToolResult"],
                "message_id": m["MessageId"],
                "timestamp": m["Timestamp"],
            },
            "created_at": m["Timestamp"],
        }
        eid = entity_ids.get(m["SessionId"])
        if eid and eid != "dry":
            record["entity_id"] = eid
        batch.append(record)
        if len(batch) >= BATCH:
            flush()
            if sent and sent % 10000 < BATCH:
                print(f"  ... {sent}/{total}")
    flush()
    print(f"[{provider}] messages migrated: {sent}")
    conn.close()


if __name__ == "__main__":
    if not DRY_RUN:
        ensure_schema()
    for prov, path, ext_col in PROVIDERS:
        migrate_provider(prov, path, ext_col)
    print("done" + (" (dry run)" if DRY_RUN else ""))
