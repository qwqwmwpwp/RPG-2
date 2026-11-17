param([string[]]$ExternallyRecentRoots=@())
. "$PSScriptRoot\LibraryCleanup.Common.ps1"
Assert-NoLinkedAncestors $script:Stage
$rows = [Collections.Generic.List[object]]::new()
foreach ($path in $script:AllowedLibraries) {
    $row = Get-LibraryAssessment -Path $path -ExternallyRecentRoots $ExternallyRecentRoots
    $rows.Add($row)
    Write-Host ("{0} | {1:F3} GiB | {2} | {3}" -f $row.Decision, ($row.Bytes/1GB), $row.Path, ($row.Reasons -join '; '))
}
$plan = [ordered]@{
    SchemaVersion=1
    GeneratedUtc=[datetime]::UtcNow.ToString('o')
    CutoffUtc=$script:CutoffUtc.ToString('o')
    Scope='Exactly 47 allowlisted Unity Library directories; source and project metadata excluded'
    ExternalEvidenceReviewRequired=$true
    ExternalRecentRoots=@($ExternallyRecentRoots)
    CurrentUnityProjectRoots=@(Get-RunningUnityProjects)
    Items=@($rows.ToArray())
}
$plan | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $script:Stage 'library-cleanup-plan.json') -Encoding utf8
$rows | Group-Object Decision | ForEach-Object {
    [pscustomobject]@{Decision=$_.Name;Count=$_.Count;Bytes=($_.Group|Measure-Object Bytes -Sum).Sum;GiB=[math]::Round(($_.Group|Measure-Object Bytes -Sum).Sum/1GB,6)}
} | ConvertTo-Json -Compress

