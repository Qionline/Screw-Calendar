[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$Remote = 'origin',

    [switch]$PushTag
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
Set-Location -LiteralPath $repositoryRoot

$solutionPath = Join-Path $repositoryRoot 'ScrewCalendar.sln'
$projectPath = Join-Path $repositoryRoot 'ScrewCalendar.csproj'
$nugetConfigPath = Join-Path $repositoryRoot 'NuGet.Config'
$publishPath = Join-Path $repositoryRoot 'artifacts\publish'
$tag = "v$Version"
$releaseSdkVersion = '8.0.425'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    Write-Host "> $Command $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "命令失败（退出码 $LASTEXITCODE）：$Command $($Arguments -join ' ')"
    }
}

function Get-CommandOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $output = & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "命令失败（退出码 $LASTEXITCODE）：$Command $($Arguments -join ' ')"
    }

    return (($output -join [Environment]::NewLine).Trim())
}

foreach ($requiredCommand in @('git', 'dotnet')) {
    if (-not (Get-Command $requiredCommand -ErrorAction SilentlyContinue)) {
        throw "找不到 $requiredCommand。请先安装并配置它，再运行发布脚本。"
    }
}

$repositoryFromGit = Get-CommandOutput 'git' @('rev-parse', '--show-toplevel')
if ([IO.Path]::GetFullPath($repositoryFromGit) -ne [IO.Path]::GetFullPath($repositoryRoot)) {
    throw "脚本必须在 Screw-Calendar 仓库中运行。"
}

$branch = Get-CommandOutput 'git' @('branch', '--show-current')
if ($branch -ne 'main') {
    throw "发布标签必须从 main 创建；当前分支是 $branch。"
}

$workingTree = & git status --porcelain
if ($LASTEXITCODE -ne 0) {
    throw '无法读取 Git 工作区状态。'
}
if ($workingTree) {
    throw "工作区存在未提交变更，请先提交或清理：`n$($workingTree -join [Environment]::NewLine)"
}

Write-Host "同步 $Remote/main 以确认发布提交已经进入主分支。" -ForegroundColor Cyan
Invoke-CheckedCommand 'git' @('fetch', $Remote, 'main', '--no-tags')
$head = Get-CommandOutput 'git' @('rev-parse', 'HEAD')
$remoteHead = Get-CommandOutput 'git' @('rev-parse', "$Remote/main")
if ($head -ne $remoteHead) {
    throw "当前 main 不是 $Remote/main 的最新提交。请先拉取或合并后再发布。"
}

[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$projectVersion = [string]($project.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if ($projectVersion -ne $Version) {
    throw "ScrewCalendar.csproj 的 Version 是 $projectVersion，而脚本参数是 $Version。"
}

$changelog = Get-Content -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Raw
if ($changelog -notmatch [regex]::Escape("[$Version]")) {
    throw "CHANGELOG.md 中没有 [$Version] 版本条目。"
}

$localTag = & git show-ref --verify --quiet "refs/tags/$tag"
$localTagExists = $LASTEXITCODE -eq 0
if ($localTagExists) {
    $tagCommit = Get-CommandOutput 'git' @('rev-parse', "$tag`^{commit}")
    if ($tagCommit -ne $head) {
        throw "本地标签 $tag 已存在，但没有指向当前 main 提交。"
    }
}

$remoteTagOutput = & git ls-remote --exit-code --tags $Remote "refs/tags/$tag" 2>$null
$remoteTagExitCode = $LASTEXITCODE
if ($remoteTagExitCode -eq 0 -and $remoteTagOutput) {
    throw "远端标签 $tag 已存在。发布新版本请使用新的语义化版本号。"
}
if ($remoteTagExitCode -notin @(2, 0)) {
    throw "无法确认远端标签状态，请检查 $Remote 连接。"
}

$installedSdk = Get-CommandOutput 'dotnet' @('--version')
if ($installedSdk -ne $releaseSdkVersion) {
    throw "发布验证要求 .NET SDK $releaseSdkVersion，当前是 $installedSdk。请安装该 SDK 后重试。"
}

Write-Host '开始执行与 GitHub Actions 相同的发布前检查。' -ForegroundColor Cyan
Invoke-CheckedCommand 'dotnet' @('restore', $solutionPath, '--configfile', $nugetConfigPath, '--locked-mode')
Invoke-CheckedCommand 'dotnet' @('run', '--project', 'tests\ScrewCalendar.Tests\ScrewCalendar.Tests.csproj', '-c', 'Release', '--no-restore', '--disable-build-servers')
Invoke-CheckedCommand 'dotnet' @('format', $solutionPath, '--verify-no-changes', '--no-restore')
Invoke-CheckedCommand 'dotnet' @('build', $projectPath, '-c', 'Release', '--no-restore', '--disable-build-servers')

if (Test-Path -LiteralPath $publishPath) {
    Remove-Item -LiteralPath $publishPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
Invoke-CheckedCommand 'dotnet' @('publish', $projectPath, '-c', 'Release', '--no-restore', '-o', $publishPath)

$requiredFiles = @(
    'ScrewCalendar.exe',
    'LICENSE',
    'README.md',
    'THIRD_PARTY_NOTICES.md',
    'locales\zh-CN.json',
    'locales\en-US.json',
    'Resources\Data\calendar-data.json'
)
$missingFiles = $requiredFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publishPath $_)) }
if ($missingFiles) {
    throw "发布目录缺少文件：$($missingFiles -join ', ')"
}

if (-not $PushTag) {
    Write-Host "验证通过。确认发布后运行：.\scripts\Release.ps1 -Version $Version -PushTag" -ForegroundColor Green
    return
}

if (-not $localTagExists) {
    Invoke-CheckedCommand 'git' @('tag', '-a', $tag, '-m', "Screw Calendar $Version")
}
Invoke-CheckedCommand 'git' @('push', $Remote, $tag)
Write-Host "标签 $tag 已推送。GitHub Actions 将自动生成 Release 和 SHA256 文件。" -ForegroundColor Green
