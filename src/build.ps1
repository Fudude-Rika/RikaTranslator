param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\RikaTranslator-0.7.4-win-x64'),
    [string]$Version = '0.7.4',
    [switch]$NoRestore,
    [switch]$FlatLayout,
    [switch]$LooseHookSource
)
$ErrorActionPreference = 'Stop'
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $OutputDirectory) -and (Get-ChildItem -LiteralPath $OutputDirectory | Select-Object -First 1)) { throw '发布目录不为空，请使用新目录，避免残留旧 DLL。' }
$publishDirectory = if ($FlatLayout) { $OutputDirectory } else { Join-Path $PSScriptRoot ('obj\publish-flat-' + [guid]::NewGuid().ToString('N')) }
$buildArguments = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:DebugType=None', '-p:DebugSymbols=false', "-p:Version=$Version")
if ($NoRestore) { $buildArguments += '--no-restore' }
dotnet publish (Join-Path $PSScriptRoot 'GameTranslateToolkit.csproj') @buildArguments -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw '主程序构建失败。' }
$uiIndexPath = Join-Path $publishDirectory 'wwwroot\index.html'
$uiIndex = [IO.File]::ReadAllText($uiIndexPath)
$uiIndex = [regex]::Replace($uiIndex, '(\?v=)[0-9]+\.[0-9]+\.[0-9]+', '${1}' + $Version)
$uiIndex = [regex]::Replace($uiIndex, '(<small id="version">v)[0-9]+\.[0-9]+\.[0-9]+', '${1}' + $Version)
[IO.File]::WriteAllText($uiIndexPath, $uiIndex, [Text.UTF8Encoding]::new($false))
$updaterDirectory = Join-Path $PSScriptRoot 'updater\bin\publish'
dotnet publish (Join-Path $PSScriptRoot 'updater\RikaUpdater.csproj') @buildArguments -o $updaterDirectory
if ($LASTEXITCODE -ne 0) { throw '独立更新程序构建失败。' }
$hookDirectory = Join-Path $PSScriptRoot ('obj\hook-publish-' + [guid]::NewGuid().ToString('N'))
dotnet publish (Join-Path $PSScriptRoot 'tools\HookWorker\HookWorker.csproj') @buildArguments -o $hookDirectory
if ($LASTEXITCODE -ne 0) { throw '独立 Hook 程序构建失败。' }
foreach ($file in Get-ChildItem -LiteralPath $hookDirectory -File) {
    if ($file.Name -eq 'RikaHookWorker.exe') { continue }
    $target = Join-Path $publishDirectory $file.Name
    if ($file.Name.StartsWith('RikaHookWorker.') -or -not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $file.FullName -Destination $target -Force }
}
foreach ($file in Get-ChildItem -LiteralPath $updaterDirectory -File -Recurse) {
    $relative = $file.FullName.Substring($updaterDirectory.TrimEnd('\').Length + 1)
    $target = Join-Path $publishDirectory $relative
    if ($relative.StartsWith('RikaUpdater.') -or -not (Test-Path -LiteralPath $target)) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}
if (-not $FlatLayout) {
    $runtimeDirectory = Join-Path $OutputDirectory 'runtime'
    [IO.Directory]::CreateDirectory($runtimeDirectory) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $publishDirectory) {
        if ($item.Name -in @('RikaTranslator.exe', 'RikaUpdater.exe')) { continue }
        $destination = if ($item.PSIsContainer -and $item.Name -in @('wwwroot', 'adapters', 'licenses', 'validation')) { $OutputDirectory } else { $runtimeDirectory }
        Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force
    }
    $dotnetDirectory = Split-Path (Get-Command dotnet).Source
    $hostTemplate = Get-ChildItem -LiteralPath (Join-Path $dotnetDirectory 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    $hostTemplatePath = Join-Path $hostTemplate.FullName 'runtimes\win-x64\native\apphost.exe'
    foreach ($name in @('RikaTranslator', 'RikaUpdater')) {
        dotnet run --project (Join-Path $PSScriptRoot 'tools\AppHostBuilder\AppHostBuilder.csproj') -c Release -- $hostTemplatePath (Join-Path $OutputDirectory ($name + '.exe')) ('runtime/' + $name + '.dll') (Join-Path $publishDirectory ($name + '.exe'))
        if ($LASTEXITCODE -ne 0) { throw '生成运行目录启动程序失败。' }
    }
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'program-layout.json'), '{"layout":"runtime","formatVersion":1}', [Text.UTF8Encoding]::new($false))
}
$dotnetDirectory = Split-Path (Get-Command dotnet).Source
$hostTemplate = Get-ChildItem -LiteralPath (Join-Path $dotnetDirectory 'packs\Microsoft.NETCore.App.Host.win-x64') -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
$hostTemplatePath = Join-Path $hostTemplate.FullName 'runtimes\win-x64\native\apphost.exe'
$hookExecutable = Join-Path $OutputDirectory 'adapters\galgame\RikaHookWorker.exe'
$hookManagedPath = if ($FlatLayout) { '../../RikaHookWorker.dll' } else { '../../runtime/RikaHookWorker.dll' }
dotnet run --project (Join-Path $PSScriptRoot 'tools\AppHostBuilder\AppHostBuilder.csproj') -c Release -- $hostTemplatePath $hookExecutable $hookManagedPath (Join-Path $hookDirectory 'RikaHookWorker.exe')
if ($LASTEXITCODE -ne 0) { throw '生成 Hook 启动程序失败。' }
$hookSourceFiles = @('HookWorker.csproj', 'Program.cs', 'COPYING', 'README.md') | ForEach-Object { Join-Path $PSScriptRoot ('tools\HookWorker\' + $_) }
if ($LooseHookSource) {
    $hookSourceDirectory = Join-Path $OutputDirectory 'licenses\galgame\RikaHookWorker-source'
    [IO.Directory]::CreateDirectory($hookSourceDirectory) | Out-Null
    Copy-Item -LiteralPath $hookSourceFiles -Destination $hookSourceDirectory -Force
} else {
    Compress-Archive -LiteralPath $hookSourceFiles -DestinationPath (Join-Path $OutputDirectory 'licenses\galgame\RikaHookWorker-source.zip') -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $OutputDirectory '使用说明.md') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY.md') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $OutputDirectory -Force
$releaseNotesPath = Join-Path $PSScriptRoot ("release-notes-$Version.md")
if (Test-Path -LiteralPath $releaseNotesPath) { Copy-Item -LiteralPath $releaseNotesPath -Destination (Join-Path $OutputDirectory '更新说明.md') -Force }
Write-Output "便携版已输出到：$OutputDirectory"
