# DesktopWidgets.Toolkit

`DesktopWidgets.Toolkit` 是 Screw Calendar 内部使用的 WPF 桌面挂件基础库，目前随主仓库共同维护，不作为独立 NuGet 包发布。

## 提供的能力

- `SingleInstanceGuard`：基于命名互斥体限制单实例运行。
- `TrayIconService`：创建和管理系统托盘图标与菜单。
- `StartupRegistration`：管理当前用户的 Windows 开机启动项。
- `WindowLayerController`：管理始终置顶和普通窗口下方的桌面层级，并支持编辑输入时临时激活桌面层窗口。
- `WindowPlacementService`：保存显示器、位置和大小，并在显示器断开时恢复到可见区域。
- `DialogWindowBehavior`：统一子窗口所有者、位置和激活行为。
- `ApplicationIconLoader`：加载应用图标。
- `Themes/`：共享控件模板和默认色板。

## 引用

WPF 项目可以通过项目引用使用工具包：

```xml
<ProjectReference Include="Shared\DesktopWidgets.Toolkit\DesktopWidgets.Toolkit.csproj" />
```

应用需要启用 Windows 桌面目标框架。托盘服务依赖 Windows Forms，因此使用方还需要启用 `UseWindowsForms`。

## 维护约定

- 只放置可供多个桌面挂件复用的 Windows 行为和控件资源。
- 日历模型、日历数据及业务界面保留在 Screw Calendar 主项目中。
- 公开类型发生行为变化时，应同步更新本文件、主项目开发文档和相应回归测试。

该工具包与 Screw Calendar 一同采用仓库根目录的 GPL-3.0-or-later 许可证。
