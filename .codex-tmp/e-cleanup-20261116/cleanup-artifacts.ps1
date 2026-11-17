param([switch]$Execute)
$ErrorActionPreference = 'Stop'
$base = 'E:\github\RPG-2\.codex-tmp\e-cleanup-20261116'
$planPath = Join-Path $base 'artifacts-plan.json'
$runPath = Join-Path $base 'cleanup-run.json'
$specs = @(
  [pscustomobject]@{ Path='E:\PS\Adobe Photoshop 2025 26.10.0.7 m0nkrus.zip'; Kind='File'; Purpose='Authorized archive' },
  [pscustomobject]@{ Path='E:\PS\Adobe Photoshop 2025 26.10.0.7 m0nkrus'; Kind='Directory'; Purpose='Authorized extracted installer package' },
  [pscustomobject]@{ Path='E:\Git\banana-master.zip'; Kind='File'; Purpose='Authorized archive' },
  [pscustomobject]@{ Path='E:\UE转换\cache'; Kind='Directory'; Purpose='Authorized conversion cache' }
)
function Get-Canonical([string]$p) {
  return [IO.Path]::GetFullPath($p).TrimEnd('\')
}
function Assert-NoReparseInAncestors([string]$p) {
  $full = Get-Canonical $p
  $drive = [IO.Path]::GetPathRoot($full)
  $rest = $full.Substring($drive.Length).Split('\')
  $cur = $drive.TrimEnd('\')
  foreach ($part in $rest) {
    $cur = if ($cur) { Join-Path $cur $part } else { $part }
    if (Test-Path -LiteralPath $cur) {
      $it = Get-Item -LiteralPath $cur -Force
      if (($it.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Reparse point in target ancestry: $cur"
      }
    }
  }
}
function Inspect-Tree([string]$p, [string]$kind) {
  $canonical = Get-Canonical $p
  $resolved = (Resolve-Path -LiteralPath $canonical -ErrorAction Stop).Path.TrimEnd('\')
  if (-not [string]::Equals($resolved,$canonical,[StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved target differs from allowlist: $canonical"
  }
  Assert-NoReparseInAncestors $canonical
  $root = Get-Item -LiteralPath $canonical -Force
  $isDir = $root.PSIsContainer
  if (($kind -eq 'Directory') -ne $isDir) { throw "Unexpected target kind: $canonical" }
  $links = [Collections.Generic.List[string]]::new()
  $files = [Collections.Generic.List[object]]::new()
  $dirs = [Collections.Generic.Stack[string]]::new()
  $dirCount = if ($isDir) { 1 } else { 0 }
  if ($isDir) { $dirs.Push($canonical) } else { $files.Add([pscustomobject]@{Path=$canonical;Length=[long]$root.Length}) }
  while ($dirs.Count -gt 0) {
    $current = $dirs.Pop()
    foreach ($item in Get-ChildItem -LiteralPath $current -Force -ErrorAction Stop) {
      if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        $links.Add($item.FullName)
        continue
      }
      if ($item.PSIsContainer) { $dirCount++; $dirs.Push($item.FullName) }
      else { $files.Add([pscustomobject]@{Path=$item.FullName;Length=[long]$item.Length}) }
    }
  }
  if ($links.Count -gt 0) { throw "Reparse point inside target: $($links[0])" }
  $bytes = [long](($files | Measure-Object -Property Length -Sum).Sum)
  [pscustomobject]@{
    Path=$canonical; Kind=$kind; Exists=$true; Bytes=$bytes; FileCount=$files.Count
    DirectoryCount=$dirCount
    ReparsePoints=$links.ToArray(); RootAttributes=[string]$root.Attributes
    RootLastWriteTime=$root.LastWriteTime.ToString('o')
  }
}
function Find-TargetProcessRefs($targets) {
  $procs = @(Get-CimInstance Win32_Process -ErrorAction Stop)
  $refs = [Collections.Generic.List[object]]::new()
  foreach ($proc in $procs) {
    if ([int]$proc.ProcessId -eq $PID) { continue }
    $matched = [Collections.Generic.List[string]]::new()
    $ep = [string]$proc.ExecutablePath
    $cl = [string]$proc.CommandLine
    foreach ($t in $targets) {
      $needle = ([string]$t.Path).TrimEnd('\')
      if (($ep -and $ep.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0) -or
          ($cl -and $cl.IndexOf($needle,[StringComparison]::OrdinalIgnoreCase) -ge 0)) {
        $matched.Add($t.Path)
      }
    }
    if ($matched.Count) {
      $refs.Add([pscustomobject]@{Name=$proc.Name;ProcessId=$proc.ProcessId;Targets=$matched.ToArray()})
    }
  }
  return $refs.ToArray()
}
$targets = @()
foreach ($s in $specs) {
  $canon = Get-Canonical $s.Path
  if (-not [string]::Equals($canon,$s.Path,[StringComparison]::OrdinalIgnoreCase)) { throw "Allowlist canonicalization mismatch: $($s.Path)" }
  if (-not (Test-Path -LiteralPath $canon)) { throw "Authorized target missing: $canon" }
  $targets += Inspect-Tree $canon $s.Kind
}
$refs = @(Find-TargetProcessRefs $targets)
if ($Execute) {
  if (-not (Test-Path -LiteralPath $planPath)) { throw 'Preflight plan missing' }
  $plan = Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
  if (@($plan.Targets).Count -ne $specs.Count) { throw 'Plan target count mismatch' }
  for ($i=0; $i -lt $specs.Count; $i++) {
    if (-not [string]::Equals([string]$targets[$i].Path,[string]$plan.Targets[$i].Path,[StringComparison]::OrdinalIgnoreCase) -or
        [long]$targets[$i].Bytes -ne [long]$plan.Targets[$i].Bytes -or
        [long]$targets[$i].FileCount -ne [long]$plan.Targets[$i].FileCount) {
      throw "Target changed since preflight; refusing: $($targets[$i].Path)"
    }
  }
  if ($refs.Count) { throw 'A process references a target; refusing deletion' }
  $deleted = [Collections.Generic.List[string]]::new()
  foreach ($s in $specs) {
    $target = Get-Canonical $s.Path
    if (-not [string]::Equals($target,$s.Path,[StringComparison]::OrdinalIgnoreCase)) { throw "Allowlist mismatch: $target" }
    $fresh = Inspect-Tree $target $s.Kind
    if ($fresh.ReparsePoints.Count) { throw "Reparse point found on final check: $target" }
    if ($s.Kind -eq 'Directory') { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction Stop }
    else { Remove-Item -LiteralPath $target -Force -ErrorAction Stop }
    $deleted.Add($target)
    Write-Output ("Completed target {0}/{1}: {2}" -f $deleted.Count,$specs.Count,$target)
  }
  $run = [pscustomobject]@{CompletedAt=(Get-Date).ToString('o');Deleted=$deleted.ToArray();ProcessReferences=$refs}
  $run | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $runPath -Encoding UTF8
  Write-Output "Deleted authorized targets: $($deleted.Count)"
} else {
  $out = [pscustomobject]@{
    CreatedAt=(Get-Date).ToString('o');Mode='Preflight only; no deletion performed'
    Targets=$targets;ProcessReferences=$refs
    Actions=@($specs | ForEach-Object {[pscustomobject]@{Path=$_.Path;Action='Delete only after explicit execution instruction';Purpose=$_.Purpose}})
  }
  $out | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $planPath -Encoding UTF8
  Get-Content -LiteralPath $planPath -Raw
}


