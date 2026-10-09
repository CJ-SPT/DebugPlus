param([string]$GameRoot = 'F:\SPT 4.1.x')
$ErrorActionPreference = 'Stop'
Add-Type -Path (Join-Path $GameRoot 'BepInEx/core/Mono.Cecil.dll')
$unity = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'EscapeFromTarkov_Data/Managed/UnityEngine.CoreModule.dll'))
$eft = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameRoot 'EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll'))
try {
    $count = 0
    foreach ($typeName in @('UnityEngine.Debug', 'UnityEngine.Logger')) {
        $type = $unity.MainModule.Types | Where-Object FullName -eq $typeName
        $methods = @($type.Methods | Where-Object { $_.IsPublic -and $_.Name.StartsWith('Log') -and $_.ReturnType.FullName -eq 'System.Void' -and $_.Parameters.Count -gt 0 })
        foreach ($method in $methods) {
            if (!$method.HasBody) { throw "Unity logging entry point has no patchable managed body: $method" }
            foreach ($parameter in $method.Parameters) {
                if ($parameter.ParameterType.FullName -notin @('System.Object', 'System.String', 'System.Object[]', 'System.Exception', 'UnityEngine.Object', 'UnityEngine.LogType', 'UnityEngine.LogOption')) {
                    throw "Unhandled logging signature: $method"
                }
            }
            $count++
        }
        foreach ($name in @('Log', 'LogFormat', 'LogException')) {
            if (!($methods | Where-Object Name -eq $name)) { throw "Missing $typeName.$name" }
        }
    }
    $debug = $unity.MainModule.Types | Where-Object FullName -eq 'UnityEngine.Debug'
    foreach ($name in @('LogWarning', 'LogError', 'LogAssertion', 'LogAssertionFormat')) {
        if (!($debug.Methods | Where-Object Name -eq $name)) { throw "Missing assertion/severity hook: $name" }
    }
    $logger = $eft.MainModule.Types | Where-Object FullName -eq 'AbstractLogger'
    if (!($logger.Fields | Where-Object Name -eq 'IsLogsEnabled')) { throw 'Native global logging gate changed.' }
    if (!($logger.Methods | Where-Object { $_.Name -eq '.ctor' -and $_.Parameters[0].ParameterType.FullName -eq 'NLog.Logger' })) { throw 'Native logger discovery constructor changed.' }
    $settings = $eft.MainModule.Types | Where-Object FullName -eq 'LoggingSettings'
    if (!($settings.Fields | Where-Object Name -eq 'AILogging')) { throw 'Native AI diagnostic gate changed.' }
    Write-Output "Verified $count installed Unity logging overloads, assertion hooks, native discovery constructor and native/AI gates (offline signature checks)."
} finally { $unity.Dispose(); $eft.Dispose() }
