[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [string]$ServerPath
)

$ErrorActionPreference = 'Stop'
$failed = $false
$matched = 0
$buildRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '构建/ServerWindows')).TrimEnd('\')
$selectedPath = $null

function Test-ProjectServerPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    $fullPath = [IO.Path]::GetFullPath($Path)
    return ($fullPath.StartsWith($buildRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($fullPath).Equals('RPG4Server.exe', [StringComparison]::OrdinalIgnoreCase))
}

try {
    if ($ServerPath) {
        if (-not [IO.Path]::IsPathRooted($ServerPath)) {
            $ServerPath = Join-Path $PSScriptRoot $ServerPath
        }
        $selectedPath = [IO.Path]::GetFullPath($ServerPath)
        if (-not (Test-ProjectServerPath $selectedPath)) {
            throw 'ServerPath 必须指向本项目 构建/ServerWindows 目录内的 RPG4Server.exe。'
        }
    }

    # 按名称只筛选候选；必须核验完整路径后才允许停止。
    $candidates = @(Get-Process -Name 'RPG4Server' -ErrorAction SilentlyContinue)
    foreach ($serverProcess in $candidates) {
        try {
            if ($serverProcess.HasExited) { continue }
            # 提前持有原进程句柄，后续操作沿用同一 Process 对象，避免 PID 重用误伤。
            $null = $serverProcess.Handle
            $processPath = $serverProcess.MainModule.FileName
            if (-not (Test-ProjectServerPath $processPath)) { continue }
            $processPath = [IO.Path]::GetFullPath($processPath)
            if ($selectedPath -and -not $selectedPath.Equals($processPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $startedAt = $serverProcess.StartTime
            $matched++
            Write-Host ("{0} PID={1} 路径={2}" -f $(if ($CheckOnly) { '[仅检查]' } else { '[停止]' }), $serverProcess.Id, $processPath)
            if ($CheckOnly) { continue }
            if ($serverProcess.HasExited) {
                Write-Host '进程已退出。'
                continue
            }
            if ($serverProcess.StartTime -ne $startedAt -or
                -not $processPath.Equals($serverProcess.MainModule.FileName, [StringComparison]::OrdinalIgnoreCase)) {
                throw '进程身份发生变化，已取消停止。'
            }
            if ($serverProcess.CloseMainWindow()) {
                Write-Host '已请求关闭主窗口，最多等待 75 秒。'
                if ($serverProcess.WaitForExit(75000)) {
                    Write-Host '进程已退出。'
                    continue
                }
            }
            if ($serverProcess.HasExited) {
                Write-Host '进程已退出。'
                continue
            }
            Write-Host '进程没有可关闭的主窗口或未及时退出，将强制终止；不保证完成对局结算或保存。'
            $serverProcess.Kill()
            if (-not $serverProcess.WaitForExit(5000)) { throw '强制终止后进程仍未退出。' }
            Write-Host '进程已终止。'
        } catch {
            if ($serverProcess.HasExited) {
                Write-Host ("PID={0} 已退出。" -f $serverProcess.Id)
            } else {
                $failed = $true
                Write-Warning ("PID={0} 无法核验或停止：{1}" -f $serverProcess.Id, $_.Exception.Message)
            }
        } finally {
            $serverProcess.Dispose()
        }
    }
    if ($matched -eq 0) { Write-Host '未找到匹配的本项目服务器进程，无需停止。' }
} catch {
    $failed = $true
    Write-Warning $_.Exception.Message
}

if ($failed) { exit 1 }
exit 0
