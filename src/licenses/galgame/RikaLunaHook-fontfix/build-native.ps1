param(
    [Parameter(Mandatory)][ValidateSet('x86', 'x64')][string]$Architecture,
    [string]$BuildRoot = (Join-Path ([IO.Path]::GetTempPath()) ('RikaLunaHook-fontfix-' + [guid]::NewGuid().ToString('N'))),
    [string]$MinHookSourceDirectory = (Join-Path $PSScriptRoot 'minhook-source'),
    [string]$CMakeExecutable = 'cmake',
    [string]$NinjaExecutable = 'ninja',
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'patch-manifest.json') -Raw | ConvertFrom-Json
$archive = Join-Path (Split-Path $PSScriptRoot) 'LunaTranslator-v12.0.1-source.zip'
$replacement = Join-Path $PSScriptRoot 'hijackfuns.cc'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $metadata.upstreamSourceSha256) { throw '上游源码包 SHA-256 不符。' }
if ((Get-FileHash -LiteralPath $replacement -Algorithm SHA256).Hash -ne $metadata.modifiedFileSha256) { throw '修复源码 SHA-256 不符。' }
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if ((Test-Path -LiteralPath $BuildRoot) -and (Get-ChildItem -LiteralPath $BuildRoot | Select-Object -First 1)) { throw '构建目录必须为空，以保留已有文件。' }
[IO.Directory]::CreateDirectory($BuildRoot) | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $BuildRoot
$source = Join-Path $BuildRoot 'LunaTranslator-12.0.1'
$file = Join-Path $source $metadata.file
if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $metadata.originalFileSha256) { throw '上游字体源码与 v12.0.1 基线不符。' }
Copy-Item -LiteralPath $replacement -Destination $file -Force
if ($PrepareOnly) {
    [pscustomobject]@{ status = 'source-prepared'; source = $source; modifiedFileSha256 = (Get-FileHash -LiteralPath $file).Hash } | ConvertTo-Json
    return
}

if (-not (Get-Command cl.exe -ErrorAction SilentlyContinue)) { throw '完整构建需要 MSVC 开发命令环境；当前只有源码准备结果。' }
if ($env:VSCMD_ARG_TGT_ARCH -ne $Architecture) { throw "请选择 $Architecture 的 MSVC 开发命令环境。" }
if (-not $MinHookSourceDirectory) { throw '请指定 MinHook v1.3.4 的已解压源码目录。' }
$MinHookSourceDirectory = [IO.Path]::GetFullPath($MinHookSourceDirectory)
if (-not (Test-Path -LiteralPath (Join-Path $MinHookSourceDirectory 'include\MinHook.h'))) { throw 'MinHook 源码目录不完整。' }
$cmake = (Get-Command $CMakeExecutable -ErrorAction Stop).Source
$ninja = (Get-Command $NinjaExecutable -ErrorAction Stop).Source
$nativeSource = Join-Path $source 'src\NativeImpl\LunaHook'
$buildDirectory = Join-Path $BuildRoot ('build-' + $Architecture)
& $cmake -S $nativeSource -B $buildDirectory -G Ninja '-DCMAKE_BUILD_TYPE=Release' '-DBUILD_HOST=OFF' '-DBUILD_HOOK=ON' '-DWIN10ABOVE=ON' '-DUSE_VC_LTL=OFF' '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded' "-DCMAKE_MAKE_PROGRAM=$ninja" "-DFETCHCONTENT_SOURCE_DIR_MINHOOK=$MinHookSourceDirectory"
if ($LASTEXITCODE -ne 0) { throw 'CMake 配置失败。' }
& $cmake --build $buildDirectory --target LunaHook --parallel 6
if ($LASTEXITCODE -ne 0) { throw 'LunaHook 完整构建失败。' }
$bits = if ($Architecture -eq 'x86') { '32' } else { '64' }
$builtFile = Join-Path $nativeSource ("builds\Release_win10\LunaHook$bits.dll")
$output = Join-Path $BuildRoot 'output'
[IO.Directory]::CreateDirectory($output) | Out-Null
Copy-Item -LiteralPath $builtFile -Destination $output
$hash = Get-FileHash -LiteralPath $builtFile -Algorithm SHA256
[pscustomobject]@{ status = 'built-not-deployed'; file = (Join-Path $output "LunaHook$bits.dll"); sha256 = $hash.Hash; size = (Get-Item -LiteralPath $builtFile).Length } | ConvertTo-Json
