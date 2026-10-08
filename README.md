# Signal Auto Scheduler V8

Production-oriented Windows rewrite of Signal Auto Scheduler.

**Migration baseline:** V7.6.2 (`7.6.2-clean-upgrade-20261008`).

## V8 foundation

- .NET 8 native WPF desktop application
- Independent **per-user background engine**
- Named Pipe IPC; no localhost web UI
- SQLite WAL + `synchronous=FULL`
- Durable dispatch journal and explicit recovery states
- signal-cli isolated behind a supervised transport adapter
- Standard Windows installer with selectable install path/progress UI
- Self-contained Windows publish; users do not install Python or .NET
- Existing data remains under `%LOCALAPPDATA%\SignalSchedulerData`

A per-user engine is used instead of a LocalSystem Windows Service so existing Signal login data, user-scoped DPAPI and LocalAppData stay in the same Windows security context.

### Reliability invariant

If delivery becomes ambiguous after entering the irreversible send window, V8 stops at that message and requires recovery. It never auto-retries an ambiguous send and never silently advances the cursor.

Current stage: **8.0.0-alpha.2 native dashboard + V7 metadata migration**. The installer now creates explicit desktop/start-menu shortcuts and the native dashboard reads migrated accounts, groups, scripts and recovery jobs. Signal sending remains intentionally disabled until Guardian/login/send transport migration is completed.
