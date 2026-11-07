[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$ConfigPath,
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'
$problems = New-Object 'System.Collections.Generic.List[string]'
$process = $null
$logPath = $null

function Get-AbsolutePath([string]$Path, [string]$BasePath) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath([IO.Path]::Combine($BasePath, $Path))
}

function Test-PathOutsideAssets([string]$Path, [string]$AssetsRoot) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    return (-not $fullPath.Equals($AssetsRoot, [StringComparison]::OrdinalIgnoreCase) -and
        -not $fullPath.StartsWith($AssetsRoot + '\', [StringComparison]::OrdinalIgnoreCase))
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
    } catch { return $false }
}

# 每个 attempt 必须同时有匹配身份的 build_started、其后的成功记录和完整 Mono 运行文件。
function Find-ServerBuild([int]$ProtocolVersion, [string]$ContentHash, [string]$RequestedPath) {
    $buildRoot = Join-Path $PSScriptRoot '构建/ServerWindows'
    if (-not (Test-Path -LiteralPath $buildRoot -PathType Container)) { return }
    $candidates = foreach ($directory in Get-ChildItem -LiteralPath $buildRoot -Directory) {
        $exe = Join-Path $directory.FullName 'RPG4Server.exe'
        if ($RequestedPath -and -not (Test-SamePath $RequestedPath $exe)) { continue }
        $record = Join-Path $PSScriptRoot ("构建/日志/server-{0}.log" -f $directory.Name)
        if (-not (Test-ServerFiles $exe) -or -not (Test-Path -LiteralPath $record -PathType Leaf)) { continue }
        $identityStarted = $false
        $succeeded = $false
        $failedAfterStart = $false
        $completed = [DateTimeOffset]::MinValue
        foreach ($line in Get-Content -LiteralPath $record -Encoding UTF8) {
            if ($line -match '^\S+\s+build_started\s+protocol=(\d+)\s+content=([0-9a-fA-F]{64})\s+output=(.+)$') {
                $recordedExe = $Matches[3].Trim()
                $identityStarted = ([int]$Matches[1] -eq $ProtocolVersion -and
                    $Matches[2].ToLowerInvariant() -eq $ContentHash.ToLowerInvariant() -and
                    (Test-SamePath $recordedExe $exe))
                $succeeded = $false
                $failedAfterStart = $false
                $completed = [DateTimeOffset]::MinValue
                continue
            }
            if ($line -match '^\S+\s+build_failed(?:\s|$)') {
                if ($identityStarted) { $failedAfterStart = $true }
                continue
            }
            if ($identityStarted -and $line -match '^(\S+)\s+build_succeeded\s+output=(.+)$' -and
                (Test-SamePath $Matches[2].Trim() $exe)) {
                $parsedCompleted = [DateTimeOffset]::MinValue
                if ([DateTimeOffset]::TryParse($Matches[1], [ref]$parsedCompleted)) {
                    $succeeded = $true
                    $completed = $parsedCompleted
                }
            }
        }
        if ($identityStarted -and $succeeded -and -not $failedAfterStart) {
            [PSCustomObject]@{ Path = $exe; Completed = $completed }
        }
    }
    return ($candidates | Sort-Object Completed -Descending | Select-Object -First 1).Path
}

function Get-JsonInteger($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return }
    $value = $property.Value
    if ($value -isnot [int] -and $value -isnot [long]) {
        $problems.Add("配置字段 $Name 必须为整数。")
        return
    }
    if ($value -lt [int]::MinValue -or $value -gt [int]::MaxValue) {
        $problems.Add("配置字段 $Name 超出 Int32 范围。")
        return
    }
    return [int]$value
}

try {
    $projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $assetsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'Assets')).TrimEnd('\')
    $explicitConfig = -not [string]::IsNullOrWhiteSpace($ConfigPath)
    $environmentConfig = $null
    if (-not $explicitConfig) {
        foreach ($scope in 'Process', 'User', 'Machine') {
            $value = [Environment]::GetEnvironmentVariable('RPG4_SERVER_CONFIG', $scope)
            if (-not [string]::IsNullOrWhiteSpace($value)) { $environmentConfig = $value; break }
        }
    }
    if ($explicitConfig) {
        $resolvedConfig = Get-AbsolutePath $ConfigPath $PSScriptRoot
    } elseif ($environmentConfig) {
        $resolvedConfig = Get-AbsolutePath $environmentConfig $projectRoot
    } else {
        $resolvedConfig = Get-AbsolutePath '配置/server.json' $PSScriptRoot
    }
    if (-not (Test-PathOutsideAssets $resolvedConfig $assetsRoot)) {
        $problems.Add('服务器配置必须位于项目 Assets 目录之外。')
    }

    $settings = [PSCustomObject]@{
        serverId = 'local-dev-01'; port = 7777; keyPath = 'identity/server-key.json'
        databaseConnectionEnvironmentVariable = 'RPG4_MYSQL_CONNECTION'
        maxPlayers = 20; maxConnections = 40; maxRawConnections = 60; maxRawConnectionsPerIp = 8
        minimumPlayers = 1; matchCountdownSeconds = 30; matchPreparationSeconds = 90; matchDurationSeconds = 1200
        reconnectGraceSeconds = 600; maxActiveAi = 30; aiDecisionIntervalMilliseconds = 100
        databaseTimeoutSeconds = 5; startupTimeoutSeconds = 90; shutdownTimeoutSeconds = 15; healthIntervalSeconds = 10
        passwordIterations = 600000; maxStoredPasswordIterations = 1200000; kdfConcurrency = 2; kdfQueueCapacity = 16
        maxPendingAuthentications = 32; rateWindowSeconds = 60; attemptsPerIp = 30; attemptsPerUsername = 8
        attemptsGlobal = 120; rateKeyCapacity = 4096
    }
    if (-not (Test-Path -LiteralPath $resolvedConfig -PathType Leaf)) {
        $problems.Add("缺少配置文件：$resolvedConfig")
    } else {
        try {
            $configFile = Get-Item -LiteralPath $resolvedConfig
            if ($configFile.Length -gt 65536) { throw 'Invalid configuration size' }
            $parsed = Get-Content -LiteralPath $resolvedConfig -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($null -eq $parsed -or $parsed -is [Array] -or $parsed -isnot [PSCustomObject]) { throw 'Invalid configuration object' }
            foreach ($name in 'serverId', 'keyPath', 'databaseConnectionEnvironmentVariable') {
                $property = $parsed.PSObject.Properties[$name]
                if ($null -ne $property) {
                    if ($property.Value -isnot [string]) { $problems.Add("配置字段 $name 必须为字符串。"); continue }
                    $settings.$name = $property.Value
                }
            }
            foreach ($name in 'port', 'maxPlayers', 'maxConnections', 'maxRawConnections', 'maxRawConnectionsPerIp',
                'minimumPlayers', 'matchCountdownSeconds', 'matchPreparationSeconds', 'matchDurationSeconds',
                'reconnectGraceSeconds', 'maxActiveAi', 'aiDecisionIntervalMilliseconds',
                'databaseTimeoutSeconds', 'startupTimeoutSeconds', 'shutdownTimeoutSeconds', 'healthIntervalSeconds',
                'passwordIterations', 'maxStoredPasswordIterations', 'kdfConcurrency', 'kdfQueueCapacity',
                'maxPendingAuthentications', 'rateWindowSeconds', 'attemptsPerIp', 'attemptsPerUsername',
                'attemptsGlobal', 'rateKeyCapacity') {
                $value = Get-JsonInteger $parsed $name
                if ($null -ne $parsed.PSObject.Properties[$name] -and $null -ne $value) { $settings.$name = $value }
            }
        } catch {
            $problems.Add('配置文件无法读取；请检查 JSON 格式、文件大小和字段类型。')
        }
    }

    if ([string]::IsNullOrWhiteSpace($settings.serverId) -or $settings.serverId.Length -gt 64 -or $settings.serverId -match '[^\x00-\x7F]') {
        $problems.Add('serverId 必须为 1 到 64 字节的 ASCII 字符串。')
    }
    if ($settings.port -lt 1 -or $settings.port -gt 65535) { $problems.Add('port 必须在 1 到 65535 之间。') }
    if ($settings.maxPlayers -lt 1 -or $settings.maxPlayers -gt 20 -or $settings.maxConnections -lt $settings.maxPlayers -or $settings.maxConnections -gt 4096 -or
        $settings.maxRawConnections -lt $settings.maxConnections -or $settings.maxRawConnections -gt 8192 -or
        $settings.maxRawConnectionsPerIp -lt 1 -or $settings.maxRawConnectionsPerIp -gt $settings.maxRawConnections) {
        $problems.Add('连接人数配置超出范围或 maxPlayers/maxConnections/maxRawConnections 关系无效。')
    }
    if ($settings.minimumPlayers -lt 1 -or $settings.minimumPlayers -gt 20 -or $settings.minimumPlayers -gt $settings.maxPlayers -or
        $settings.matchCountdownSeconds -lt 5 -or $settings.matchCountdownSeconds -gt 300 -or
        $settings.matchPreparationSeconds -lt 10 -or $settings.matchPreparationSeconds -gt 600 -or
        $settings.matchDurationSeconds -lt 60 -or $settings.matchDurationSeconds -gt 7200 -or
        $settings.reconnectGraceSeconds -lt 5 -or $settings.reconnectGraceSeconds -gt 900 -or
        $settings.reconnectGraceSeconds -gt $settings.matchDurationSeconds -or
        $settings.maxActiveAi -lt 1 -or $settings.maxActiveAi -gt 30 -or
        $settings.aiDecisionIntervalMilliseconds -lt 20 -or $settings.aiDecisionIntervalMilliseconds -gt 2000) {
        $problems.Add('单局/AI 配置超出范围或关系无效：minimumPlayers 不得超过 maxPlayers（最多 20），重连宽限期不得超过对局时长，maxActiveAi 必须为 1 到 30。')
    }
    if ($settings.databaseTimeoutSeconds -lt 1 -or $settings.databaseTimeoutSeconds -gt 10 -or $settings.startupTimeoutSeconds -lt 10 -or $settings.startupTimeoutSeconds -gt 300 -or
        $settings.shutdownTimeoutSeconds -lt 1 -or $settings.shutdownTimeoutSeconds -gt 60 -or $settings.healthIntervalSeconds -lt 2 -or $settings.healthIntervalSeconds -gt 300) {
        $problems.Add('databaseTimeoutSeconds、startupTimeoutSeconds、shutdownTimeoutSeconds 或 healthIntervalSeconds 超出 ServerConfiguration.Validate 范围。')
    }
    if ($settings.passwordIterations -lt 600000 -or $settings.maxStoredPasswordIterations -lt $settings.passwordIterations -or $settings.maxStoredPasswordIterations -gt 2000000 -or
        $settings.kdfConcurrency -lt 1 -or $settings.kdfConcurrency -gt 16 -or $settings.kdfQueueCapacity -lt 0 -or $settings.kdfQueueCapacity -gt 256 -or
        $settings.maxPendingAuthentications -lt 1 -or $settings.maxPendingAuthentications -gt 512) {
        $problems.Add('密码哈希或待认证队列配置超出 ServerConfiguration.Validate 范围。')
    }
    if ($settings.rateWindowSeconds -lt 1 -or $settings.rateWindowSeconds -gt 3600 -or $settings.attemptsPerIp -lt 1 -or $settings.attemptsPerUsername -lt 1 -or
        $settings.attemptsGlobal -lt 1 -or $settings.rateKeyCapacity -lt 64 -or $settings.rateKeyCapacity -gt 65536) {
        $problems.Add('登录限流配置超出 ServerConfiguration.Validate 范围。')
    }
    if ([string]::IsNullOrWhiteSpace($settings.keyPath)) { $problems.Add('keyPath 不能为空。') }
    if ([string]::IsNullOrWhiteSpace($settings.databaseConnectionEnvironmentVariable) -or
        $settings.databaseConnectionEnvironmentVariable -notmatch '^[A-Za-z_][A-Za-z0-9_]*$') {
        $problems.Add('databaseConnectionEnvironmentVariable 必须为有效的环境变量名。')
    }
    $keyPath = Get-AbsolutePath $settings.keyPath ([IO.Path]::GetDirectoryName($resolvedConfig))
    if (-not (Test-PathOutsideAssets $keyPath $assetsRoot)) {
        $problems.Add('服务器密钥必须位于项目 Assets 目录之外。')
    } elseif (-not (Test-Path -LiteralPath $keyPath -PathType Leaf)) {
        $problems.Add("缺少已有服务器密钥：$keyPath（不会自动生成或替换）")
    }

    $variableName = $settings.databaseConnectionEnvironmentVariable
    $connection = $null
    if ($variableName -match '^[A-Za-z_][A-Za-z0-9_]*$') {
        foreach ($scope in 'Process', 'User', 'Machine') {
            $value = [Environment]::GetEnvironmentVariable($variableName, $scope)
            if (-not [string]::IsNullOrWhiteSpace($value)) { $connection = $value; break }
        }
        if ([string]::IsNullOrWhiteSpace($connection)) { $problems.Add("缺少数据库环境变量：$variableName（已检查 Process、User、Machine）") }
    }

    $protocolSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Assets/Scripts/Networking/Contracts/EntryProtocol.cs') -Raw -Encoding UTF8
    $ruleSource = Get-Content -LiteralPath (Join-Path $projectRoot 'Assets/Scripts/Networking/EntryContentManifest.cs') -Raw -Encoding UTF8
    $versionMatch = [regex]::Match($protocolSource, 'const\s+uint\s+Version\s*=\s*(\d+)\s*;')
    $ruleMatch = [regex]::Match($ruleSource, 'const\s+string\s+GenerationRule\s*=\s*"([^"]+)"\s*;')
    $manifestPath = Join-Path $assetsRoot 'Settings/EntryContentManifest.asset'
    if (-not $versionMatch.Success -or -not $ruleMatch.Success -or -not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        $problems.Add('无法读取协议版本、清单生成规则或 EntryContentManifest.asset。')
    } else {
        $sourceProtocol = [int]$versionMatch.Groups[1].Value
        $sourceRule = $ruleMatch.Groups[1].Value
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
        $manifestProtocolMatch = [regex]::Match($manifest, '(?m)^\s*protocolVersion:\s*(\d+)\s*$')
        $manifestHashMatch = [regex]::Match($manifest, '(?m)^\s*contentHash:\s*([0-9a-fA-F]{64})\s*$')
        $manifestRuleMatch = [regex]::Match($manifest, '(?m)^\s*generationRule:\s*(\S+)\s*$')
        if (-not $manifestProtocolMatch.Success -or -not $manifestHashMatch.Success -or -not $manifestRuleMatch.Success) {
            $problems.Add('内容清单的 protocolVersion、contentHash 或 generationRule 缺失/格式无效。')
        } else {
            $protocolVersion = [int]$manifestProtocolMatch.Groups[1].Value
            $contentHash = $manifestHashMatch.Groups[1].Value.ToLowerInvariant()
            if ($protocolVersion -ne $sourceProtocol -or $manifestRuleMatch.Groups[1].Value -cne $sourceRule) {
                $problems.Add("内容清单与源码身份不一致（源码 protocol=$sourceProtocol rule=$sourceRule；清单 protocol=$protocolVersion rule=$($manifestRuleMatch.Groups[1].Value)）。请在 Unity 中执行 Generate Authority Content Manifest、Validate Entry Configuration，然后重建 Windows Dedicated Server。")
            }
        }
    }

    $requestedServer = $null
    if (-not [string]::IsNullOrWhiteSpace($ServerPath)) { $requestedServer = Get-AbsolutePath $ServerPath $PSScriptRoot }
    $resolvedServer = $null
    if ($null -ne $protocolVersion -and $null -ne $contentHash) {
        $resolvedServer = Find-ServerBuild $protocolVersion $contentHash $requestedServer
    }
    if ([string]::IsNullOrWhiteSpace($resolvedServer)) {
        if ($requestedServer) {
            $problems.Add('指定服务器未匹配当前 protocol/hash 的成功 buildlog 记录和完整输出文件；不能用 -ServerPath 绕过身份校验。')
        } else {
            $problems.Add('未找到与当前 protocol/hash 匹配、成功记录和运行文件齐全的服务器构建。')
        }
    }

    if ($problems.Count -gt 0) {
        Write-Host '启动预检失败，未启动服务器：' -ForegroundColor Red
        foreach ($problem in $problems) { Write-Host "  - $problem" }
        exit 1
    }
    Write-Host "服务器：$resolvedServer"
    Write-Host "配置：$resolvedConfig"
    Write-Host ("单局参数：玩家 {0}-{1}，倒计时 {2}s，准备 {3}s，对局 {4}s，重连宽限 {5}s；AI {6} 个，决策间隔 {7}ms" -f
        $settings.minimumPlayers, $settings.maxPlayers, $settings.matchCountdownSeconds, $settings.matchPreparationSeconds,
        $settings.matchDurationSeconds, $settings.reconnectGraceSeconds, $settings.maxActiveAi, $settings.aiDecisionIntervalMilliseconds)
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
        [Environment]::SetEnvironmentVariable($variableName, $connection, 'Process')
        $process = Start-Process -FilePath $resolvedServer -ArgumentList $arguments -WorkingDirectory ([IO.Path]::GetDirectoryName($resolvedServer)) -WindowStyle Hidden -PassThru
    } finally {
        [Environment]::SetEnvironmentVariable($variableName, $previousValue, 'Process')
    }
    Write-Host "进程已启动，PID：$($process.Id)，等待服务器初始化。"
    Write-Host "日志：$logPath"
    $deadline = [DateTime]::UtcNow.AddSeconds([int]$settings.startupTimeoutSeconds + 15)
    $entryReady = $false
    $listening = $false
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) {
            Write-Host "服务器进程已退出（PID：$($process.Id)，退出码：$($process.ExitCode)）。日志：$logPath" -ForegroundColor Red
            exit 1
        }
        if (Test-Path -LiteralPath $logPath -PathType Leaf) {
            try {
                $runtimeLog = Get-Content -LiteralPath $logPath -Raw -Encoding UTF8
                if ($runtimeLog -match ("Dedicated server entry protocol={0}\s+content={1}\s+rule={2}\." -f $protocolVersion, [regex]::Escape($contentHash), [regex]::Escape($sourceRule))) { $entryReady = $true }
                if ($runtimeLog -match ("Server listening on port {0}(?:\D|$)" -f $settings.port)) { $listening = $true }
                $phaseMatches = [regex]::Matches($runtimeLog, 'Dedicated server phase=([A-Za-z]+)\.')
                $ready = ($phaseMatches.Count -gt 0 -and $phaseMatches[$phaseMatches.Count - 1].Groups[1].Value -ceq 'Ready')
            } catch { }
        }
        if ($entryReady -and $listening -and $ready) {
            Write-Host "服务器已就绪，PID：$($process.Id)，端口：$($settings.port)。日志：$logPath" -ForegroundColor Green
            exit 0
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "等待服务器就绪超时（PID：$($process.Id)）；进程保持运行。请查看日志：$logPath" -ForegroundColor Red
    exit 1
} catch {
    if ($process) {
        Write-Host "启动检查/等待失败（PID：$($process.Id)）；进程未自动停止。日志：$logPath" -ForegroundColor Red
    } else {
        Write-Host '启动脚本执行失败；请检查路径、配置格式及本地文件/环境变量读取权限。' -ForegroundColor Red
    }
    exit 1
}
