# V8 architecture and migration contract

## Process model

```
SignalScheduler.exe (native WPF UI)
        │ Named Pipe IPC
        ▼
SignalScheduler.Service.exe (Windows Service)
        ├─ Durable task engine
        ├─ SQLite state store
        ├─ Signal Guardian / supervisor   [Stage 3]
        └─ signal-cli transport           [Stage 3]
```

The UI is not a lifecycle authority. Minimizing, rendering faults or reopening the UI must not stop or mutate durable tasks.

## V7.6.2 baseline

- APP_VERSION: 7.6.2
- APP_BUILD: 7.6.2-clean-upgrade-20261008
- Data: `%LOCALAPPDATA%\SignalSchedulerData`
- V7 already has durable `dispatch_journal`, inflight markers, `recovery_needed`, WAL and fail-closed behavior.

V8 initially creates `v8_*` tables beside the V7 schema. It must not drop or rebuild V7 tables during migration.

## Release-blocking invariants

1. A confirmed logical dispatch key must never be automatically sent again.
2. Cursor advancement and confirmed dispatch persistence belong to the same SQLite transaction.
3. Any exception after entering the irreversible send window is ambiguous unless the transport can prove definitely-not-sent.
4. Ambiguous send = `RecoveryRequired`; never auto-retry and never advance.
5. Signal recovery never automatically resumes a paused/recovery task.
6. UI failure/minimize/restart cannot silently change service task state.
7. Service/machine restart reconstructs state from SQLite, not memory.
8. Disabled/deleted accounts are rechecked immediately before irreversible send.
9. Updater may not kill an in-flight send merely to finish installation.
10. `SignalSchedulerData` is never deleted by normal install/repair/upgrade.

## Migration order

1. Read-only V7 schema inventory.
2. Consistent SQLite backup.
3. Create V8 schema/markers.
4. Import account labels, scripts, steps, attachments and groups.
5. Conservatively map paused/recovery jobs.
6. Validate counts, hashes and references.
7. Only then enable the V8 execution engine.

Signal login-store migration is separate and requires real Windows validation.
