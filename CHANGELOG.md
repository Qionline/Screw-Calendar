# Changelog

本项目的重要变化将记录在此文件中。

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [Semantic Versioning](https://semver.org/lang/zh-CN/)。

## [Unreleased]

## [1.0.1] - 2026-09-20

### Changed

- Markdown 待办编辑器改用单一 AvalonEdit 文档，支持稳定的跨行选区、撤销、复制粘贴和任务类型继承。
- 统一 Markdown 编辑器和预览区域的选区颜色、列表圆点和任务复选框样式。
- 增加一键发布前检查脚本，统一本地验证、发布 SDK 和 Git 标签推送流程。
- CI 与 Release 固定使用已验证的 .NET SDK 8.0.425，避免滚动 SDK 造成锁文件不一致。

### Fixed

- 修复包含图片、空任务项或多种 Markdown 块时编辑器聚焦、删除和输入可能导致闪退的问题。
- 修复编辑模式复选框尺寸裁切，以及浅色模式选区边框过深的问题。

## [1.0.0] - 2026-09-20

### Added

- 支持从 holiday-cn 静默更新中国法定节假日与调休数据，并在断网时回退到本地缓存或内置数据。
- 支持经典、极简、紧凑三种样式、主题色切换、托盘菜单和今日待办编辑入口。

### Changed

- 设置页按日历布局、外观、底部待办、窗口行为、语言和本机数据分组。
- 编辑待办窗口调整为约 324×403px，改善多行 Markdown 编辑空间。

### Fixed

- 今日待办编辑窗口打开时，底部今日待办区域暂停交互，避免复选框刷新造成窗口卡死。

## [0.1.0] - 2026-09-19

### Added

- Windows 10/11 x64 桌面日历，支持 5～10 行、按月翻页和今天定位。
- 经典、极简、紧凑样式，以及明暗模式、主题色和透明度。
- 节气、2025～2026 年中国法定节假日与调休信息。
- 常驻和按日期保存的 Markdown 待办、任务勾选与本机图片归档。
- 托盘、开机启动、窗口层级、位置大小锁定和多显示器恢复。
- 本机 JSON 数据、导入导出、自动备份与损坏恢复。
- 简体中文和英文界面。
- 开源仓库规范、自动化回归测试、持续集成和版本发布流程。

[Unreleased]: https://github.com/Qionline/Screw-Calendar/compare/v1.0.1...HEAD
[1.0.1]: https://github.com/Qionline/Screw-Calendar/releases/tag/v1.0.1
[1.0.0]: https://github.com/Qionline/Screw-Calendar/releases/tag/v1.0.0
[0.1.0]: https://github.com/Qionline/Screw-Calendar/releases/tag/v0.1.0
