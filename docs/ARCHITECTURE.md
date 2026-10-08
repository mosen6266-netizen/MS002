# V8 architecture and migration contract

## Process model

```
SignalScheduler.exe (native WPF UI)
        │ Named Pipe IPC
        ▼
SignalScheduler.Engine.exe (per-user background engine)
        ├─ Durable task engine
        ├─ SQLite state store
        ├─ Signal Guardian / supervisor   [Stage 3]
        └─ signal-cli transport           [Stage 3]
```

V8 uses a **per-user background engine**, not a LocalSystem Windows Service. This is deliberate: Signal login data, LocalAppData, user-scoped DPAPI and existing V7 data all belong to the interactive Windows user. Running the core as LocalSystem would introduce avoidable profile/permission failures.

The engine is independent from the UI. Minimizing or rendering faults cannot stop durable tasks. The installer registers the engine for the current user's login.

## V7.6.2 baseline

- APP_VERSION: 7.6.2
- APP_BUILD: 7.6.2-clean-upgrade-20261008
- Data: `%LOCALAPPDATA%\SignalSchedulerData`
- V7 already has durable `dispatch_journal`, inflight markers, `recovery_needed`, WAL and fail-closed behavior.

V8 creates `v8_*` tables beside the V7 schema. It does not drop or rebuild V7 tables.

## Release-blocking invariants

1. A confirmed logical dispatch key must never be automatically sent again.
2. Cursor advancement and confirmed persistence are one SQLite transaction.
3. Any exception after entering the irreversible send window is ambiguous unless definitely-not-sent can be proven.
4. Ambiguous send = `RecoveryRequired`; never auto-retry and never advance.
5. Signal recovery never auto-resumes a paused/recovery task.
6. UI failure/minimize/restart cannot silently change engine task state.
7. Engine/machine restart reconstructs state from SQLite, not memory.
8. Disabled/deleted accounts are rechecked immediately before irreversible send.
9. Updater may not kill an in-flight send merely to finish installation.
10. `SignalSchedulerData` is never deleted by normal install/repair/upgrade.

## Migration order

1. Read-only V7 schema inventory.
2. Consistent SQLite backup through SQLite backup API.
3. Create V8 schema/markers.
4. Import account labels, scripts, steps, attachments and groups.
5. Any V7 running task is imported as paused; inflight/recovery tasks become `RecoveryRequired`.
6. Validate source/target counts inside the transaction.
7. Only after validation may later stages enable V8 execution.

Signal login-store migration is separate and requires real Windows validation.
