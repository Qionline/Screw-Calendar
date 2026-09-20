# Screw Calendar

[![Build](https://github.com/Qionline/Screw-Calendar/actions/workflows/build.yml/badge.svg)](https://github.com/Qionline/Screw-Calendar/actions/workflows/build.yml)
[![License: GPL v3 or later](https://img.shields.io/badge/License-GPL--3.0--or--later-blue.svg)](LICENSE)

Screw Calendar 是一个面向 Windows 10/11 的桌面日历与本机 Markdown 待办工具。它可以作为始终置顶或位于普通窗口下方的桌面挂件运行，并通过系统托盘管理显示状态。

![Screw Calendar logo](logo.png)

## 功能

- 星期一至星期日的七列日历，支持 5～10 个日期行、按月翻页和“回到今天”。
- 经典、极简、紧凑三种样式，支持浅色/深色、四种主题色和透明度。
- 今天高亮、周末区分、二十四节气，以及可静默联网更新的中国法定节假日和调休标记。
- 常驻待办和按日期保存的 Markdown 待办，提供普通可视块编辑器和本机图片归档。
- 托盘、开机启动、位置和大小锁定、窗口层级、多显示器位置恢复与单实例运行。
- 简体中文和英文界面。
- JSON 导入导出、主文件自动备份和损坏恢复。

## 下载与运行

从 [GitHub Releases](https://github.com/Qionline/Screw-Calendar/releases) 下载最新的 `ScrewCalendar-*-win-x64.zip`，校验同版本 `.sha256` 文件后解压，运行 `ScrewCalendar.exe`。发布包自带 .NET 运行时，无需安装程序或额外运行库。

当前仅提供 Windows 10/11 x64 版本。Windows 首次运行未经代码签名的开源程序时可能显示安全提示，请确认文件来自本仓库的 Release 页面并核对 SHA-256。

使用方法、数据位置、备份与迁移请阅读[用户指南](docs/USER_GUIDE.md)。

## 本机数据

程序不会同步或上传日历内容。程序会从 `holiday-cn` 下载公开的节假日 JSON，并在启动或翻页时检查当前查看年份及下一年的数据；成功缓存后 24 小时内不重复下载，网络不可用时自动使用缓存或内置数据。数据保存在：

```text
%LocalAppData%\ScrewCalendar\
├─ data\calendar.json
├─ data\calendar.json.bak
├─ data\assets\
├─ data\holiday-cache\
└─ logs\app.log
```

JSON 导出不包含 `assets` 中的图片。跨电脑迁移包含图片的待办时，需要同时复制 `assets` 文件夹。

## 从源码构建

安装不低于 [global.json](global.json) 基线版本的 .NET 8 SDK，然后运行：

```powershell
dotnet restore .\ScrewCalendar.sln --configfile .\NuGet.Config
dotnet run --project .\tests\ScrewCalendar.Tests\ScrewCalendar.Tests.csproj -c Release --no-restore
dotnet format .\ScrewCalendar.sln --verify-no-changes --no-restore
dotnet build .\ScrewCalendar.csproj -c Release --no-restore
```

完整架构、开发规范和发布步骤见[开发文档](docs/DEVELOPMENT.md)与[发布指南](docs/RELEASING.md)。

## 参与贡献

提交修改前请阅读[贡献指南](CONTRIBUTING.md)、[社区行为准则](CODE_OF_CONDUCT.md)和 [UI 样式契约](UI_STYLE_CONTRACT.md)。安全问题请按照[安全策略](SECURITY.md)私下报告。

## 许可证

Copyright © 2026 Qionline。

本项目采用 [GNU General Public License v3.0 or later](LICENSE)。第三方软件、数据及视觉资产声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
