# Signal Auto Scheduler V8

Production-oriented Windows rewrite of Signal Auto Scheduler.

**Migration baseline:** V7.6.2 (`7.6.2-clean-upgrade-20261008`).

## V8 foundation

- .NET 8 native WPF desktop application
- Independent .NET 8 Windows Service
- Named Pipe IPC; no localhost web UI
- SQLite WAL + `synchronous=FULL`
- Durable dispatch journal and explicit recovery states
- signal-cli isolated behind a supervised transport adapter
- Standard Windows installer with selectable install path/progress UI
- Self-contained Windows publish; users do not install Python or .NET
- Existing data remains under `%LOCALAPPDATA%\SignalSchedulerData`

### Reliability invariant

If delivery becomes ambiguous after entering the irreversible send window, V8 must stop at that message and require recovery. It must never auto-retry an ambiguous send and must never silently advance the cursor.

Current stage: **8.0.0-alpha.1 foundation**. Signal sending is intentionally disabled until the V7.6.2 Signal/Guardian contract is migrated and tested.
