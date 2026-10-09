[CmdletBinding()]
param(
    [ValidateSet('Build','Check','Package','Install')][string]$Action = 'Build',
    [string]$GameRoot = (Join-Path $PSScriptRoot '../../..'),
    [string]$HeadlessRoot = '',
    [switch]$BuildAssets,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game = [IO.Path]::GetFullPath($GameRoot)
$version = (Get-Content -LiteralPath (Join-Path $repo 'DebugPlus/VERSION.txt') -Raw).Trim()
$assemblyVersion = [version]::Parse($version + '.0')
if (!(Test-Path -LiteralPath (Join-Path $game 'EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll'))) { throw 'GameRoot is not an EFT installation.' }
Push-Location $repo
try {
    if ($BuildAssets) { & (Join-Path $PSScriptRoot 'Build-Assets.ps1'); if (!$?) { throw 'Asset build failed.' } }
    if (!$SkipBuild) { dotnet build DebugPlus.sln -c Release --nologo -v:minimal -p:DeployDebugPlus=false -p:PackageDebugPlus=false "-p:TarkovDir=$game/"; if ($LASTEXITCODE) { throw 'Build failed.' } }
    if ($Action -eq 'Build') { return }
    if ($Action -eq 'Check') {
        & (Join-Path $PSScriptRoot 'Check-LoggingHooks.ps1') -GameRoot $game
        foreach ($test in @('Tests/DebugPlus.Checks.csproj','Tests/NativeCapture/NativeCapture.csproj','DebugPlus.Server/Tests/DebugPlus.Server.Tests.csproj','DebugPlus.Fika/Tests/WireChecks.csproj')) {
            dotnet run --project $test -c Release -- (Join-Path $game 'SPT_Runtime')
            if ($LASTEXITCODE) { throw "Check failed: $test" }
        }
        return
    }
    $assets = Join-Path $repo 'artifacts/assets/debugplus.workbench'
    if (!(Test-Path -LiteralPath $assets)) { throw 'Build the workbench bundle with tools/Build-Assets.ps1 first.' }
    $provenancePath = Join-Path $repo 'artifacts/assets/source-manifest.json'
    if (!(Test-Path -LiteralPath $provenancePath)) { throw 'Build UI assets to record their matching source manifest before packaging.' }
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json
    if ($provenance.Version -ne $version -or $provenance.UnityVersion -ne '2022.3.43f1' -or $provenance.BundleSHA256 -ne (Get-FileHash -LiteralPath $assets -Algorithm SHA256).Hash) { throw 'Workbench bundle version or hash mismatch; rebuild assets.' }
    foreach ($source in $provenance.Sources) {
        if ((Get-FileHash -LiteralPath (Join-Path $repo $source.Path) -Algorithm SHA256).Hash -ne $source.SHA256) { throw "UI source changed since asset build: $($source.Path)" }
    }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
    $package = Join-Path $repo "artifacts/packages/DebugPlus-$version-$stamp"
    $fikaPackage = Join-Path $repo "artifacts/packages/DebugPlus.Fika-$version-$stamp"
    $clientRelative = 'BepInEx/plugins/DebugPlus'
    $serverRelative = 'SPT_Runtime/user/mods/DebugPlus'
    $client = Join-Path $package $clientRelative
    $server = Join-Path $package $serverRelative
    $fikaClient = Join-Path $fikaPackage $clientRelative
    New-Item -ItemType Directory -Force -Path $client,$server,$fikaClient | Out-Null
    Copy-Item -LiteralPath 'DebugPlus/bin/Release/netstandard2.1/DebugPlus.dll' -Destination $client
    Copy-Item -LiteralPath 'DebugPlus.Common/bin/Release/netstandard2.1/DebugPlus.Common.dll' -Destination $client
    Copy-Item -LiteralPath 'DebugPlus.Fika/bin/Release/netstandard2.1/DebugPlus.Fika.dll' -Destination $fikaClient
    Copy-Item -LiteralPath $assets -Destination (Join-Path $client 'debugplus.workbench')
    Copy-Item -LiteralPath 'DebugPlus.Server/bin/Release/net10.0/DebugPlus.Server.dll' -Destination $server
    Copy-Item -LiteralPath 'DebugPlus.Common/bin/Release/netstandard2.1/DebugPlus.Common.dll' -Destination $server
    # Keep build evidence beside staging, outside the runtime-only release archives.
    Copy-Item -LiteralPath $provenancePath -Destination ($package + '.asset-source-manifest.json')
    foreach ($assembly in Get-ChildItem -LiteralPath $package,$fikaPackage -Recurse -Filter 'DebugPlus*.dll') {
        if ([Reflection.AssemblyName]::GetAssemblyName($assembly.FullName).Version -ne $assemblyVersion) { throw "Component version mismatch: $($assembly.Name); expected $assemblyVersion" }
    }
    $packages = @(
        [pscustomobject]@{ Name='DebugPlus'; Root=$package },
        [pscustomobject]@{ Name='DebugPlus.Fika'; Root=$fikaPackage }
    )
    $manifest = [Collections.Generic.List[object]]::new()
    foreach ($component in $packages) {
        $componentManifest = @(Get-ChildItem -LiteralPath $component.Root -File -Recurse | ForEach-Object {
            [pscustomobject]@{ Path=[IO.Path]::GetRelativePath($component.Root,$_.FullName); SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash; Source=$_.FullName }
        })
        ConvertTo-Json -InputObject @($componentManifest | Select-Object Path,SHA256) | Set-Content -LiteralPath ($component.Root + '.manifest.json')
        foreach ($entry in $componentManifest) { $manifest.Add($entry) }
        Compress-Archive -Path (Join-Path $component.Root '*') -DestinationPath ($component.Root + '.zip')
    }
    # Publish only after staging and validation succeed, preserving the previous release on failure.
    $releaseDirectory = Join-Path $repo 'release'
    New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
    foreach ($component in $packages) {
        $releaseZip = Join-Path $releaseDirectory "$($component.Name)-$version-Release.zip"
        Copy-Item -LiteralPath ($component.Root + '.zip') -Destination $releaseZip -Force
        Write-Output "Package: $releaseZip"
    }
    if ($Action -ne 'Install') { return }
    $installs = [Collections.Generic.List[object]]::new()
    foreach ($entry in $manifest | Where-Object { $_.Path.StartsWith('BepInEx') -or $_.Path.StartsWith('SPT_Runtime') }) {
        if ($entry.Path.EndsWith('DebugPlus.Fika.dll') -and !(Test-Path -LiteralPath (Join-Path $game 'BepInEx/plugins/Fika/Fika.Core.dll'))) { continue }
        $installs.Add([pscustomobject]@{ Source=$entry.Source; Target=Join-Path $game $entry.Path; Hash=$entry.SHA256 })
    }
    $roots = @($game)
    if ($HeadlessRoot) {
        $headless = [IO.Path]::GetFullPath($HeadlessRoot)
        if (!(Test-Path -LiteralPath (Join-Path $headless 'BepInEx/plugins/Fika/Fika.Core.dll'))) { throw 'HeadlessRoot is not a Fika installation.' }
        $roots += $headless
        foreach ($entry in $manifest | Where-Object { $_.Path.StartsWith('BepInEx') -and $_.Path.EndsWith('.dll') }) {
            $installs.Add([pscustomobject]@{ Source=$entry.Source; Target=Join-Path $headless $entry.Path; Hash=$entry.SHA256 })
        }
    }
    # Probe every destination before changing any installed component.
    $existing = @($installs | ForEach-Object Target) + @($roots | ForEach-Object { Join-Path $_ 'BepInEx/plugins/DebugPlus.dll' })
    foreach ($path in $existing | Where-Object { Test-Path -LiteralPath $_ }) {
        try { $probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose() }
        catch { throw "Installation is locked. Close the owning application yourself and retry: $path" }
    }
    $backup = Join-Path $repo "artifacts/install-backups/$stamp"
    New-Item -ItemType Directory -Force -Path $backup | Out-Null
    $changed = [Collections.Generic.List[object]]::new()
    try {
        foreach ($entry in $installs) {
            $saved = $null
            if (Test-Path -LiteralPath $entry.Target) { $saved=Join-Path $backup ([guid]::NewGuid().ToString('N')+'.bak'); Copy-Item -LiteralPath $entry.Target -Destination $saved }
            $changed.Add([pscustomobject]@{Target=$entry.Target;Backup=$saved})
            New-Item -ItemType Directory -Force -Path (Split-Path $entry.Target) | Out-Null
            Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
            if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.Hash) { throw "Hash mismatch: $($entry.Target)" }
        }
        foreach ($root in $roots) {
            $legacy = Join-Path $root 'BepInEx/plugins/DebugPlus.dll'
            if (Test-Path -LiteralPath $legacy) {
                $saved=Join-Path $backup ([guid]::NewGuid().ToString('N')+'-legacy.dll.bak')
                Move-Item -LiteralPath $legacy -Destination $saved
                $changed.Add([pscustomobject]@{Target=$legacy;Backup=$saved})
            }
        }
        $changed | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'restore-map.json')
        $installs | Select-Object Target,Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo 'artifacts/installed-manifest.json')
        Write-Output "Installed and hash-verified $($installs.Count) files. Existing server policy was preserved. Backup: $backup"
    } catch {
        for ($i=$changed.Count-1;$i -ge 0;$i--) {
            $entry=$changed[$i]
            if ($entry.Backup) { Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force }
            elseif (Test-Path -LiteralPath $entry.Target) { Remove-Item -LiteralPath $entry.Target }
        }
        throw
    }
} finally { Pop-Location }
