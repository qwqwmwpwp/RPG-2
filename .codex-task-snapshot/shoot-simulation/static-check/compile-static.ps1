param([switch]$IncludePlayerMovement)

$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$unityData = 'E:\ue\2022\2022.3.62f3\Editor\Data'
$dotnet = Join-Path $unityData 'NetCoreRuntime\dotnet.exe'
$csc = Join-Path $unityData 'DotNetSdkRoslyn\csc.dll'
$managed = Join-Path $unityData 'Managed'
$unityEngine = Join-Path $managed 'UnityEngine'
$netStandard = Join-Path $unityData 'NetStandard\ref\2.1.0\netstandard.dll'
$netStandardShims = Join-Path $unityData 'NetStandard\compat\2.1.0\shims\netstandard'
$scriptAssemblies = Join-Path $projectRoot 'Library\ScriptAssemblies'
$reportRoot = Join-Path $PSScriptRoot ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$outputRoot = Join-Path $reportRoot 'out'

foreach ($path in @($dotnet, $csc, $netStandard, $netStandardShims, $unityEngine, $scriptAssemblies)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Required compiler/reference path missing: $path" }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

$frameworkReferences = @($netStandard) + @(
    Get-ChildItem -LiteralPath $netStandardShims -Filter '*.dll' -File -Recurse |
        ForEach-Object { $_.FullName }
)
$engineReferences = @(
    Get-ChildItem -LiteralPath $managed -Filter 'UnityEngine.dll' -File |
        ForEach-Object { $_.FullName }
) + @(
    Get-ChildItem -LiteralPath $unityEngine -Filter '*.dll' -File -Recurse |
        ForEach-Object { $_.FullName }
)

function Write-Utf8File([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content, [System.Text.UTF8Encoding]::new($false))
}

function Compile-Assembly([string]$Name, [string]$SourceDirectory, [string[]]$References) {
    $sources = @(
        Get-ChildItem -LiteralPath $SourceDirectory -Filter '*.cs' -File -Recurse |
            Sort-Object FullName |
            ForEach-Object { $_.FullName }
    )
    if ($sources.Count -eq 0) { throw "No C# sources found for $Name in $SourceDirectory" }
    $output = Join-Path $outputRoot ($Name + '.dll')
    $responsePath = Join-Path $reportRoot ($Name + '.rsp')
    $logPath = Join-Path $reportRoot ($Name + '.log')
    $arguments = @(
        '/nologo',
        '/nostdlib+',
        '/target:library',
        '/langversion:9',
        '/nullable:disable',
        '/nowarn:0649',
        ('"/out:' + $output + '"')
    )
    foreach ($reference in $References | Sort-Object -Unique) {
        $arguments += ('"/reference:' + $reference + '"')
    }
    foreach ($source in $sources) { $arguments += ('"' + $source + '"') }
    Write-Utf8File -Path $responsePath -Content (($arguments -join "`r`n") + "`r`n")

    $compilerOutput = & $dotnet $csc ('@' + $responsePath) 2>&1
    $exitCode = $LASTEXITCODE
    Write-Utf8File -Path $logPath -Content (($compilerOutput | Out-String) + "`r`nexitCode=$exitCode`r`n")
    if ($exitCode -ne 0) {
        throw "$Name static compilation failed; see $logPath"
    }
    if (-not (Test-Path -LiteralPath $output)) { throw "$Name compiler succeeded without producing $output" }
    return [pscustomobject]@{
        assembly = $Name
        sourceCount = $sources.Count
        sourceDirectory = $SourceDirectory
        referenceCount = $References.Count
        output = $output
        responseFile = $responsePath
        compilerLog = $logPath
        exitCode = $exitCode
    }
}

$uiDirectory = Join-Path $projectRoot 'Assets\Scripts\UI'
$stateMachineDirectory = Join-Path $projectRoot 'Assets\Scripts\StateMachine'
$inputDirectory = Join-Path $projectRoot 'Assets\Scripts\Input'
$uiResult = Compile-Assembly 'RPG4.UI' $uiDirectory ($frameworkReferences + $engineReferences)
$stateResult = Compile-Assembly 'RPG4.StateMachine' $stateMachineDirectory $frameworkReferences
$assemblyResults = [System.Collections.Generic.List[object]]::new()
$assemblyResults.Add($uiResult)
$assemblyResults.Add($stateResult)
$inputSystemAssembly = Join-Path $scriptAssemblies 'Unity.InputSystem.dll'
if (-not (Test-Path -LiteralPath $inputSystemAssembly)) { throw "Unity Input System reference missing: $inputSystemAssembly" }
$inputResult = Compile-Assembly 'RPG4.Input' $inputDirectory (
    $frameworkReferences + $engineReferences + @($inputSystemAssembly, $uiResult.output)
)
$assemblyResults.Add($inputResult)

if ($IncludePlayerMovement) {
    $playerMovementDirectory = Join-Path $projectRoot 'Assets\Scripts\PlayerMovement'
    $cinemachineAssembly = Join-Path $scriptAssemblies 'Cinemachine.dll'
    if (-not (Test-Path -LiteralPath $cinemachineAssembly)) { throw "Cinemachine reference missing: $cinemachineAssembly" }
    $playerMovementResult = Compile-Assembly 'RPG4.PlayerMovement' $playerMovementDirectory (
        $frameworkReferences + $engineReferences + @(
            $inputResult.output,
            (Join-Path $outputRoot 'RPG4.StateMachine.dll'),
            $cinemachineAssembly
        )
    )
    $assemblyResults.Add($playerMovementResult)
}

$report = [ordered]@{
    purpose = 'Static Roslyn compilation only; no Unity editor, project logic, tests, analyzers, source generators, or output assemblies were executed.'
    unityData = $unityData
    compiler = $csc
    outputDirectory = $outputRoot
    assemblies = @($assemblyResults.ToArray())
}
$reportPath = Join-Path $reportRoot 'compile-report.json'
Write-Utf8File -Path $reportPath -Content (($report | ConvertTo-Json -Depth 6) + "`r`n")
Write-Output "Static compilation passed for RPG4.UI, RPG4.StateMachine, and RPG4.Input. Report: $reportPath"
