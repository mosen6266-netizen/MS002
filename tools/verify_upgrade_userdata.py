#!/usr/bin/env python3
"""Non-destructive CI-only proof that Inno Setup in-place upgrades preserve V8 data.

Uses only Python's standard-library sqlite3; never runs on customer machines.
Run "seed" only in an isolated GitHub Actions runner after Engine initialization.
The expected snapshot is written OUTSIDE the installed app and user data directory.
"""
import hashlib
import json
import sqlite3
import sys
from pathlib import Path

ACCOUNT = "ci-test-account-not-a-real-number"
JOB = "ci-upgrade-completed-task"
SCRIPT = "ci-upgrade-script"
GROUP = "ci-test-group"
DISPATCH = "ci-upgrade-confirmed-dispatch"
MESSAGE = "CI 虚构剧本：覆盖升级前的内容必须保留"
DATA_FILE = "ci-user-owned-fixture.bin"
OPTIONS_FILE = "read-options.json"

QUERIES = {
    "account_label": (
        "SELECT label,enabled,revision FROM v8_account_settings WHERE account=?", (ACCOUNT,)
    ),
    "account_catalog": (
        "SELECT label,enabled FROM v8_signal_accounts WHERE account=?", (ACCOUNT,)
    ),
    "selected_group": (
        "SELECT group_id,selected_at FROM v8_selected_groups WHERE group_id=?", (GROUP,)
    ),
    "script": (
        "SELECT script_id,name,revision,target_group_id FROM v8_editor_scripts WHERE script_id=?", (SCRIPT,)
    ),
    "script_step": (
        "SELECT position,account,message,attachment,delay_after,typing_seconds "
        "FROM v8_editor_steps WHERE script_id=? ORDER BY position", (SCRIPT,)
    ),
    "script_revision": (
        "SELECT revision,name,steps_json FROM v8_editor_versions "
        "WHERE script_id=? ORDER BY revision", (SCRIPT,)
    ),
    "finished_job": (
        "SELECT state,cursor FROM v8_jobs WHERE job_id=?", (JOB,)
    ),
    "dispatch": (
        "SELECT state,cursor,account_id,group_id,payload_hash "
        "FROM v8_dispatch_journal WHERE dispatch_key=?", (DISPATCH,)
    ),
    "history": (
        "SELECT event_type,detail FROM v8_event_log WHERE job_id=? ORDER BY id", (JOB,)
    ),
}

def snapshot(root: Path) -> dict:
    dbpath = root / "data.db"
    if not dbpath.is_file():
        raise AssertionError("User data database missing after upgrade")
    with sqlite3.connect(str(dbpath), timeout=20) as con:
        state = {key: con.execute(query, params).fetchall()
                 for key, (query, params) in QUERIES.items()}
    for key, rows in state.items():
        if not rows:
            raise AssertionError(f"Expected user data missing: {key}")
    for name in (DATA_FILE, OPTIONS_FILE):
        path = root / name
        if not path.is_file():
            raise AssertionError(f"User-owned file missing: {name}")
        state["sha256_" + name] = hashlib.sha256(path.read_bytes()).hexdigest()
    return state


def seed(root: Path) -> None:
    dbpath = root / "data.db"
    if not dbpath.is_file():
        raise AssertionError("Engine has not initialized the actual per-user database")
    with sqlite3.connect(str(dbpath), timeout=20) as con:
        con.execute("PRAGMA busy_timeout=20000")
        required = {
            "v8_signal_accounts", "v8_account_settings", "v8_selected_groups",
            "v8_editor_scripts", "v8_editor_steps", "v8_editor_versions",
            "v8_jobs", "v8_dispatch_journal", "v8_event_log",
        }
        existing = {r[0] for r in con.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        missing = required - existing
        if missing:
            raise AssertionError("Engine schema not ready: " + ", ".join(sorted(missing)))
        with con:
            con.execute(
                "INSERT INTO v8_signal_accounts(account,label,enabled,online,last_seen) "
                "VALUES(?,?,1,0,0)", (ACCOUNT, "CI 原始账号备注")
            )
            con.execute(
                "INSERT INTO v8_account_settings(account,label,enabled,revision,updated_at) "
                "VALUES(?,?,1,1,100)", (ACCOUNT, "CI 自定义备注 - 应保留")
            )
            con.execute(
                "INSERT INTO v8_selected_groups(group_id,selected_at) VALUES(?,?)",
                (GROUP, 123456789)
            )
            con.execute(
                "INSERT INTO v8_editor_scripts("
                "script_id,legacy_id,name,target_group_id,revision,created_at,updated_at) "
                "VALUES(?,NULL,?,?,2,100,200)",
                (SCRIPT, "CI 编辑过的剧本", GROUP)
            )
            con.execute(
                "INSERT INTO v8_editor_steps("
                "script_id,position,account,message,attachment,pause_after,"
                "reminder_text,delay_after,typing_seconds) "
                "VALUES(?,0,?,?,?,0,?,45,5)",
                (SCRIPT, ACCOUNT, MESSAGE, "", "人工提示保留")
            )
            con.execute(
                "INSERT INTO v8_editor_versions("
                "script_id,revision,name,target_group_id,steps_json,saved_at) "
                "VALUES(?,1,?,?,?,150)",
                (SCRIPT, "CI 初始版本", GROUP,
                 '[{"Position":0,"Message":"旧版本保留","Attachment":""}]')
            )
            con.execute(
                "INSERT INTO v8_jobs(job_id,state,cursor,updated_at) "
                "VALUES(?,'Completed',1,300)", (JOB,)
            )
            con.execute(
                "INSERT INTO v8_dispatch_journal("
                "dispatch_key,job_id,run_token,run_cycle,cursor,group_id,"
                "account_id,payload_hash,state,provider_message_id,detail,"
                "created_at,updated_at) "
                "VALUES(?,?,'ci-token',1,0,?,?,'ci-hash','Committed',NULL,"
                "'CI 已确认记录',300,300)",
                (DISPATCH, JOB, GROUP, ACCOUNT)
            )
            con.execute(
                "INSERT INTO v8_event_log("
                "job_id,dispatch_key,event_type,detail,created_at) "
                "VALUES(?,?, 'batch_step_confirmed','CI 保留历史证据',300)",
                (JOB, DISPATCH)
            )
    (root / DATA_FILE).write_bytes(b"CI-ONLY-USER-DATA-KEEP-ME-v1\x00\x01")
    (root / OPTIONS_FILE).write_text(
        json.dumps({"ReadReceipts": True, "LinkedDeviceSync": False}),
        encoding="utf-8"
    )


def main() -> None:
    if len(sys.argv) != 4 or sys.argv[1] not in {"seed", "verify"}:
        raise SystemExit("usage: verify_upgrade_userdata.py seed|verify DATA_ROOT EXPECTED_JSON")
    command, root_arg, expected_arg = sys.argv[1:]
    root, expected_file = Path(root_arg), Path(expected_arg)
    if command == "seed":
        if expected_file.exists():
            raise AssertionError("Fixture expected snapshot must be fresh")
        seed(root)
        expected_file.write_text(json.dumps(snapshot(root), ensure_ascii=False), encoding="utf-8")
        print("Seeded isolated CI-only user account, script, history, preferences and file marker")
    else:
        expected = json.loads(expected_file.read_text(encoding="utf-8"))
        actual = snapshot(root)
        if actual != expected:
            differences = sorted(k for k in expected.keys() | actual.keys()
                                 if expected.get(k) != actual.get(k))
            raise AssertionError("User data changed during in-place upgrade: " + ", ".join(differences))
        print("PASS: account remarks, scripts/steps/revisions, completed history,"
              " selected group and user-owned files survived in-place upgrade")


if __name__ == "__main__":
    main()
