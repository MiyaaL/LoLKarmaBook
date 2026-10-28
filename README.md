# LoLKarmaBook

Windows LOL 对局助手：自动连接本机已登录的客户端，按账号管理玩家笔记，再次相遇时提示历史记录；支持赛后补记和 Hexdata 海斗分查询。

## 安装 1.0.0

在 [GitHub Releases](https://github.com/MiyaaL/LoLKarmaBook/releases) 下载：

- `LoLKarmaBook-1.0.0-windows-x64-setup.exe`：Windows 10 / 11 64 位安装包，包含 .NET 运行时，无需另装 .NET。
- `LoLKarmaBook-1.0.0-windows-x64-setup.exe.sha256`：安装包的 SHA256 校验文件。

运行安装器，默认安装到 `D:\LoLKarmaBook`，也可选择其他可写文件夹。安装器提供开始菜单入口、可选桌面快捷方式和卸载程序。升级前从系统托盘退出 LoLKarmaBook，再安装到原程序目录；笔记数据位于该目录的 `userdata` 中。

## 开始使用

1. 登录 LOL 客户端，再启动 LoLKarmaBook；支持检测 WeGame 启动的客户端。
2. 在「当前对局」查看连接状态和名单。选择一名玩家后，右侧展开记录表单，名单中持续显示选中状态。
3. 勾选类别，按需添加英雄或装备标签、填写备注，再保存记录。提醒有效期默认 365 天，填写 `0` 表示永久；默认值可在设置中调整。
4. 在「我的记录」按状态、类别和时间筛选。常用的「编辑」直接显示，归档、撤回、发布草稿和删除位于「更多」菜单中。
5. 本局遇到已记录玩家时，赛后会显示右下角提醒卡片，可直接补记本局。没有历史命中时不自动弹窗；赛后 20 分钟内，可在「当前对局」点击「标记上一局」。
6. 自动检测失败时，展开「连接与对局详情」，选择包含 `LeagueClientUx.exe` 或 `lockfile` 的客户端目录，然后重新检测。

检测约每 2 秒进行一次，连接中断后自动重试。同局刷新会保留正在编辑的内容；每个游戏账号使用独立笔记空间，切换账号自动切换记录。「我的记录」中的「演示空间」提供独立的虚构数据，返回「实时对局」即可继续使用实际账号。

默认最小化或关闭窗口会收到系统托盘，检测继续运行；右键托盘图标选择「退出」才结束程序。可在「设置 / 关于」关闭托盘行为。

## Hexdata 海斗分

1. 打开「Hexdata 海斗分」，点击「登录 Hexdata」，在弹出的专用浏览器窗口完成登录，再回到程序点击「我已完成登录」。
2. 点击「查我的海斗分」，或输入 `名字#编号` 后点击「查这个 ID」。
3. 展开本地查询记录，查看海斗分、近期对局及双方名单；海斗分走势图使用已保存的查询结果。
4. 查询详情中的「查询玩家」和双方名单中的玩家 ID 均可选中文字，按 `Ctrl+C` 复制；聚焦后按 `Ctrl+A` 可选中完整 ID。

查询需要联网，分数和对局内容来自 Hexdata。退出 Hexdata 登录后，本地查询记录仍会保留。若服务仍在收集有效对局，页面会显示相应状态。

## 界面与记录

1.0.0 使用暖米黄色背景、深棕文字和琥珀色操作按钮。当前对局页将连接详情收起、保留明确的记录对象与保存入口；记录列表支持弹性宽度和筛选换行；设置页在宽窗口使用独立双列，在窄窗口自动改为单列，诊断日志默认折叠。

支持五类记录：挂机逃跑、消极游戏、言语挑衅/辱骂、游戏技术水平较低、乱出装。记录保存写下时的玩家游戏 ID，后续改名不会覆盖旧记录；稳定身份用于关联再次相遇的玩家。同局提醒去重，过期记录停止提醒，仍可在「我的记录」中管理。

快捷键：`Ctrl+1` 当前对局、`Ctrl+2` 我的记录、`Ctrl+3` Hexdata 海斗分、`Ctrl+4` 设置 / 关于，`F5` 刷新当前页面。

## 数据与备份

数据默认位于 **程序目录下的 `userdata`**，例如 `D:\LoLKarmaBook\userdata`。

- 每个账号的笔记数据库：`userdata/data/profiles/<空间标识>/lolkarmabook.db`。
- 设置、日志、快照、导出、草稿以及 Hexdata 查询缓存均在本地保存。
- 在「设置 / 关于」可导出快照、从快照恢复，并分别修改数据根目录、当前空间数据库、设置文件和日志目录。路径变更在退出时复制数据并保留原文件，下次启动生效。
- 备注、玩家游戏 ID、稳定身份、草稿及保存的登录令牌使用当前 Windows 用户的 DPAPI 保护；类别、时间等元数据和 Hexdata 查询报告并非整库加密。
- 快照恢复适用于同一台电脑、同一 Windows 用户。卸载保留 `userdata`。

客户端检测通过本机 HTTPS API 读取账号和对局信息；本地笔记按账号空间保存。

## 开发与验证

桌面程序使用 .NET 10 WPF；SDK 版本见 `global.json`。

```powershell
dotnet run --project src/LoLKarmaBook.Desktop
.\scripts\verify.ps1
```

验证脚本构建解决方案并运行 Core、Scoring、Integration、Riot、Desktop Logic、Client、Paths 的控制台断言套件，以及评分参考数据比对。实际本机客户端的只读探测可使用：

```powershell
dotnet run --project tests/LoLKarmaBook.Client.Tests -c Release -- --probe-local
```

构建 Windows 安装包需安装 Inno Setup 7，或带 `ChineseSimplified.isl` 的兼容 Inno Setup 6：

```powershell
.\scripts\installer-windows.ps1 -IsccPath 'C:\Program Files\Inno Setup 7\ISCC.exe'
```

默认在 `artifacts/` 输出安装包及同名 `.sha256` 文件，可通过 `-OutputDirectory` 指定目录。脚本拒绝覆盖同名产物，并检查发布目录不含用户数据库、日志和测试数据。

安装后的隔离启动检查：

```powershell
.\scripts\windows-smoke.ps1 -Executable 'D:\LoLKarmaBook\LoLKarmaBook.exe'
```

核心、存储、评分模块与 CLI 可在 Linux 运行，WPF 界面仅在 Windows 运行：

```bash
bash scripts/verify.sh
dotnet run --project src/LoLKarmaBook.Cli -- demo
dotnet run --project src/LoLKarmaBook.Cli -- performance fixtures/scoring/performance-demo.json
dotnet run --project src/LoLKarmaBook.Cli -- rating-demo
dotnet run --project src/LoLKarmaBook.Cli -- estimate-demo
```

实现细节见 [桌面与实时检测](docs/desktop.md)、[核心 API](docs/core-api.md)和[评分公式](docs/scoring.md)。发布构建与安装验证配置见 [Windows 工作流](https://github.com/MiyaaL/LoLKarmaBook/actions/workflows/windows-release.yml)。
