# MS002 可迁移备份包：内容与缺口（2026-10-13）
此备份创建于原账号仍在线时。主运行线路：ui/v2-monochrome，标签 v8.0.0-ui2.4。原仓库 main 是另一条版本线，请勿混淆。

## 压缩包里的文件
- git/MS002-all-refs.bundle：所有可抓取远程 Git 分支、提交与标签；用于向全新 GitHub 仓库镜像迁移。
- source/main 和 source/ui-v2-monochrome：方便直接查看的源码快照。
- updates：旧自动更新分支的内容快照。
- installer/SignalScheduler_Setup_V8.0.0-ui2.4.exe：原发布的离线安装包。
- github/releases.json：原 GitHub Releases API 元信息；不是所有历史发行版的 EXE。
- .backup/GPT_RESTORE_PROMPT_ZH.md：发给新 ChatGPT 的恢复总指令。
- .backup/MERGE_PRIVATE_BACKUPS.ps1：将独立的敏感云端备份与本包合并到一个本地 ZIP。
- SHA256SUMS.txt：压缩前文件的完整校验和。

## 尚需另行保存的**私人**数据
- Cloudflare Worker 的真实运行脚本：signal-scheduler-license（请用现有 Cloudflare 账号导出，不能在公开 GitHub 存放后台源码及敏感凭证）。
- Cloudflare 当前使用的 D1 全量 SQL：signal_scheduler_license （绑定名 DB；旧 ID 0fe279c8-53dd-4875-abab-aa6f8db5a659）。_v2 数据库只是另一独立资源，当前 Worker 未绑定。
- Worker secret WORKER_MASTER_KEY 原值（Cloudflare 不提供读回）：若原值丢失，必须设计重新签名/过渡方案，原租约无法一丝不差地恢复。
- 所有 Windows 机器上的 %LOCALAPPDATA%\\SignalSchedulerData、signal-cli 账号链接状态、附件、账号备注、个人剧本和许可文件。DPAPI 与机器/用户绑定，跨机不保证登录状态可用。
- 使用中的 VPS 全盘/容器/配置/数据备份，以及域名注册权、TLS 证书、GitHub Actions secrets、安装包代码签名私钥。这些从 MS002 GitHub 仓库读取不到。

## 与原环境对应的 Cloudflare 清单
- 账户内 Worker：signal-scheduler-license
- Worker binding：DB -> signal_scheduler_license （D1）
- secret 名称：WORKER_MASTER_KEY（原值不可见）
- Worker 兼容日期：2026-10-06
- 旧 workers.dev 入口：https://signal-scheduler-license.mosen6266-ms007.workers.dev/
- 其他 Worker ms007-crm、ms007-crm-staging 未被证明是 MS002 授权依赖，**不自动合并**。
- DNS 历史：ms007.me A 记录 155.138.211.252（是否仍为 MS002 服务端必须单独验证）。
- 原域名、旧账号 OAuth、旧 Worker 子域名和密钥即使建立新账号也不能当然复用。

## 恢复原则
切换新账号时必须新建所有第三方身份和密钥，测试后才切换客户端授权与更新入口；若现有安装器内写死旧网址，旧账号同时消失意味着旧安装器不能无缝自动切换。该备份可以复刻源代码、接口及功能，但不可保证恢复无法导出的旧第三方身份、Signal 登录会话、私钥、域名所有权。

**安全警告**：私人 D1 SQL 含真实卡密/管理/设备资料，不要上传公共 GitHub、公开分享或通过不可信工具处理。
