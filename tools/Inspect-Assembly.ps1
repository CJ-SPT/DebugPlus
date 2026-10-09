param([string]$Assembly = 'F:\SPT 4.1.x\EscapeFromTarkov_Data\Managed\Assembly-CSharp.dll', [string]$Type = '.', [string]$Member = '.', [switch]$IL, [switch]$Fields)
$ErrorActionPreference = 'Stop'
Add-Type -Path 'F:\SPT 4.1.x\BepInEx\core\Mono.Cecil.dll'
$definition = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($Assembly)
try {
    function AllTypes($items) { foreach ($item in $items) { $item; AllTypes $item.NestedTypes } }
    foreach ($entry in (AllTypes $definition.MainModule.Types | Where-Object FullName -match $Type)) {
        Write-Output "TYPE $($entry.FullName) : $($entry.BaseType)"
        foreach ($method in ($entry.Methods | Where-Object Name -match $Member)) {
            Write-Output "$($method.Attributes) $($method.FullName)"
            if ($IL -and $method.HasBody) { $method.Body.Instructions | ForEach-Object ToString }
        }
        if ($Fields) {
            $entry.Properties | Where-Object Name -match $Member | ForEach-Object { "PROPERTY $($_.FullName)" }
            $entry.Fields | Where-Object Name -match $Member | ForEach-Object { "FIELD $($_.Attributes) $($_.FullName)" }
        }
    }
} finally { $definition.Dispose() }
