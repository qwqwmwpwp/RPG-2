param([switch]$EditorSymbols)

# Pure compiler invocation. Never start Unity, MSBuild, project scripts, analyzers,
# source generators, tests, or load any produced assembly.
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$unityData = 'E:\ue\2022\2022.3.62f3\Editor\Data'
$dotnet = Join-Path $unityData 'NetCoreRuntime\dotnet.exe'
$csc = Join-Path $unityData 'DotNetSdkRoslyn\csc.dll'
$managed = Join-Path $unityData 'Managed'
$library = Join-Path $projectRoot 'Library\ScriptAssemblies'
$runRoot = Join-Path $PSScriptRoot ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$outputRoot = Join-Path $runRoot 'out'
$scriptRoot = Join-Path $projectRoot 'Assets\Scripts'
$framework = @((Join-Path $unityData 'NetStandard\ref\2.1.0\netstandard.dll')) + @(
    Get-ChildItem -LiteralPath (Join-Path $unityData 'NetStandard\compat\2.1.0\shims\netstandard') -Filter '*.dll' -File -Recurse |
        ForEach-Object { $_.FullName }
)
$engine = @((Join-Path $managed 'UnityEngine.dll')) + @(
    Get-ChildItem -LiteralPath (Join-Path $managed 'UnityEngine') -Filter '*.dll' -File -Recurse |
        ForEach-Object { $_.FullName }
)
$editor = @()
if ($EditorSymbols) {
    # This installation stores editor modules beside engine modules, not in Managed/UnityEditor.
    $editor = @(
        Get-ChildItem -LiteralPath (Join-Path $managed 'UnityEngine') -Filter 'UnityEditor*.dll' -File |
            ForEach-Object { $_.FullName }
    )
}
foreach ($path in @($dotnet, $csc) + $framework + $engine + $editor) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing compiler/reference: $path" }
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
$definitions = @{}
$guidNames = @{}
$results = [System.Collections.Generic.List[object]]::new()
$active = @{}
$done = @{}
foreach ($file in Get-ChildItem -LiteralPath $scriptRoot -Filter '*.asmdef' -File -Recurse) {
    $data = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
    if ($data.includePlatforms.Count -gt 0 -or $data.excludePlatforms.Count -gt 0 -or
        $data.defineConstraints.Count -gt 0 -or $data.versionDefines.Count -gt 0 -or
        $data.precompiledReferences.Count -gt 0 -or $data.overrideReferences -or $data.allowUnsafeCode) {
        throw "Unsupported asmdef settings require explicit review: $($file.FullName)"
    }
    $definitions[$data.name] = [pscustomobject]@{ data = $data; path = $file.FullName; directory = $file.DirectoryName }
    $meta = $file.FullName + '.meta'
    if (Test-Path -LiteralPath $meta) {
        $match = [regex]::Match((Get-Content -LiteralPath $meta -Raw), '(?m)^guid:\s*([a-f0-9]+)')
        if ($match.Success) { $guidNames[$match.Groups[1].Value] = $data.name }
    }
}

function Invoke-StaticCompiler([string]$Name, [string[]]$Sources, [string[]]$References, [string]$Asmdef) {
    $response = Join-Path $runRoot ($Name + '.rsp')
    $log = Join-Path $runRoot ($Name + '.log')
    $output = Join-Path $outputRoot ($Name + '.dll')
    $defines = 'UNITY_2022_3;UNITY_2022_3_OR_NEWER;UNITY_2022_2_OR_NEWER;UNITY_2022_1_OR_NEWER;UNITY_2021_3_OR_NEWER;UNITY_2020_3_OR_NEWER;UNITY_2019_3_OR_NEWER;UNITY_STANDALONE;UNITY_STANDALONE_WIN'
    if ($EditorSymbols) { $defines += ';UNITY_EDITOR;UNITY_EDITOR_WIN' }
    $arguments = @('/nologo', '/nostdlib+', '/target:library', '/langversion:9',
        '/nullable:disable', '/nowarn:0649', ('/define:' + $defines), ('"/out:' + $output + '"'))
    foreach ($reference in $References | Sort-Object -Unique) { $arguments += ('"/reference:' + $reference + '"') }
    foreach ($source in $Sources | Sort-Object -Unique) { $arguments += ('"' + $source + '"') }
    [IO.File]::WriteAllText($response, (($arguments -join "`r`n") + "`r`n"), $utf8)
    $compilerOutput = & $dotnet $csc '/noconfig' ('@' + $response) 2>&1
    $code = $LASTEXITCODE
    [IO.File]::WriteAllText($log, (($compilerOutput | Out-String) + "`r`nexitCode=$code`r`n"), $utf8)
    $results.Add([pscustomobject]@{ assembly = $Name; asmdef = $Asmdef; sourceCount = $Sources.Count;
        sources = $Sources; referenceCount = $References.Count; output = $output; response = $response; log = $log; exitCode = $code })
    if ($code -ne 0) { throw "$Name failed pure Roslyn compilation: $log" }
    return $output
}

function Compile-Definition([string]$Name) {
    if ($done.ContainsKey($Name)) { return $done[$Name] }
    if ($active.ContainsKey($Name)) { throw "Circular asmdef dependency: $Name" }
    $active[$Name] = $true
    $definition = $definitions[$Name]
    $references = @($framework)
    if (-not $definition.data.noEngineReferences) { $references += $engine + $editor }
    foreach ($referenceName in $definition.data.references) {
        if ($referenceName.StartsWith('GUID:')) {
            $guid = $referenceName.Substring(5)
            if (-not $guidNames.ContainsKey($guid)) { throw "Unresolved asmdef GUID: $referenceName" }
            $referenceName = $guidNames[$guid]
        }
        if ($definitions.ContainsKey($referenceName)) {
            $references += Compile-Definition $referenceName
        } else {
            $external = Join-Path $library ($referenceName + '.dll')
            if (-not (Test-Path -LiteralPath $external -PathType Leaf)) { throw "Missing external asmdef reference: $external" }
            $references += $external
        }
    }
    # Assign every source to its nearest asmdef; includes newly created .cs files.
    $sources = @(Get-ChildItem -LiteralPath $definition.directory -Filter '*.cs' -File -Recurse | Where-Object {
        $sourceDirectory = $_.DirectoryName
        while ($sourceDirectory -ne $definition.directory) {
            if (@(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.asmdef' -File).Count -gt 0) { return $false }
            $sourceDirectory = Split-Path -Parent $sourceDirectory
        }
        return $true
    } | Sort-Object FullName | ForEach-Object { $_.FullName })
    if ($sources.Count -eq 0) { throw "No sources for asmdef: $Name" }
    $done[$Name] = Invoke-StaticCompiler $Name $sources $references $definition.path
    $active.Remove($Name)
    return $done[$Name]
}

$failure = $null
try {
    foreach ($name in @($definitions.Keys | Sort-Object)) { $null = Compile-Definition $name }
    $weaponSource = Join-Path $projectRoot 'Assets\CombatGirlsCharacterPack\Biperworks_Tools\CombatGirls_Weapon_Control\Character_Weapon_Controller.cs'
    $null = Invoke-StaticCompiler 'WeaponControllerAudit' @($weaponSource) ($framework + $engine + $editor + @($done.Values)) 'Synthetic compile of this default-assembly consumer only; not the full Assembly-CSharp'
} catch { $failure = $_.ToString() }
$report = [ordered]@{
    purpose = 'Pure Roslyn compilation to unloaded DLLs; no Unity, MSBuild, project code, analyzers, generators, tests, or output assemblies executed.'
    compiler = $csc; editorSymbols = [bool]$EditorSymbols; sourceRoot = $scriptRoot; outputRoot = $outputRoot
    externalReferences = 'Existing Library/ScriptAssemblies package DLLs are read only as compiler metadata; not rebuilt or loaded.'
    limitation = 'This checks all Assets/Scripts asmdefs plus the weapon controller in isolation; not the entire project/default assembly, scene binding, serialized assets, or runtime behavior.'
    assemblies = @($results.ToArray()); failure = $failure
}
$reportPath = Join-Path $runRoot 'compile-report.json'
[IO.File]::WriteAllText($reportPath, (($report | ConvertTo-Json -Depth 8) + "`r`n"), $utf8)
if ($failure) { throw "$failure`nReport: $reportPath" }
Write-Output "Pure static compilation passed ($($results.Count) assemblies). Report: $reportPath"
