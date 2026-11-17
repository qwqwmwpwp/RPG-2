param(
    [Parameter(Mandatory)][string[]]$LibraryPaths,
    [switch]$Execute,
    [switch]$ExternalEvidenceReviewed,
    [string[]]$ExternallyRecentRoots=@()
)
. "$PSScriptRoot\LibraryCleanup.Common.ps1"
if (-not $Execute) { throw 'Deletion is disabled. Explicit -Execute is required after review.' }
if (-not $ExternalEvidenceReviewed) { throw 'Main agent must review external Editor/Hub evidence before execution.' }
Assert-NoLinkedAncestors $script:Stage
$planPath=Join-Path $script:Stage 'library-cleanup-plan.json'
$plan=Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
$planCutoffUtc=([datetime]$plan.CutoffUtc).ToUniversalTime()
if ($plan.SchemaVersion -ne 1 -or $planCutoffUtc.Ticks -ne $script:CutoffUtc.Ticks) { throw 'Unexpected plan schema/cutoff' }
$runId=(Get-Date -Format 'yyyyMMdd-HHmmss-fff')+'-'+([guid]::NewGuid().ToString('N').Substring(0,8))
$logPath=Join-Path $script:Stage ("execution-{0}.jsonl" -f $runId)
$effectiveRecent=@($ExternallyRecentRoots)+@($plan.ExternalRecentRoots)
$runningAtStart=@(Get-RunningUnityProjects)
foreach ($requested in @($LibraryPaths | Select-Object -Unique)) {
    $event=[ordered]@{Utc=[datetime]::UtcNow.ToString('o');Path=$requested;Status='Skipped';BytesBefore=0L;FilesBefore=0L;Reason=$null;RemainingBytes=$null;RemainingFiles=$null}
    try {
        $path=Assert-AllowedLibrary $requested
        $prior=@($plan.Items|Where-Object {$_.Path -ceq $path})
        if ($prior.Count -ne 1 -or $prior[0].Decision -ne 'DeleteEligible') { throw 'Plan does not mark this exact target DeleteEligible' }
        $now=Get-LibraryAssessment -Path $path -ExternallyRecentRoots $effectiveRecent
        $event.BytesBefore=$now.Bytes; $event.FilesBefore=$now.Files
        if ($now.Decision -eq 'AlreadyMissing') { $event.Status='AlreadyMissing'; $event.Reason='Target no longer exists' }
        elseif ($now.Decision -ne 'DeleteEligible') { $event.Status='Preserved'; $event.Reason=$now.Reasons -join '; ' }
        else {
            # Final path and process validation immediately before recursive deletion.
            $verified=Assert-AllowedLibrary $path
            $runningImmediatelyBefore=@(Get-RunningUnityProjects)
            if ($runningImmediatelyBefore -icontains $now.ProjectRoot) { throw 'Project began running after metadata scan' }
            if ($verified -cne $path) { throw 'Canonical target changed' }
            $event.Status='Deleting'; $event.Reason='Final prechecks passed'
            $event | ConvertTo-Json -Compress | Add-Content -LiteralPath $logPath -Encoding utf8
            Write-Host ("Deleting Library | {0:F3} GiB | {1} files | {2}" -f ($now.Bytes/1GB),$now.Files,$verified)
            Remove-Item -LiteralPath $verified -Recurse -Force -ErrorAction Stop
            if (Test-Path -LiteralPath $verified) { throw 'Target still exists after Remove-Item' }
            $event.Status='Completed'; $event.Reason='Library removed; parent project retained'
        }
    } catch {
        $event.Status='FailedOrPreserved'; $event.Reason=$_.Exception.Message
        if (Test-Path -LiteralPath $requested -PathType Container) {
            try {
                $remaining=[LibraryCleanupReadScan]::Scan($requested,$false)
                $event.RemainingBytes=$remaining.Bytes; $event.RemainingFiles=$remaining.Files
            } catch {}
        }
    }
    $event.Utc=[datetime]::UtcNow.ToString('o')
    $event | ConvertTo-Json -Compress | Add-Content -LiteralPath $logPath -Encoding utf8
    Write-Host ("{0} | {1} | {2}" -f $event.Status,$event.Path,$event.Reason)
    [pscustomobject]$event
}
Write-Host "Run log: $logPath"

