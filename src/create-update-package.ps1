param(
    [Parameter(Mandatory)][string]$ProgramDirectory,
    [Parameter(Mandatory)][string]$OutputZip,
    [Parameter(Mandatory)][string]$ReleaseNotesFile,
    [string]$MinimumVersion = '0.4.0',
    [string]$LegacyDirectory
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
function Read-Sha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    $input = [IO.File]::OpenRead($Path)
    try { return [BitConverter]::ToString($algorithm.ComputeHash($input)).Replace('-', '').ToLowerInvariant() }
    finally { $input.Dispose(); $algorithm.Dispose() }
}
$ProgramDirectory = (Resolve-Path -LiteralPath $ProgramDirectory).Path
$OutputZip = [IO.Path]::GetFullPath($OutputZip)
if (Test-Path -LiteralPath $OutputZip) { throw '输出更新包已存在，请使用新的文件名。' }
if ($OutputZip.StartsWith($ProgramDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '更新包不能输出到程序目录内。' }
$version = (Get-Item -LiteralPath (Join-Path $ProgramDirectory 'RikaTranslator.exe')).VersionInfo
$targetVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart)"
if ([version]$targetVersion -lt [version]$MinimumVersion) { throw '目标版本不能低于最低版本。' }
$notes = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $ReleaseNotesFile).Path).Trim()
if ([string]::IsNullOrWhiteSpace($notes) -or $notes.Length -gt 20000) { throw '更新说明为空或过长。' }
$files = @()
foreach ($file in Get-ChildItem -LiteralPath $ProgramDirectory -File -Recurse | Sort-Object FullName) {
    if (-not $file.FullName.StartsWith($ProgramDirectory.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '发现越界文件。' }
    $relative = $file.FullName.Substring($ProgramDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
    if ($relative -match '^(data|updates|tests|font-fixes|bin|obj)/' -or $relative.StartsWith('.') -or $relative -match '\.(pdb|tmp)$') { continue }
    $checkedPath = $file.FullName
    while (-not [string]::IsNullOrEmpty($checkedPath)) {
        if (([IO.File]::GetAttributes($checkedPath) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "程序目录存在链接：$relative" }
        $checkedPath = [IO.Path]::GetDirectoryName($checkedPath)
    }
    $files += [ordered]@{ path = $relative; size = $file.Length; sha256 = (Read-Sha256 $file.FullName) }
}
$required = @('RikaTranslator.exe','RikaTranslator.dll','RikaTranslator.deps.json','RikaTranslator.runtimeconfig.json','RikaUpdater.exe','RikaUpdater.dll','RikaUpdater.deps.json','RikaUpdater.runtimeconfig.json','wwwroot/index.html','wwwroot/app.js','wwwroot/styles.css','hostpolicy.dll','hostfxr.dll','coreclr.dll','System.Private.CoreLib.dll')
$layout = if (Test-Path -LiteralPath (Join-Path $ProgramDirectory 'runtime\RikaTranslator.dll')) { 'runtime' } else { 'flat' }
if ($layout -eq 'runtime') {
    if ([version]$MinimumVersion -lt [version]'0.5.1') { throw '运行目录布局要求最低版本 0.5.1。' }
    $required = @($required | ForEach-Object { if ($_.EndsWith('.exe') -or $_.StartsWith('wwwroot/')) { $_ } else { 'runtime/' + $_ } })
}
foreach ($name in $required) { if ($name -notin $files.path) { throw "程序目录缺少必要文件：$name" } }
$removeFiles = @()
if ($LegacyDirectory) {
    if ($layout -ne 'runtime') { throw '仅运行目录布局可整理旧文件。' }
    $LegacyDirectory = (Resolve-Path -LiteralPath $LegacyDirectory).Path
    foreach ($file in Get-ChildItem -LiteralPath $LegacyDirectory -File -Recurse) {
        $relative = $file.FullName.Substring($LegacyDirectory.TrimEnd('\').Length + 1).Replace('\', '/')
        if (('runtime/' + $relative) -notin $files.path) { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '旧目录不能包含链接。' }
        $removeFiles += [ordered]@{path=$relative;size=$file.Length;sha256=(Read-Sha256 $file.FullName)}
    }
}
$manifest = [ordered]@{ formatVersion = 1; product = 'rika-translator'; version = $targetVersion; minimumVersion = $MinimumVersion; architecture = 'win-x64'; dataFormatVersion = 1; layout = $layout; releaseNotes = $notes; files = $files; removeFiles = $removeFiles }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($OutputZip)) | Out-Null
$stream = [IO.File]::Open($OutputZip, [IO.FileMode]::CreateNew)
try {
    $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $entry = $zip.CreateEntry('rika-update.json', [IO.Compression.CompressionLevel]::Optimal)
        $writer = [IO.StreamWriter]::new($entry.Open(), [Text.UTF8Encoding]::new($false))
        try { $writer.Write(($manifest | ConvertTo-Json -Depth 6)) } finally { $writer.Dispose() }
        foreach ($file in $files) {
            $entry = $zip.CreateEntry(('payload/' + $file.path), [IO.Compression.CompressionLevel]::Optimal)
            $input = [IO.File]::OpenRead((Join-Path $ProgramDirectory $file.path))
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $zip.Dispose() }
} finally { $stream.Dispose() }
Write-Output "本地更新包：$OutputZip"
Write-Output "目标版本：$targetVersion；最低版本：$MinimumVersion；程序文件：$($files.Count)"
Write-Output "SHA-256：$(Read-Sha256 $OutputZip)"
