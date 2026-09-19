# Screw Calendar 发布指南

项目版本遵循语义化版本。应用版本在 `ScrewCalendar.csproj` 中统一维护，Git 标签使用 `v<版本号>` 格式，例如 `v0.1.0`。

## 分支约定

- `main` 始终保持可发布，只接受通过自动检查的 Pull Request。
- 日常功能使用 `feature/<名称>`，普通修复使用 `fix/<名称>`。
- 正式版本冻结后，从最新 `main` 创建 `release/<版本号>`，例如 `release/1.0.0`；该分支只接受版本、文档和阻断发布的问题修复。
- 紧急补丁使用 `hotfix/<版本号>`，并在修复完成后合并回 `main`。
- 不维护长期 `develop` 分支。发布标签必须指向已经进入 `main` 的提交，Release 工作流会自动验证这一点。

## 发布前检查

1. 更新 `ScrewCalendar.csproj` 中的 `Version`；程序集版本和文件版本由 SDK 自动派生。
2. 把 `CHANGELOG.md` 中待发布的内容移动到带日期的版本标题下。
3. 确认已安装 `global.json` 指定的 .NET SDK。
4. 执行以下命令：

```powershell
dotnet restore .\ScrewCalendar.sln --configfile .\NuGet.Config --locked-mode
dotnet run --project .\tests\ScrewCalendar.Tests\ScrewCalendar.Tests.csproj -c Release --no-restore
dotnet format .\ScrewCalendar.sln --verify-no-changes --no-restore
dotnet build .\ScrewCalendar.csproj -c Release --no-restore
dotnet publish .\ScrewCalendar.csproj -c Release --no-restore -o .\artifacts\publish
```

5. 检查发布目录包含应用程序、语言文件、日历数据、`LICENSE`、`README.md` 和 `THIRD_PARTY_NOTICES.md`。
6. 手工回归 Windows 10/11、三种日历样式、明暗模式、主题色、托盘、开机启动、置顶/桌面层级、多显示器恢复、导入导出和 Markdown 图片。

## 创建发布

把发布分支通过 Pull Request 合并回 `main`，等待 `Build` 必需检查通过，然后在该合并提交上创建并推送与项目版本一致的标签：

```powershell
[xml]$project = Get-Content .\ScrewCalendar.csproj
$version = $project.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
git tag -a "v$version" -m "Screw Calendar $version"
git push origin "v$version"
```

`.github/workflows/release.yml` 会重新验证、生成 Windows x64 免安装压缩包和 SHA-256 文件，并创建 GitHub Release。GitHub 自动附带该标签对应的源代码归档。

正式 1.0 版本使用 `1.0.0` 和 `v1.0.0`，不要简写为 `1.0` 或 `v1.0`。应用版本与 `CalendarState.Version` 数据格式版本彼此独立；只有持久化 JSON 结构发生不兼容变化时才增加数据格式版本。

若工作流失败，不要手工替换同名发布文件。修复问题、删除尚未公开使用的错误标签，再从经过验证的提交重新创建标签。
