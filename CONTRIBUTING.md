# 贡献指南

感谢你考虑为 Screw Calendar 做贡献。

参与项目即表示你同意遵守 [社区行为准则](CODE_OF_CONDUCT.md)。

## 开始之前

- 目标平台为 Windows 10/11 x64。
- 安装 .NET 8 SDK。
- UI 修改前必须完整阅读 `UI_STYLE_CONTRACT.md`。
- 不要提交 `bin`、`obj`、`artifacts`、用户日历数据、日志或本机配置。

## 本地验证

```powershell
dotnet restore .\ScrewCalendar.sln --configfile .\NuGet.Config
dotnet run --project .\tests\ScrewCalendar.Tests\ScrewCalendar.Tests.csproj -c Release --no-restore
dotnet build .\ScrewCalendar.csproj -c Release --no-restore
dotnet format .\ScrewCalendar.sln --verify-no-changes --no-restore
```

## Pull Request 要求

- 一个 Pull Request 聚焦一个明确问题。
- 用户可见文本同时更新 `locales/zh-CN.json` 和 `locales/en-US.json`。
- 新依赖必须说明用途、许可证和替代方案。
- 行为或数据格式变更必须更新测试与文档。
- UI 变更必须按 `UI_STYLE_CONTRACT.md` 完成回归检查。
- 不得包含真实用户数据、截图中的隐私信息或密钥。

提交 Pull Request 即表示你有权贡献该内容，并同意该贡献按本仓库的 GPL-3.0-or-later 许可证发布。
