[CmdletBinding()]
param(
    [string]$SdkPath = (Join-Path $PSScriptRoot '../../CJ-SDK'),
    [string]$UnityPath = 'F:/Unity/Editor/2022.3.43f1/Editor/Unity.exe',
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/assets'),
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
$repoPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkFullPath = (Resolve-Path -LiteralPath $SdkPath).Path
$outputFullPath = [IO.Path]::GetFullPath($OutputPath)
if (!(Test-Path -LiteralPath $UnityPath)) { throw "Unity 2022.3.43f1 was not found: $UnityPath" }
$versionFile = Join-Path $sdkFullPath 'ProjectSettings/ProjectVersion.txt'
if ((Get-Content -LiteralPath $versionFile -Raw) -notmatch 'm_EditorVersion: 2022\.3\.43f1') { throw 'CJ-SDK must use Unity 2022.3.43f1.' }
$unityProcesses = Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction SilentlyContinue
if ($unityProcesses | Where-Object { !$_.CommandLine -or $_.CommandLine -like "*$sdkFullPath*" -or $_.CommandLine -like '*CJ-SDK*' }) {
    throw 'CJ-SDK is open in Unity. Close it yourself before building DebugPlus assets; the build never stops an editor.'
}
$unityLockPath = Join-Path $sdkFullPath 'Temp/UnityLockfile'
if (Test-Path -LiteralPath $unityLockPath) {
    try { $probe = [IO.File]::Open($unityLockPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None); $probe.Dispose() }
    catch { throw 'CJ-SDK is locked by Unity. Close it yourself before building DebugPlus assets.' }
}
$isolatedPath = Join-Path $sdkFullPath 'Assets/Mods/DebugPlus.Workbench.Assets'
$editorPath = Join-Path $isolatedPath 'Editor'
New-Item -ItemType Directory -Force -Path $isolatedPath, $editorPath, $outputFullPath | Out-Null
foreach ($name in @('Workbench.uxml', 'Workbench.uss', 'Workbench.tss')) {
    Copy-Item -LiteralPath (Join-Path $repoPath "DebugPlus/UI/Assets/$name") -Destination (Join-Path $isolatedPath $name) -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'unity/WorkbenchAssetBuilder.cs') -Destination (Join-Path $editorPath 'WorkbenchAssetBuilder.cs') -Force
Copy-Item -LiteralPath (Join-Path $repoPath 'DebugPlus/UI/ItemGridView.cs') -Destination (Join-Path $editorPath 'ItemGridView.cs') -Force
Copy-Item -LiteralPath (Join-Path $repoPath 'DebugPlus/UI/LoggingView.cs') -Destination (Join-Path $editorPath 'LoggingView.cs') -Force
Copy-Item -LiteralPath (Join-Path $repoPath 'DebugPlus/UI/WorkbenchScrolling.cs') -Destination (Join-Path $editorPath 'WorkbenchScrolling.cs') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'unity/DebugPlus.Workbench.AssetBuilder.asmdef') -Destination (Join-Path $editorPath 'DebugPlus.Workbench.AssetBuilder.asmdef') -Force
$logPath = Join-Path $outputFullPath 'unity-build.log'
$arguments = @('-batchmode', '-quit', '-projectPath', ('"' + $sdkFullPath + '"'), '-executeMethod', 'DebugPlus.AssetBuild.WorkbenchAssetBuilder.Build', '-debugPlusOutput', ('"' + $outputFullPath + '"'), '-logFile', ('"' + $logPath + '"'))
if ($Preview) { $arguments += '-debugPlusPreview' } else { $arguments += '-nographics' }
$process = Start-Process -FilePath $UnityPath -ArgumentList $arguments -PassThru -WindowStyle Hidden
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity asset build failed with exit code $($process.ExitCode). See $logPath" }
$bundlePath = Join-Path $outputFullPath 'debugplus.workbench'
if (!(Test-Path -LiteralPath $bundlePath)) { throw "Unity exited without creating $bundlePath. See $logPath" }
$sources = @('DebugPlus/UI/Assets/Workbench.uxml','DebugPlus/UI/Assets/Workbench.uss','DebugPlus/UI/Assets/Workbench.tss','DebugPlus/UI/ItemGridView.cs','DebugPlus/UI/LoggingView.cs','DebugPlus/UI/WorkbenchScrolling.cs','tools/unity/WorkbenchAssetBuilder.cs','tools/unity/DebugPlus.Workbench.AssetBuilder.asmdef')
$sourceManifest = [pscustomobject]@{
    UnityVersion = '2022.3.43f1'
    Version = (Get-Content -LiteralPath (Join-Path $repoPath 'DebugPlus/VERSION.txt') -Raw).Trim()
    BundleSHA256 = (Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash
    Sources = @($sources | ForEach-Object { [pscustomobject]@{ Path=$_; SHA256=(Get-FileHash -LiteralPath (Join-Path $repoPath $_) -Algorithm SHA256).Hash } })
}
$sourceManifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $outputFullPath 'source-manifest.json')
Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256
Write-Output "Built isolated DebugPlus UI assets: $bundlePath"
