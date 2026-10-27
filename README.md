# Codex 额度与恢复 1.0.8

Windows 本地融合插件：在 Codex 顶栏显示额度，额度恢复后继续因额度耗尽而中断的原任务。

## 组成与数据流

`CodexQuotaUnified.exe` 是顶栏和额度读取的唯一所有者，每 30 秒向官方本地 App Server 读取一次账户额度。它写入 `%LOCALAPPDATA%/CodexQuotaUnified/status.json`，恢复引擎只消费这份快照。任务状态每 10 秒观察一次，独立于额度查询频率；恢复引擎查询任务轮次时不读取额度。

恢复进程只处理启动后观察到活跃、随后同一轮次以官方额度耗尽错误失败的本地 Codex 任务。发送前再次确认最新轮次、任务非运行状态、恢复开关和额度快照。两种额度均需大于零；快照需无读取错误、未标旧且距官方读取不足 45 秒，以覆盖 30 秒检测间隔。

## 保留的本机改进

- 顶栏保留原已确认位置：相对上游自定义帮助菜单锚点向右 80 DIP、向下 5 DIP；96 DPI 时条带为 276×26，基准位置 x=402、y=10，随窗口移动和缩放。
- 恢复监控先取得命名互斥锁，再读写状态。未取得锁的实例不加载状态、不发送消息。
- 每个线程和失败轮次在发送前先写入持久化占用记录。发送结果不确定时标记 `uncertain_no_retry`，不自动重发。
- 首次安装迁移旧插件的 `dispatched` 去重账本；旧的待恢复任务与观察状态不迁移。
- 续跑提示词固定为：

> 本轮工作因 Codex 额度耗尽而中断，现在额度已恢复。请从该中断的已有进度继续用户原先要求的工作验证和交付

## 启用功能和使用

- 安装后 Codex 加载 MCP 插件，显示 5 小时、周剩余额度及电池边框样式的重置卡张数；顶栏不显示到期日期。
- 悬停约 0.3 秒弹出圆角卡片，只列出 `5h 刷新`、`周额度刷新`、每张 `重置卡 N 到期` 的时间。不显示剩余比例、卡数标题、倒计时、更新时间、时区说明或操作提示。时间仍按北京时间 `MM-dd HH:mm` 显示，缺失日期为 `--`。
- 卡片使用中性浅/深色背景、细边框、对齐的两列文字，跟随已有额度条的主题和 DPI；鼠标离开、顶栏隐藏或窗口移动时关闭，不抢输入焦点。
- 顶栏背景保留 1/255 alpha 命中层，文字间隙也可悬停。卡数以官方 `availableCount` 为准，到期时间只读取可用卡；右键顶栏可立即刷新。
- 顶栏采用绑定 Codex 的非激活 popup 窗口，定位时恢复其在宿主之上的层级，避免进程正常但被主窗口遮住；保持原有位置，不使用全局置顶。
- 不创建托盘图标；旧版失效图标由安装器定向清理，防止异常退出后积累残影。
- 本机按用户授权开启自动恢复；公开安装需显式使用 `-EnableRecovery` 启用。需要保持电脑和 Codex 运行；退出 Codex 后组件及恢复子进程结束。
- 暂停自动恢复：将本机数据目录 `recovery-settings.json` 的 `enabled` 改为 `false`，顶栏显示继续运行。重新执行安装命令并带上 `-EnableRecovery` 可再启用。
- 已删除额度路径的通知图标和低额度气泡提醒实现。
- 切换到其他应用或最小化 Codex 时，顶栏覆盖层隐藏，检测仍在后台运行。
- 不包含桌面玻璃电池和动画。不自动使用重置卡、购买额度、更换模型或批准权限。
- 在聊天中请求读取额度时，使用插件只读工具 `get_quota_status`，同时返回顶栏位置和恢复状态。

## 构建与安装

普通安装：从 [Releases](https://github.com/Bellaboy/codex-quota-unified/releases) 下载 Windows x64 压缩包并解压，在解压目录用 PowerShell 7 执行下面的安装命令。需要本机 Python 3.12 和已登录的 Codex Windows 桌面应用；预编译包自带 .NET 运行时。

```powershell
./install.ps1 -PythonPath '你的 python.exe 绝对路径' -EnableRecovery
```

从源码构建另需 .NET 10 SDK。使用 PowerShell 7：

```powershell
dotnet publish ./src/CodexStatusbar/CodexStatusbar.csproj -c Release -r win-x64 --self-contained true -o ./plugin/bin
./install.ps1 -PythonPath '你的 python.exe 绝对路径' -EnableRecovery
```

安装器注册独立市场 `codex-unified-tools` 和插件 `codex-quota-unified`，不修改 Codex 主程序，也不创建 Windows 登录启动项。`plugin/runtime.json` 仅保存本地 Python 路径，安装后随插件缓存复制。

v1.0.1 修复 v1.0.0 重启后未加载的问题：`mcp.json` 必须包含 Agent Plugins 的 `$schema`，否则官方加载器不发现该服务。另用 `.codex-plugin/plugin.json` 的 Codex 环境覆盖声明传入当前桌面连接环境，避免恢复进程继续使用上一次启动的连接。

v1.0.2 修复真实桌面冷启动没有 `CODEX_THREAD_ID` 时的恢复连接更新：使用当前启动提供的管道，保留此前官方调用提供的聊天身份。首次启用自动恢复请让 Codex 执行安装命令，以保存它提供的调用身份；普通终端没有该身份时，顶栏仍可工作，恢复监控会明确报告身份缺失。身份和连接均保存在本机，不进入发布包。

当前源码仍保留上游 Token/TPS 状态条及其 Windows 启动管理实现；统一入口阻止启用这些功能，构建尚未剔除这些源码。额度路径的低额度气泡提醒在 1.0.4 中随托盘图标删除。玻璃电池及动画没有进入统一插件安装包。

v1.0.3 只调整了托盘注册时机，用户仍复现残影；v1.0.4 直接移除通知图标。独立显示进程方案未通过用户视觉验证，已撤回且未发布，最终保留原 MCP 与 Codex 窗口 owner 绑定策略。

窗口移动接入原生位置事件，直接使用 SetWindowPos 平移已有分层表面；事件路径不排队等待 33 毫秒定时器，不为平移重复生成位图。常规轮询继续作为窗口发现和布局补充。

登录状态变化时只读取官方认证文件的修改时间，不读取认证内容；先清空旧额度并立即重查。查询期间登录状态再次改变时，丢弃该次结果。

MCP 连接关闭仍会结束对应显示进程；其他已连接的实例可以接管。该版本没有承诺脱离所有 MCP 连接后保持显示。清理脚本 `scripts/clear-legacy-tray.ps1` 只删除提示文字匹配旧额度插件且回调窗口已销毁的记录，不重启 Explorer、不改其他软件图标。

`tests/verify-plugin-discovery.py` 使用官方 `plugin/read` 和临时无模型会话验证服务发现、MCP 托管启动、新额度读数及恢复监控；仅对二进制握手成功不足以证明自动加载成功。

运行状态和官方桥接身份位于独立的本机数据目录；不提交到 GitHub。卸载使用：

```powershell
codex plugin remove codex-quota-unified@codex-unified-tools
codex plugin marketplace remove codex-unified-tools
```

## 上游及许可

- [1435661587/codex-quota-battery](https://github.com/1435661587/codex-quota-battery)：2.2.2，恢复引擎，保留原 MIT 许可于 `LICENSE-glass`。
- [xiaoxuw/codex-quota-battery](https://github.com/xiaoxuw/codex-quota-battery)：v1.1.1，顶栏、额度读取和布局，保留 `LICENSE`、`LICENSE-codex-token-overlay` 和 `THIRD-PARTY-NOTICES.md`。

本机工作副本的 `upstream/` 保留原样下载的源码；公开仓库记录的上游版本身份见 `verification/sources.json`。本项目以独立仓库发布，不向上游仓库推送代码。
