# Lists every string literal that game code passes to CompareTag / tag setters, so the names of
# the tags a prefab needs can be matched against the indices seen in ripped prefabs.
param([string]$GameDir = "D:\SteamLibrary\steamapps\common\7 Days To Die")
$cecil = Join-Path $GameDir "Mods\0_TFP_Harmony\Mono.Cecil.dll"
Add-Type -Path $cecil
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $GameDir "7DaysToDie_Data\Managed\Assembly-CSharp.dll"))
$hits = @{}
foreach ($type in $asm.MainModule.GetTypes()) {
  foreach ($m in $type.Methods) {
    if (-not $m.HasBody) { continue }
    $ins = $m.Body.Instructions
    for ($i = 1; $i -lt $ins.Count; $i++) {
      $op = $ins[$i].Operand
      if ($null -eq $op) { continue }
      $s = [string]$op
      if ($s -match "CompareTag|set_tag|FindGameObjectsWithTag|FindWithTag|FindGameObjectWithTag") {
        for ($j = $i - 1; $j -ge [Math]::Max(0, $i - 4); $j--) {
          if ($ins[$j].OpCode.Name -eq "ldstr") { $k = [string]$ins[$j].Operand; if (-not $hits.ContainsKey($k)) { $hits[$k] = @() }; $hits[$k] += "$($type.Name).$($m.Name)"; break }
        }
      }
    }
  }
}
foreach ($k in ($hits.Keys | Sort-Object)) { "{0,-22} {1}" -f $k, (($hits[$k] | Select-Object -Unique | Select-Object -First 4) -join ", ") }
