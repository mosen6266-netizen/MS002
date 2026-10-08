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

Current stage: **8.0.0-alpha.12 durable dispatch safety and Guardian restart barrier**.

Alpha.4 also adds a fully Simplified-Chinese installer and a safe in-place upgrade handshake. Running/in-flight work blocks upgrade; supported builds are never force-killed after a failed safe-shutdown handshake. Real message sending remains intentionally disabled until the durable send transaction, in-flight recovery semantics and two-phase Guardian restart handshake are migrated from the V7 contract.

Alpha.5 adds a journal replay guard, live account/group authorization before Sending, durable ambiguous-result quarantine at startup, and a two-phase Guardian restart barrier that pauses tasks without silently resuming them. CI contains fake-transport regression tests. Irreversible Signal message sending remains deliberately **disabled** until full recovery UI and runner parity are delivered.

Alpha.6 adds a native Simplified-Chinese recovery center with V8 and read-only migrated V7 task rows, per-message journal detail, evidence copying, and transactional manual pause through Named Pipe IPC. The pause survives Windows restart and never rewrites an ambiguous in-flight send; pending sends remain eligible for recovery quarantine. No resume and no real message sending in alpha.6.

Alpha.7 introduces a native script editor backed by separate revisioned SQLite authoring tables. Original V7 script metadata remains untouched. Users can add/reorder/delete steps, specify optional sending accounts, delays, typing seconds, reminders and attachment references, and import/export a bounded JSON format. This stage does not embed attachment binaries or enable Signal message delivery.

Alpha.8 adds real account remark/enable management and Signal-group-ID-based selection, both persistent in V8 SQLite and surviving updates/restarts. A common Signal group across accounts is shown once and counted by member accounts, with user-friendly group names. Account-disable policy is checked again before the irreversible send boundary. Account settings and selected groups are staging metadata only, not a runner. Message sending remains disabled.

## 中文安装包下载

经过 Windows CI 完整验收并手动发布的测试安装包请访问 [GitHub Releases](https://github.com/mosen6266-netizen/MS002/releases)。详细操作见 [中文安装与发布说明](docs/INSTALL_RELEASE_GUIDE_ZH.md)。此渠道发布的 alpha 安装包仍然不是正式商用版本。

## V8 alpha.9 多群任务预演

在左侧「运行任务」中选择已保存的剧本、勾选此前在群组管理中保存的群组，可以创建彼此独立的任务**预演**。后台负责按剧本顺序、输入时间、消息间隔模拟推进；可对单个群的任务暂停、继续、停止，提醒暂停会置顶弹窗。所有游标、任务状态及审计事件写入 SQLite，重启后待运行任务一律暂停并需人工继续。

**预演绝不调用 Signal API，也不会发送真实消息。** 这一步验证工作流、调度和异常恢复。实际发送、图片附件和授权管理仍不可用，不应将 alpha.9 当成可自动发消息的正式版。

## V8 alpha.10 图片附件导入和预览

在原生剧本编辑器选中某条消息，点击「为选中句导入图片」，程序把不超过 15 MB 的 PNG/JPEG/GIF/BMP 图片复制到 `%LOCALAPPDATA%\SignalSchedulerData\attachments\images`，采用 SHA-256 内容寻址去重并防路径穿越，保存到剧本中的仅为 `img:<sha256>.<ext>` 引用。使用「预览图片」时重新校验 SHA-256，阻止受损文件显示。原始用户图片不删除，升级程序不覆盖附件数据。

**当前不包含图片实际发送，也不自动修复 V7 已丢失的附件路径。** 与 alpha.9 一样，真实 Signal 自动发送仍关闭。

## V8 alpha.11 沿用已有授权后台

该版本连接现有 Cloudflare Worker `signal-scheduler-license` 的 `/api/license/activate` 和 `/api/license/check`。在原生「授权设置」中可输入已有或新卡密、在线验证类型与到期日期。卡密和稳定设备标识使用 Windows DPAPI（当前用户）加密保存在 LocalAppData；6 小时以内离线租约通过 P-256 ECDSA 验签与首次激活公钥锁定验证，后台禁用和设备限制以服务器为准。程序不生成新卡、不修改云端数据库、不保存管理员凭证。

**当前仅实现 V8 激活/校验与资料持久化；既有 V7 本地设备绑定标识尚未自动迁移。旧卡如超过设备上限，需要在既有后台合法解绑并重新激活。真实 Signal 发送仍禁用，所以当前授权功能不等于完整的生产级反破解能力。**

## V8 alpha.12 · 真实发送测试（严格限制）

用户可以在左侧「实发测试」打开专用对话框，选择已经勾选的**专用测试群**与属于该群的在线账号，勾选同意并二次确认后发送**单条**带测试编号的真实 Signal 消息。此功能不读取剧本，也不触发任何其他群。

使用已有 Cloudflare 卡密进行**在线**校验。每次发送创建独立 SQLite 任务和发送日志，调用 `signal-cli JSON-RPC send`，检查响应 JSON-RPC 请求 ID 和 `timestamp`。只有确认收到有效回执才将任务标记 Completed。断网、超时、服务重启、RPC 错误及不完整回复都视为结果不明：暂停并记录在恢复中心，**不自动重试**。一次只允许一条，至少间隔 90 秒，禁止绕过未知发送记录；不允许在非勾选群组发送。

**alpha.12 尚不能自动执行真实多账号/多群剧本。** 预演任务仍严格不调用 Signal。要完成全功能验收，还需用户在自己的专用测试群执行端到端检查和负载验证；CI 的虚拟 RPC 响应不能证明真实群已收到消息。
