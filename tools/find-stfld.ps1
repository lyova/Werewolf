# Lists every method that WRITES a given field (stfld / stsfld), which scan-cecil.ps1 cannot do
# because it stops at the first matching instruction of each method - usually a read.
param([Parameter(Mandatory=$true)][string]$Field, [switch]$IL, [string]$GameDir = "D:\SteamLibrary\steamapps\common\7 Days To Die")
Add-Type -Path (Join-Path $GameDir "Mods\0_TFP_Harmony\Mono.Cecil.dll")
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir "7DaysToDie_Data\Managed\Assembly-CSharp.dll"))
foreach ($type in $asm.MainModule.GetTypes()) {
  foreach ($m in $type.Methods) {
    if (-not $m.HasBody) { continue }
    $writes = $m.Body.Instructions | Where-Object { ($_.OpCode.Name -eq "stfld" -or $_.OpCode.Name -eq "stsfld") -and ([string]$_.Operand) -match $Field }
    if ($writes) {
      "$($type.FullName)::$($m.Name)"
      if ($IL) { foreach ($i in $m.Body.Instructions) { if ($i.OpCode.Name -ne "nop") { "    $($i.OpCode.Name) $($i.Operand)" } } }
    }
  }
}
