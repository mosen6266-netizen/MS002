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

Current stage: **8.0.0-alpha.8 durable dispatch safety and Guardian restart barrier**.

Alpha.4 also adds a fully Simplified-Chinese installer and a safe in-place upgrade handshake. Running/in-flight work blocks upgrade; supported builds are never force-killed after a failed safe-shutdown handshake. Real message sending remains intentionally disabled until the durable send transaction, in-flight recovery semantics and two-phase Guardian restart handshake are migrated from the V7 contract.

Alpha.5 adds a journal replay guard, live account/group authorization before Sending, durable ambiguous-result quarantine at startup, and a two-phase Guardian restart barrier that pauses tasks without silently resuming them. CI contains fake-transport regression tests. Irreversible Signal message sending remains deliberately **disabled** until full recovery UI and runner parity are delivered.

Alpha.6 adds a native Simplified-Chinese recovery center with V8 and read-only migrated V7 task rows, per-message journal detail, evidence copying, and transactional manual pause through Named Pipe IPC. The pause survives Windows restart and never rewrites an ambiguous in-flight send; pending sends remain eligible for recovery quarantine. No resume and no real message sending in alpha.6.

Alpha.7 introduces a native script editor backed by separate revisioned SQLite authoring tables. Original V7 script metadata remains untouched. Users can add/reorder/delete steps, specify optional sending accounts, delays, typing seconds, reminders and attachment references, and import/export a bounded JSON format. This stage does not embed attachment binaries or enable Signal message delivery.

Alpha.8 adds real account remark/enable management and Signal-group-ID-based selection, both persistent in V8 SQLite and surviving updates/restarts. A common Signal group across accounts is shown once and counted by member accounts, with user-friendly group names. Account-disable policy is checked again before the irreversible send boundary. Account settings and selected groups are staging metadata only, not a runner. Message sending remains disabled.

## 中文安装包下载

经过 Windows CI 完整验收并手动发布的测试安装包请访问 [GitHub Releases](https://github.com/mosen6266-netizen/MS002/releases)。详细操作见 [中文安装与发布说明](docs/INSTALL_RELEASE_GUIDE_ZH.md)。此渠道发布的 alpha 安装包仍然不是正式商用版本。
