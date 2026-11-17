Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:Stage = 'E:\github\RPG-2\.codex-tmp\e-cleanup-20261116'
$script:CutoffUtc = [datetime]::Parse('2026-11-13T08:34:50Z').ToUniversalTime()
$script:AllowedLibraries = @(
    'E:\unity\hhzpt\Library',
    'E:\unity\My project\Library',
    'E:\unity\live2D-2\Library',
    'E:\unity\TieJiangPu\Library',
    'E:\unity\lianji\Library',
    'E:\unity\ecs1\Library',
    'E:\unity\lianji3\Library',
    'E:\unity\特效\Library',
    'E:\unity\zdxt\Library',
    'E:\unity\My project (3)\Library',
    'E:\unity\UmaViewer\Library',
    'E:\unity\BUG\Library',
    'E:\unity\huihezhi\Library',
    'E:\unity\ZhangDou\Library',
    'E:\unity\RPG-2\Library',
    'E:\unity\lianji2\Library',
    'E:\unity\zhanqi\Library',
    'E:\unity\jszl\Library',
    'E:\unity\YZCT\Library',
    'E:\unity\RPJ\Library',
    'E:\unity\3C\Library',
    'E:\unity\WOW\Library',
    'E:\unity\科研\Library',
    'E:\unity\Hui he (2)\Library',
    'E:\unity\My project (2)\Library',
    'E:\unity\My project (1)\Library',
    'E:\unity\xfj\Library',
    'E:\unity\HHZ\Library',
    'E:\unity\分层状态机\Library',
    'E:\unity\zq\Library',
    'E:\unity\gongjue\Library',
    'E:\github\unity\BBB\Library',
    'E:\github\unity\dot-of-dot\dot of dot\Library',
    'E:\github\unity\banana\Library',
    'E:\github\unity\ecs\Library',
    'E:\github\unity\nongchangbaoweizhan\Library',
    'E:\github\unity\RPG\Library',
    'E:\github\ZDXT2\Library',
    'E:\github\ZDXT\Library',
    'E:\github\unity\RPG\ecs\Library',
    'E:\github\RPG-1\Library',
    'E:\github\RPG-1-verify\Library',
    'E:\rpg_probe\Library',
    'E:\rpg_probe3\Library',
    'E:\rpg_probe4\Library',
    'E:\rpg1review\Library',
    'E:\Git\banana\Library'
)
function Get-CanonicalPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
}
function Assert-NoLinkedAncestors([string]$Path) {
    $current = Get-CanonicalPath $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse point in target/ancestor: $current"
            }
        }
        $parent = [IO.Directory]::GetParent($current)
        if ($null -eq $parent) { break }
        $current = $parent.FullName
    }
}
function Assert-AllowedLibrary([string]$Path) {
    $canonical = Get-CanonicalPath $Path
    if ($script:AllowedLibraries -cnotcontains $canonical) { throw "Not in the exact fixed allowlist: $canonical" }
    if ([IO.Path]::GetFileName($canonical) -cne 'Library') { throw 'Target leaf must be exactly Library' }
    foreach ($protected in @('E:\github\RPG-2', 'E:\github\NXM-1')) {
        if ($canonical.Equals($protected, [StringComparison]::OrdinalIgnoreCase) -or
            $canonical.StartsWith($protected + '\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Protected project: $canonical"
        }
    }
    Assert-NoLinkedAncestors $canonical
    return $canonical
}
if (-not ('LibraryCleanupReadScan' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
public class LibraryCleanupReadScan {
 public class Result {
  public string Path; public long Bytes, Files, Directories;
  public DateTime MaxFileWriteUtc=DateTime.MinValue, MaxDirectoryWriteUtc=DateTime.MinValue;
  public string MaxFilePath,MaxDirectoryPath;
  public List<string> Errors=new List<string>(),ReparsePoints=new List<string>(),NestedProjects=new List<string>();
 }
 static void Visit(DirectoryInfo d, Result r, bool detectNested) {
  r.Directories++;
  try {
   if(d.LastWriteTimeUtc>r.MaxDirectoryWriteUtc){r.MaxDirectoryWriteUtc=d.LastWriteTimeUtc;r.MaxDirectoryPath=d.FullName;}
   if(detectNested) {
    if(d.Name.Equals(".git",StringComparison.OrdinalIgnoreCase)) r.NestedProjects.Add(d.FullName);
    if(d.Name.Equals("ProjectSettings",StringComparison.OrdinalIgnoreCase) &&
       File.Exists(System.IO.Path.Combine(d.FullName,"ProjectVersion.txt"))) r.NestedProjects.Add(d.Parent.FullName);
    if(Directory.Exists(System.IO.Path.Combine(d.FullName,"Assets")) &&
       Directory.Exists(System.IO.Path.Combine(d.FullName,"Packages")) &&
       Directory.Exists(System.IO.Path.Combine(d.FullName,"ProjectSettings"))) r.NestedProjects.Add(d.FullName);
   }
   foreach(var entry in d.EnumerateFileSystemInfos()) {
    try {
     if((entry.Attributes&FileAttributes.ReparsePoint)!=0){r.ReparsePoints.Add(entry.FullName);continue;}
     if((entry.Attributes&FileAttributes.Directory)!=0){Visit((DirectoryInfo)entry,r,detectNested);continue;}
     var f=(FileInfo)entry;r.Bytes+=f.Length;r.Files++;
     var time=f.LastWriteTimeUtc;
     if(time>r.MaxFileWriteUtc){r.MaxFileWriteUtc=time;r.MaxFilePath=f.FullName;}
    }catch(Exception e){r.Errors.Add(entry.FullName+" | "+e.GetType().Name);}
   }
  }catch(Exception e){r.Errors.Add(d.FullName+" | "+e.GetType().Name);}
 }
 public static Result Scan(string path,bool nested) {
  var r=new Result{Path=path};
  var d=new DirectoryInfo(path);
  if(!d.Exists){r.Errors.Add("Missing directory: "+path);return r;}
  if((d.Attributes&FileAttributes.ReparsePoint)!=0){r.ReparsePoints.Add(path);return r;}
  Visit(d,r,nested);return r;
 }
}
'@
}
function Get-RunningUnityProjects {
    $roots = [Collections.Generic.List[string]]::new()
    foreach ($proc in @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" -ErrorAction Stop)) {
        if ([string]::IsNullOrWhiteSpace($proc.CommandLine)) { throw "Cannot read Unity process $($proc.ProcessId)" }
        $match = [regex]::Match($proc.CommandLine, '(?i)(?:^|\s)-projectpath\s+(?:"([^"]+)"|(.+?))(?=\s+-|$)')
        if (-not $match.Success) { throw "Cannot establish project of Unity process $($proc.ProcessId)" }
        $root = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value.Trim() }
        $roots.Add((Get-CanonicalPath $root))
    }
    return @($roots.ToArray())
}
function Get-LibraryAssessment {
    param([Parameter(Mandatory)][string]$Path, [string[]]$ExternallyRecentRoots = @())
    $reasons = [Collections.Generic.List[string]]::new()
    $root = [IO.Path]::GetDirectoryName((Get-CanonicalPath $Path))
    $data = [ordered]@{
        Path = Get-CanonicalPath $Path
        ProjectRoot = $root
        CutoffUtc = $script:CutoffUtc.ToString('o')
        Bytes = 0L
        Files = 0L
        MaxFileWriteTimeUtc = $null
        MaxFilePath = $null
        MaxDirectoryWriteTimeUtc = $null
        MaxTraceWriteTimeUtc = $null
        MaxTracePath = $null
        ProjectVersion = $null
        ProjectStructureVerified = $false
        TraceStats = @()
        ReparsePoints = @()
        NestedProjects = @()
        Errors = @()
        Decision = 'Keep'
        Reasons = @()
    }
    try {
        $target = Assert-AllowedLibrary $Path
        if (-not (Test-Path -LiteralPath $target -PathType Container)) { $reasons.Add('Library missing'); $data.Decision = 'AlreadyMissing'; return [pscustomobject]$data }
        foreach ($marker in @('Assets', 'Packages', 'ProjectSettings')) {
            $markerPath = Join-Path $root $marker
            if (-not (Test-Path -LiteralPath $markerPath -PathType Container)) { throw "Missing Unity project marker: $markerPath" }
            Assert-NoLinkedAncestors $markerPath
        }
        $versionPath = Join-Path $root 'ProjectSettings\ProjectVersion.txt'
        if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "Missing ProjectVersion: $versionPath" }
        Assert-NoLinkedAncestors $versionPath
        $data.ProjectVersion = (Get-Content -LiteralPath $versionPath -TotalCount 2 -ErrorAction Stop) -join '; '
        $data.ProjectStructureVerified = $true
        $running = @(Get-RunningUnityProjects)
        if ($running -icontains $root) { $reasons.Add('Project currently running in Unity') }
        if ($ExternallyRecentRoots -icontains $root) { $reasons.Add('Recent project evidence from external Editor/Hub records') }
        $library = [LibraryCleanupReadScan]::Scan($target, $true)
        $data.Bytes = $library.Bytes; $data.Files = $library.Files
        if ($library.MaxFileWriteUtc -ne [datetime]::MinValue) {
            $data.MaxFileWriteTimeUtc = $library.MaxFileWriteUtc.ToString('o')
            $data.MaxFilePath = $library.MaxFilePath
        }
        if ($library.MaxDirectoryWriteUtc -ne [datetime]::MinValue) { $data.MaxDirectoryWriteTimeUtc = $library.MaxDirectoryWriteUtc.ToString('o') }
        $data.ReparsePoints = @($library.ReparsePoints.ToArray())
        $data.NestedProjects = @($library.NestedProjects.ToArray() | Select-Object -Unique)
        $data.Errors = @($library.Errors.ToArray())
        if ($data.ReparsePoints.Count -gt 0) { $reasons.Add('Reparse points inside Library') }
        if ($data.NestedProjects.Count -gt 0) { $reasons.Add('Embedded Unity project markers inside Library') }
        if ($data.Errors.Count -gt 0) { $reasons.Add('Incomplete Library metadata access') }
        $traces = [Collections.Generic.List[object]]::new()
        $maxTrace = [datetime]::MinValue
        foreach ($traceName in @('Library', 'Logs', 'Temp')) {
            $tracePath = Join-Path $root $traceName
            if (-not (Test-Path -LiteralPath $tracePath -PathType Container)) { continue }
            Assert-NoLinkedAncestors $tracePath
            $scan = if ($traceName -eq 'Library') { $library } else { [LibraryCleanupReadScan]::Scan($tracePath, $false) }
            $traces.Add([pscustomobject]@{
                Name=$traceName; Bytes=$scan.Bytes; Files=$scan.Files
                MaxFileWriteTimeUtc=if($scan.MaxFileWriteUtc -eq [datetime]::MinValue){$null}else{$scan.MaxFileWriteUtc.ToString('o')}
                MaxFilePath=$scan.MaxFilePath; Errors=@($scan.Errors.ToArray()); ReparsePoints=@($scan.ReparsePoints.ToArray())
            })
            if ($scan.Errors.Count -gt 0 -or $scan.ReparsePoints.Count -gt 0) { $reasons.Add("Unclear access/link evidence in $traceName") }
            if ($scan.MaxFileWriteUtc -gt $maxTrace) { $maxTrace=$scan.MaxFileWriteUtc; $data.MaxTracePath=$scan.MaxFilePath }
            if ($scan.MaxDirectoryWriteUtc -ge $script:CutoffUtc) { $reasons.Add("Recent directory metadata in $traceName; conservative keep") }
        }
        $data.TraceStats = @($traces.ToArray())
        if ($maxTrace -ne [datetime]::MinValue) { $data.MaxTraceWriteTimeUtc=$maxTrace.ToString('o') }
        if ($maxTrace -ge $script:CutoffUtc) { $reasons.Add('File-level runtime trace at or after the fixed cutoff') }
        if ($library.Files -eq 0 -or $maxTrace -eq [datetime]::MinValue) { $reasons.Add('No reliable file-level evidence') }
        if ($reasons.Count -eq 0) { $data.Decision='DeleteEligible'; $reasons.Add('Verified Unity project; complete metadata; no linked/embedded project or recent runtime evidence') }
    } catch {
        $reasons.Add($_.Exception.Message)
        $data.Errors = @($data.Errors) + $_.Exception.Message
    } finally {
        $data.Reasons = @($reasons.ToArray() | Select-Object -Unique)
    }
    return [pscustomobject]$data
}

