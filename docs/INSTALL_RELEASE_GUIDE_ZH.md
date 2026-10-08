# 中文离线安装包：下载及发布说明

## 用户下载

请优先访问本仓库的 [GitHub Releases](https://github.com/mosen6266-netizen/MS002/releases)。

在对应版本中下载 **SignalScheduler_Setup_V8.x.x-alpha.x.exe**。
这是完整的 Windows 10/11 64 位离线安装器：安装到用户电脑时，不需要额外安装 Python、PowerShell 模块、Java、.NET 或 signal-cli。

当前 alpha 测试阶段的真实 Signal 自动发送功能仍关闭，切勿将其当作正式商用版。

如果 Releases 里尚未发布该版本，可进入 [Windows V8 Build](https://github.com/mosen6266-netizen/MS002/actions/workflows/windows-build.yml)，找到成功的 **main** 分支构建，在页面底部 Artifacts 下载 **SignalScheduler-V8-Windows** 并解压使用；Actions Artifacts 会过期。

## 维护者发布（人工批准）

1. 等待 main 分支的 **Windows V8 Build** 所有步骤成功，包括离线运行环境校验、安装器生成、真实安装和覆盖升级冒烟检查。
2. 复制该成功运行的数字 run_id。
3. 在 [Publish Verified Windows Installer](https://github.com/mosen6266-netizen/MS002/actions/workflows/publish-verified-installer.yml) 中点击 **Run workflow**。
4. successful_run_id 填写成功运行的数字 ID；version_tag 填写该安装包的准确版本，例如 **v8.0.0-alpha.8**。
5. 工作流会确认来源是成功的 main 分支构建、检查 EXE 名称与版本号一致，然后发布 prerelease，附带哈希清单和具体来源 commit。重复版本号会拒绝覆盖。

## 数据与安全

- 用户数据位于 %LOCALAPPDATA%\SignalSchedulerData，正常安装和升级不得删除该文件夹。
- 正在执行任务或消息处于不可逆发送阶段时，不允许强制覆盖更新。
- 发布前确认版本号显示、桌面快捷方式和所有中文安装提示正常。
- 缺少测试结果时不要生成或标注为已验证的下载版本。
