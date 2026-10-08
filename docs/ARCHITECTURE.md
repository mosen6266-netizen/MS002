# V8 architecture and migration contract

## Process model

```
SignalScheduler.exe (native WPF UI)
        │ Named Pipe IPC
        ▼
SignalScheduler.Engine.exe (per-user background engine)
        ├─ Durable task engine
        ├─ SQLite state store
        ├─ Signal Guardian
        │    ├─ JSON-RPC listAccounts probe
        │    ├─ healthy / busy / fault state machine
        │    ├─ owned-daemon restart policy
        │    └─ privacy-filtered diagnostics
        └─ signal-cli 0.14.9
             └─ Eclipse Temurin JRE 25.0.4.1+1
```

V8 uses a **per-user background engine**, not a LocalSystem Windows Service. Signal login data, LocalAppData, user-scoped DPAPI and the V7 data store all belong to the interactive Windows user.

The UI is not a lifecycle authority. Minimizing, closing and reopening the UI does not define durable task state.

## Signal Guardian rules

- Port 7583 is accepted as Signal only after a real JSON-RPC `listAccounts` response is validated.
- A valid existing signal-cli daemon may be reused.
- A responsive non-Signal process on 7583 becomes `external-conflict`; V8 never kills it.
- Short RPC failures become `busy`, not an immediate restart.
- An owned daemon is eligible for restart only after sustained RPC failure **and** no recent process output.
- Signal service recovery never implies task recovery.
- Alpha.3 keeps irreversible sends disabled; the next transport stage must perform a two-phase pause/persist handshake before Guardian may restart an owned daemon during active jobs.

## V7.6.2 baseline

- APP_VERSION: 7.6.2
- APP_BUILD: 7.6.2-clean-upgrade-20261008
- Data: `%LOCALAPPDATA%\SignalSchedulerData`
- V7 already has durable dispatch journal, inflight markers, recovery-needed semantics, WAL and fail-closed behavior.

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
4. Import account labels, scripts, steps and groups.
5. Any V7 running task is imported as paused; inflight/recovery tasks become `RecoveryRequired`.
6. Validate source/target counts inside the transaction.
7. Only after validation may later stages enable V8 execution.

Signal login-store behavior is intentionally kept in the same Windows user context. Alpha.3 first validates that the bundled runtime can see the user's existing default signal-cli account store before adding any relocation/migration step.

## Alpha.5 journal hardening

- Reserve only a Running task at its current cursor; no replay of a Prepared/Sending/Confirmed/RecoveryRequired dispatch.
- Recheck the synced account's enabled/online state and the membership of the intended Signal group atomically before entering Sending.
- On restart, Sending/Unknown becomes RecoveryRequired; Prepared is provably DefinitelyNotSent; previously Running jobs are paused.
- Confirmation advances the cursor in a single transaction even when the initiating caller token has already been cancelled.
- Guardian restart is two-phase: first pause tasks and check in-flight rows atomically; then, only if safe, stop/restart owned signal-cli. If the owned process already crashed, quarantine the ambiguous rows before recovery.
- **No irreversible Signal messaging is activated in alpha.5.**
