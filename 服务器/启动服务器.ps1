[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$ConfigPath = '配置/server.json',
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'
$problems = New-Object 'System.Collections.Generic.List[string]'

function Get-AbsolutePath([string]$Path, [string]$BasePath) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath([IO.Path]::Combine($BasePath, $Path))
}

function Test-ServerFiles([string]$ExePath) {
    $directory = [IO.Path]::GetDirectoryName($ExePath)
    $dataName = [IO.Path]::GetFileNameWithoutExtension($ExePath) + '_Data'
    return ((Test-Path -LiteralPath $ExePath -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $directory 'UnityPlayer.dll') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $directory "$dataName/Managed") -PathType Container) -and
        (Test-Path -LiteralPath (Join-Path $directory 'MonoBleedingEdge') -PathType Container))
}

function Test-SamePath([string]$Left, [string]$Right) {
    try {
        return [IO.Path]::GetFullPath($Left).Equals([IO.Path]::GetFullPath($Right), [StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

# 只接受身份匹配、成功记录和运行文件都齐全的构建；不依赖 EXE 修改时间。
function Find-ServerBuild([int]$ProtocolVersion, [string]$ContentHash) {
    $buildRoot = Join-Path $PSScriptRoot '构建/ServerWindows'
    if (-not (Test-Path -LiteralPath $buildRoot -PathType Container)) { return }
    $candidates = foreach ($directory in Get-ChildItem -LiteralPath $buildRoot -Directory) {
        $exe = Join-Path $directory.FullName 'RPG4Server.exe'
        $record = Join-Path $PSScriptRoot ("构建/日志/server-{0}.log" -f $directory.Name)
        if (-not (Test-ServerFiles $exe) -or -not (Test-Path -LiteralPath $record -PathType Leaf)) { continue }
        $started = $false
        $completed = [DateTimeOffset]::MinValue
        # 构建日志由 Unity 以无 BOM 的 UTF-8 写入；按 ANSI 读取会让含中文的 output 路径比对失败。
        foreach ($line in Get-Content -LiteralPath $record -Encoding UTF8) {
            if ($line -match '^(\S+)\s+build_started\s+protocol=(\d+)\s+content=([0-9a-fA-F]{64})\s+output=(.+)$') {
                $recordedExe = $Matches[4].Trim()
                $started = ([int]$Matches[2] -eq $ProtocolVersion -and
                    $Matches[3].ToLowerInvariant() -eq $ContentHash.ToLowerInvariant() -and
                    (Test-SamePath $recordedExe $exe))
                continue
            }
            if ($line -match '^(\S+)\s+build_succeeded\s+output=(.+)$') {
                $recordedExe = $Matches[2].Trim()
                if (-not (Test-SamePath $recordedExe $exe)) { continue }
                $parsedCompleted = [DateTimeOffset]::MinValue
                if ([DateTimeOffset]::TryParse($Matches[1], [ref]$parsedCompleted)) { $completed = $parsedCompleted }
            }
        }
        if ($started -and $completed -ne [DateTimeOffset]::MinValue) {
            [PSCustomObject]@{ Path = $exe; Completed = $completed }
        }
    }
    return ($candidates | Sort-Object Completed -Descending | Select-Object -First 1).Path
}

try {
    $resolvedConfig = Get-AbsolutePath $ConfigPath $PSScriptRoot
    $settings = [PSCustomObject]@{
        keyPath = 'identity/server-key.json'
        databaseConnectionEnvironmentVariable = 'RPG4_MYSQL_CONNECTION'
    }
    if (-not (Test-Path -LiteralPath $resolvedConfig -PathType Leaf)) {
        $problems.Add("缺少配置文件：$resolvedConfig")
    } else {
        try {
            $parsed = Get-Content -LiteralPath $resolvedConfig -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($null -eq $parsed -or $parsed -is [Array] -or $parsed -isnot [PSCustomObject]) {
                throw 'Invalid configuration object'
            }
            foreach ($name in 'keyPath', 'databaseConnectionEnvironmentVariable') {
                if ($null -ne $parsed.PSObject.Properties[$name]) {
                    if ([string]::IsNullOrWhiteSpace([string]$parsed.$name)) { throw 'Missing configuration value' }
                    $settings.$name = [string]$parsed.$name
                }
            }
        } catch {
            $problems.Add('配置文件无法读取或启动必需字段无效；请检查 JSON 和路径/环境变量名。')
        }
    }

    $keyPath = Get-AbsolutePath $settings.keyPath ([IO.Path]::GetDirectoryName($resolvedConfig))
    $assetsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../Assets')).TrimEnd('\')
    $manifestPath = Join-Path $PSScriptRoot '../Assets/Settings/EntryContentManifest.asset'
    $protocolVersion = $null
    $contentHash = $null
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $problems.Add("缺少内容清单：$manifestPath")
    } else {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
        $protocolMatch = [regex]::Match($manifest, '(?m)^\s*protocolVersion:\s*(\d+)\s*$')
        $contentMatch = [regex]::Match($manifest, '(?m)^\s*contentHash:\s*([0-9a-fA-F]{64})\s*$')
        if (-not $protocolMatch.Success -or [int]$protocolMatch.Groups[1].Value -le 0) {
            $problems.Add('内容清单的 protocolVersion 缺失或格式无效。')
        } else { $protocolVersion = [int]$protocolMatch.Groups[1].Value }
        if (-not $contentMatch.Success) {
            $problems.Add('内容清单的 contentHash 缺失或格式无效（应为 64 位十六进制）。')
        } else { $contentHash = $contentMatch.Groups[1].Value.ToLowerInvariant() }
    }
    if ($keyPath.Equals($assetsRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $keyPath.StartsWith($assetsRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
        $problems.Add('服务器密钥必须位于项目 Assets 目录之外。')
    } elseif (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        $problems.Add("缺少已有服务器密钥：$keyPath（不会自动生成或替换）")
    }

    $variableName = $settings.databaseConnectionEnvironmentVariable
    $connection = $null
    foreach ($scope in 'Process', 'User', 'Machine') {
        $value = [Environment]::GetEnvironmentVariable($variableName, $scope)
        if (-not [string]::IsNullOrWhiteSpace($value)) { $connection = $value; break }
    }
    if ([string]::IsNullOrWhiteSpace($connection)) {
        $problems.Add("缺少数据库环境变量：$variableName（已检查 Process、User、Machine）")
    }

    $resolvedServer = $null
    if (-not [string]::IsNullOrWhiteSpace($ServerPath)) {
        $resolvedServer = Get-AbsolutePath $ServerPath $PSScriptRoot
        if (-not (Test-ServerFiles $resolvedServer)) {
            $problems.Add("指定服务器缺少 EXE、UnityPlayer.dll、对应 _Data/Managed 或 MonoBleedingEdge：$resolvedServer")
        }
    } else {
        if ($null -ne $protocolVersion -and $null -ne $contentHash) {
            $resolvedServer = Find-ServerBuild $protocolVersion $contentHash
        }
        if ([string]::IsNullOrWhiteSpace($resolvedServer)) {
            $problems.Add('未找到与当前内容清单身份匹配、成功记录和运行文件齐全的服务器；可用 -ServerPath 指定已有完整构建。')
        }
    }

    if ($problems.Count -gt 0) {
        Write-Host '启动预检失败，未启动服务器：' -ForegroundColor Red
        foreach ($problem in $problems) { Write-Host "  - $problem" }
        if ($resolvedServer) { Write-Host "已选服务器：$resolvedServer" }
        exit 1
    }
    Write-Host "服务器：$resolvedServer"
    Write-Host "配置：$resolvedConfig"
    if ($CheckOnly) {
        Write-Host '本地预检通过；未连接数据库、未验证密钥内容、未启动进程。数据库连接需包含 Database 并使用 SslMode=VerifyFull，由服务器初始化时验证。'
        exit 0
    }

    $logDirectory = Join-Path $PSScriptRoot '日志'
    [void](New-Item -ItemType Directory -Path $logDirectory -Force)
    $logPath = Join-Path $logDirectory ("server-{0}-{1}.log" -f (Get-Date -Format 'yyyyMMdd-HHmmss'), [Guid]::NewGuid().ToString('N'))
    $arguments = '-batchmode -nographics -serverConfig "{0}" -logFile "{1}"' -f $resolvedConfig, $logPath
    $previousValue = [Environment]::GetEnvironmentVariable($variableName, 'Process')
    try {
        # Start-Process 继承进程环境；只在创建子进程期间提供所需变量，随后还原。
        [Environment]::SetEnvironmentVariable($variableName, $connection, 'Process')
        $process = Start-Process -FilePath $resolvedServer -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($resolvedServer)) -WindowStyle Hidden -PassThru
    } finally {
        [Environment]::SetEnvironmentVariable($variableName, $previousValue, 'Process')
    }
    Write-Host "进程已启动，PID：$($process.Id)，等待服务器初始化。"
    Write-Host "日志：$logPath"
    exit 0
} catch {
    # 不输出异常正文，避免配置或环境变量中的秘密出现在终端。
    Write-Host '启动脚本执行失败；请检查路径、配置格式及本地文件/环境变量读取权限。' -ForegroundColor Red
    exit 1
}
