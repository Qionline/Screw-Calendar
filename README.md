# Screw Calendar

Screw Calendar 是一个面向 Windows 10/11 的桌面日历与本机 Markdown 待办应用。

> 用户文档正在整理中，后续将补充完整的功能介绍、截图、下载与使用说明。

## 当前状态

- 支持 Windows 10/11 x64。
- 使用 .NET 8 和 WPF 开发。
- 用户数据仅保存在本机 `%LocalAppData%\ScrewCalendar`。
- 项目采用 GNU General Public License v3.0 or later。

## 文档

- [用户指南（待完善）](docs/USER_GUIDE.md)
- [开发、构建与项目规范](docs/DEVELOPMENT.md)
- [UI 样式契约](UI_STYLE_CONTRACT.md)
- [贡献指南](CONTRIBUTING.md)
- [社区行为准则](CODE_OF_CONDUCT.md)
- [安全策略](SECURITY.md)
- [第三方软件与数据声明](THIRD_PARTY_NOTICES.md)

## 快速构建

```powershell
dotnet restore .\ScrewCalendar.sln --configfile .\NuGet.Config
dotnet run --project .\tests\ScrewCalendar.Tests\ScrewCalendar.Tests.csproj -c Release --no-restore
dotnet build .\ScrewCalendar.csproj -c Release --no-restore
```

完整开发流程请阅读 [开发文档](docs/DEVELOPMENT.md)。

## 许可证

Copyright © 2026 Qionline。

本项目使用 [GNU General Public License v3.0 or later](LICENSE)。第三方组件与数据使用各自许可证，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
