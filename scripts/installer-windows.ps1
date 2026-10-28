param(
    [string]$Version,
    [ValidateNotNullOrEmpty()]
    [string]$OutputDirectory = 'artifacts',
    [string]$IsccPath
)

# Builds the Windows release: publishes the self-contained app into a staging
# directory, compiles the Inno Setup installer, and keeps only the setup EXE and
# its SHA256 file. The staging directory is temporary and always removed.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $repositoryRoot 'src/LoLKarmaBook.Desktop/LoLKarmaBook.Desktop.csproj'
$fixturePath = Join-Path $repositoryRoot 'fixtures/scoring/performance-demo.json'
$installerScript = Join-Path $repositoryRoot 'packaging/windows/installer.iss'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
    $versionNode = $project.SelectSingleNode('/Project/PropertyGroup/Version')
    if ($null -eq $versionNode) { throw 'The desktop project does not define a Version.' }
    $Version = $versionNode.InnerText.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw 'Version must be a three-part version with optional prerelease/build metadata.'
}
$numericVersion = ($Version -split '[-+]')[0] + '.0'

function Resolve-RepositoryPath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repositoryRoot $Path))
}

$fixture = Get-Content -LiteralPath $fixturePath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($fixture.schemaVersion -ne 1 -or [string]::IsNullOrWhiteSpace($fixture.source)) {
    throw 'The packaged performance example must use the format accepted by PerformanceJson.'
}

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $isccCommand = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $isccCommand) { $IsccPath = $isccCommand.Source }
    else {
        foreach ($programFilesRoot in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
            if ([string]::IsNullOrWhiteSpace($programFilesRoot)) { continue }
            foreach ($major in @(7, 6)) {
                $candidate = Join-Path $programFilesRoot "Inno Setup $major/ISCC.exe"
                if (Test-Path -LiteralPath $candidate -PathType Leaf) { $IsccPath = $candidate; break }
            }
            if (-not [string]::IsNullOrWhiteSpace($IsccPath)) { break }
        }
    }
}
if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    throw 'Install Inno Setup 7 (or compatible Inno Setup 6 with ChineseSimplified.isl), then provide -IsccPath or add ISCC.exe to PATH.'
}
$compiler = (Resolve-Path -LiteralPath $IsccPath).Path
if (-not (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $compiler) 'Languages/ChineseSimplified.isl') -PathType Leaf)) {
    throw 'The Inno Setup compiler must include Languages/ChineseSimplified.isl.'
}

$outputRoot = Resolve-RepositoryPath $OutputDirectory
$baseName = 'LoLKarmaBook-' + $Version + '-windows-x64-setup'
$setupPath = Join-Path $outputRoot ($baseName + '.exe')
$checksumPath = $setupPath + '.sha256'
foreach ($destination in @($setupPath, $checksumPath)) {
    if (Test-Path -LiteralPath $destination) { throw "Refusing to overwrite existing installer output: $destination" }
}

# Keep the staging name short: Windows SQLite and the installer accept paths
# well below the limit, but a short root also avoids long-path surprises.
$stagingRoot = Join-Path ([IO.Path]::GetTempPath()) ('LoLKarmaBook-build-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
$packagePath = Join-Path $stagingRoot 'app'
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Push-Location $repositoryRoot
try {
    $dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    # Use dotnet from the current PATH and the repository SDK selection. Only
    # publish output is staged; no test folders, local app data or logs are copied.
    & $dotnet publish $projectPath -c Release -r win-x64 --self-contained true -m:1 -nodeReuse:false -p:UseSharedCompilation=false "-p:Version=$Version" -o $packagePath
    if ($LASTEXITCODE -ne 0) {
        throw "Windows publish failed with exit code $LASTEXITCODE. Staging kept for inspection: $stagingRoot"
    }
} finally {
    Pop-Location
}

foreach ($requiredFile in @(
    'LoLKarmaBook.exe', 'LoLKarmaBook.dll', 'LoLKarmaBook.Core.dll', 'LoLKarmaBook.Storage.dll', 'LoLKarmaBook.Scoring.dll', 'LoLKarmaBook.Client.dll', 'LoLKarmaBook.Riot.dll', 'LoLKarmaBook.Hexdata.dll',
    'LoLKarmaBook.deps.json', 'LoLKarmaBook.runtimeconfig.json', 'coreclr.dll', 'hostfxr.dll',
    'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll', 'e_sqlite3.dll'
)) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $requiredFile) -PathType Leaf)) {
        throw "The self-contained publish is incomplete: missing $requiredFile"
    }
}

# Also reject unexpected content copied by future project-file changes.
$unexpectedFiles = @(Get-ChildItem -LiteralPath $packagePath -Recurse -Force -File | Where-Object {
    $relativePath = $_.FullName.Substring($packagePath.Length).TrimStart('\', '/')
    $relativePath -match '(?i)(^|[\\/])(userdata|data|logs|backups|exports|TestResults)([\\/]|$)' -or
    $_.Name -match '(?i)(\.(db(?:-(?:shm|wal|journal))?|sqlite3?|log|pfx|p12|user)$|^\.env(?:\.|$)|^unins\d*\.)'
})
if ($unexpectedFiles.Count -gt 0) {
    throw ('Unexpected private/test content in publish output: ' + ($unexpectedFiles.Name -join ', '))
}

$productVersion = (Get-Item -LiteralPath (Join-Path $packagePath 'LoLKarmaBook.exe')).VersionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($productVersion) -or ($productVersion -split '\+')[0] -cne ($Version -split '\+')[0]) {
    throw "Published ProductVersion '$productVersion' does not match requested Version '$Version'."
}

# Ships inside the installer only; it is not kept as a loose file afterwards.
$utf8 = New-Object System.Text.UTF8Encoding($false)
$startHere = @"
LoLKarmaBook $Version

启动后自动连接本机 LOL 客户端，识别当前账号并持续更新真实对局；已有记录会在再次相遇时提醒。

1. 从开始菜单或桌面快捷方式启动 LoLKarmaBook。
   程序依赖同目录中的运行库文件，请使用安装器或卸载程序管理该目录，无需另装 .NET。
2. 保持 LOL 客户端已登录，在程序中查看连接账号、对局阶段和当前名单；必要时选择客户端目录或重新连接。
3. 选中玩家后保存类别、标签与备注，在「我的记录」中查找和维护。
4. 在「Hexdata 海斗分」登录后查询游戏 ID，查看查询历史、分数趋势和对局详情。
   对局详情中的玩家 ID 支持鼠标选中，再按 Ctrl+C 复制。
5. 在「设置 / 关于」中调整偏好、查看数据位置、导出快照或恢复备份。

数据默认保存在安装目录的 userdata 子文件夹（默认 D:\LoLKarmaBook\userdata）。
备注及稳定身份字段由当前 Windows 用户的 DPAPI 保护；备份只适用于同机同 Windows 账户，
不能跨电脑或账户恢复。类别、时间等元数据并非整库加密。

默认最小化和关闭窗口会收到系统托盘，实时检测继续运行；右键托盘图标可退出。
"@
[IO.File]::WriteAllText((Join-Path $packagePath 'START-HERE.zh-CN.txt'), $startHere, $utf8)

$fixtureDirectory = Join-Path $packagePath 'fixtures/scoring'
New-Item -ItemType Directory -Path $fixtureDirectory | Out-Null
Copy-Item -LiteralPath $fixturePath -Destination (Join-Path $fixtureDirectory 'performance-demo.json')

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
try {
    & $compiler "/DPackageDir=$packagePath" "/DInstallerOutputDir=$outputRoot" "/DAppVersion=$Version" "/DAppVersionNumber=$numericVersion" "/DInstallerBaseName=$baseName" $installerScript
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed with exit code $LASTEXITCODE. Staging kept for inspection: $stagingRoot" }
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw 'Inno Setup did not produce the expected EXE.' }
}
catch {
    Write-Host "Staging directory kept for inspection: $stagingRoot"
    throw
}

$resolvedStagingRoot = [IO.Path]::GetFullPath($stagingRoot)
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
if ([IO.Path]::GetDirectoryName($resolvedStagingRoot) -ine $temporaryRoot -or
    [IO.Path]::GetFileName($resolvedStagingRoot) -notmatch '^LoLKarmaBook-build-[a-f0-9]{12}$') {
    throw "Refusing to remove an unexpected staging directory: $resolvedStagingRoot"
}
Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($checksumPath, $hash + '  ' + [IO.Path]::GetFileName($setupPath) + "`n", $utf8)
Write-Host "Installer: $setupPath"
Write-Host "SHA256: $checksumPath"
