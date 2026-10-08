# Signal Auto Scheduler V8

Production-oriented Windows rewrite of Signal Auto Scheduler.

**Migration baseline:** V7.6.2 (`7.6.2-clean-upgrade-20261008`).

## V8 foundation

- .NET 8 native WPF desktop application
- Independent **per-user background engine**
- Named Pipe IPC; no localhost web UI
- SQLite WAL + `synchronous=FULL`
- Durable dispatch journal and explicit recovery states
- Standard Windows installer with selectable install path/progress UI
- Self-contained Windows publish; users do not install .NET/Python
- Existing data remains under `%LOCALAPPDATA%\SignalSchedulerData`

## Signal runtime

Alpha.3 adds the real offline Signal runtime:

- Eclipse Temurin JRE **25.0.4.1+1**
- signal-cli **0.14.9**
- real JSON-RPC `listAccounts` identity probe on `127.0.0.1:7583`
- layered Guardian states: `starting / healthy / busy / fault / external-conflict / missing-runtime`
- existing valid signal-cli daemon can be reused
- unknown processes on 7583 are never killed
- owned daemon restart requires sustained RPC failure and lack of processing output
- diagnostic Signal logs are privacy-filtered and rotated

A per-user engine is used instead of a LocalSystem Windows Service so existing Signal login data, user-scoped DPAPI and LocalAppData stay in the interactive user's security context.

### Reliability invariant

If delivery becomes ambiguous after entering the irreversible send window, V8 stops at that message and requires recovery. It never auto-retries an ambiguous send and never silently advances the cursor.

Current stage: **8.0.0-alpha.4 safe Chinese installer / upgrade lifecycle**.

Alpha.4 also adds a fully Simplified-Chinese installer and a safe in-place upgrade handshake. Running/in-flight work blocks upgrade; supported builds are never force-killed after a failed safe-shutdown handshake. Real message sending remains intentionally disabled until the durable send transaction, in-flight recovery semantics and two-phase Guardian restart handshake are migrated from the V7 contract.
