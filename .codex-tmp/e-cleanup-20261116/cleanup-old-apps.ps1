[CmdletBinding()]
param([ValidateSet('Preflight','Execute')][string]$Mode='Preflight')
$ErrorActionPreference='Stop'
$work='E:\github\RPG-2\.codex-tmp\e-cleanup-20261116'
$planPath=Join-Path $work 'old-apps-plan.json'
$targets=@(
    [pscustomobject]@{Name='WPS-old';Path='E:\WPS\WPS Office\12.1.0.29161';Current='E:\WPS\WPS Office\12.1.0.29690';Exe='E:\WPS\WPS Office\12.1.0.29690\office6\wps.exe';Version='12.1.0.29690';ProcessPattern='^(wps.*|wpp|et|kso.*|wtoolex.*|ksolaunch)\.exe$'},
    [pscustomobject]@{Name='WeChat-old';Path='E:\腾讯\WeChat';Current='E:\腾讯\weixin';Exe='E:\腾讯\weixin\Weixin.exe';Version='4.1.1.19';ProcessPattern='^(WeChat.*|Weixin.*)\.exe$'}
)
function Assert-NormalPath([string]$path,[string]$expected) {
    $full=[IO.Path]::GetFullPath($path).TrimEnd('\')
    if(-not [string]::Equals($full,$expected,[StringComparison]::OrdinalIgnoreCase)){throw "Path not allowlisted: $full"}
    $node=$full
    while($node){
        $item=Get-Item -LiteralPath $node -Force
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point: $node"}
        $parent=[IO.Directory]::GetParent($node)
        if($null -eq $parent){break};$node=$parent.FullName
    }
}
function Get-Inventory($target) {
    Assert-NormalPath $target.Path $target.Path
    $files=[Collections.Generic.List[object]]::new()
    $directories=[Collections.Generic.List[string]]::new()
    $suspect=[Collections.Generic.List[string]]::new()
    $templates=[Collections.Generic.List[string]]::new()
    $stack=[Collections.Generic.Stack[string]]::new();$stack.Push($target.Path)
    $bytes=0L
    while($stack.Count){
        $dir=$stack.Pop();$directories.Add($dir)
        foreach($item in Get-ChildItem -LiteralPath $dir -Force){
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "Reparse point: $($item.FullName)"}
            $relative=$item.FullName.Substring($target.Path.Length+1)
            if($item.PSIsContainer){
                if($item.Name -match '(?i)^(Msg|FileStorage|WeChat Files|Weixin Files|wxid_.+|db_storage|account|userdata|user_data|Documents|聊天记录|个人文件)$'){$suspect.Add($relative)}
                $stack.Push($item.FullName)
            }else{
                $bytes+=$item.Length
                $files.Add([pscustomobject]@{Path=$item.FullName;Length=$item.Length;LastWriteTimeUtc=$item.LastWriteTimeUtc.ToString('o')})
                if($item.Extension -match '(?i)^\.(db|db3|sqlite|sqlite3|db-wal|db-shm|doc|docx|xls|xlsx|ppt|pptx|pdf|bak)$'){
                    $builtin=$target.Name -eq 'WPS-old' -and ($relative -match '^office6\\mui\\(zh_CN|default)\\templates\\' -or $relative -match '^office6\\mui\\(zh_CN|default)\\resource\\xlsx\\Kingsoft_Et_[^\\]+\.xlsx$' -or $relative -match '^office6\\mui\\(zh_CN|default)\\resource\\watermark\\information\\preinstall\\[^\\]+\.doc$' -or $relative -match '^office6\\mui\\(zh_CN|default)\\resource\\watermark\\information\\user_defined\\addwatermark_buildin_wps\.doc$' -or $relative -match '^office6\\addons\\knewdocs\\res\\blanktemplate\\(blank_official\.docx|normal(_black|_gray)?\.pptx)$')
                    if($builtin){$templates.Add($relative)}else{$suspect.Add($relative)}
                }
            }
        }
    }
    [pscustomobject]@{Bytes=$bytes;Files=$files.ToArray();Directories=$directories.ToArray();SuspectPersonalData=$suspect.ToArray();BuiltInTemplates=$templates.ToArray();ReparsePoints=0}
}
function Get-Registrations {
    $roots=@('HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall','HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall')
    foreach($root in $roots){
        if(Test-Path -LiteralPath $root){foreach($key in Get-ChildItem -LiteralPath $root){
            $p=Get-ItemProperty -LiteralPath $key.PSPath
            if($p.DisplayName -match '(?i)WPS|WeChat|Weixin|微信'){
                [pscustomobject]@{Key=$key.Name;DisplayName=$p.DisplayName;DisplayVersion=$p.DisplayVersion;InstallLocation=$p.InstallLocation;UninstallString=$p.UninstallString;DisplayIcon=$p.DisplayIcon}
            }
        }}
    }
}
function Get-OperationalReferences($target) {
    $escaped=[regex]::Escape($target.Path)
    $refs=[Collections.Generic.List[object]]::new()
    foreach($s in Get-CimInstance Win32_Service){if($s.PathName -match $escaped){$refs.Add([pscustomobject]@{Kind='Service';Location=$s.Name;Value=$s.PathName})}}
    foreach($s in Get-CimInstance Win32_StartupCommand){if($s.Command -match $escaped){$refs.Add([pscustomobject]@{Kind='Startup';Location=$s.Location;Value=$s.Command})}}
    foreach($task in Get-ScheduledTask){foreach($a in $task.Actions){$s="$($a.Execute) $($a.Arguments) $($a.WorkingDirectory)";if($s -match $escaped){$refs.Add([pscustomobject]@{Kind='ScheduledTask';Location="$($task.TaskPath)$($task.TaskName)";Value=$s})}}}
    $regRoots=@('HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths','HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths','HKCU:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\Microsoft\Windows\CurrentVersion\Run','HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run','HKCU:\Software\Microsoft\Windows\CurrentVersion\RunOnce','HKLM:\Software\Microsoft\Windows\CurrentVersion\RunOnce')
    foreach($root in $regRoots){if(Test-Path -LiteralPath $root){
        $keys=@(Get-Item -LiteralPath $root)+@(Get-ChildItem -LiteralPath $root -Recurse)
        foreach($key in $keys){foreach($name in $key.GetValueNames()){$v=[string]$key.GetValue($name);if($v -match $escaped){$refs.Add([pscustomobject]@{Kind='Registry';Location="$($key.Name)\$name";Value=$v})}}}
    }}
    $shell=New-Object -ComObject WScript.Shell
    $shortcutRoots=@([Environment]::GetFolderPath('DesktopDirectory'),[Environment]::GetFolderPath('CommonDesktopDirectory'),[Environment]::GetFolderPath('StartMenu'),[Environment]::GetFolderPath('CommonStartMenu'),[Environment]::GetFolderPath('Startup'),[Environment]::GetFolderPath('CommonStartup')) | Select-Object -Unique
    foreach($root in $shortcutRoots){if($root -and (Test-Path -LiteralPath $root)){
        foreach($link in Get-ChildItem -LiteralPath $root -Filter '*.lnk' -File -Recurse -Force){
            $s=$shell.CreateShortcut($link.FullName);$v="$($s.TargetPath) $($s.Arguments) $($s.WorkingDirectory)"
            if($v -match $escaped){$refs.Add([pscustomobject]@{Kind='Shortcut';Location=$link.FullName;Value=$v})}
        }
    }}
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
    $refs.ToArray()
}
function Get-BackupClassUseEvidence {
    $backup='KWPS.PDF.9.ksobak'
    $uses=[Collections.Generic.List[object]]::new()
    foreach($root in @('HKCU:\Software\Classes','HKLM:\Software\Classes')){
        foreach($extension in Get-ChildItem -LiteralPath $root | Where-Object {$_.PSChildName.StartsWith('.')}){
            if([string]$extension.GetValue('') -eq $backup){$uses.Add([pscustomobject]@{Kind='ExtensionDefault';Key=$extension.Name;Value=$backup})}
            $open=Join-Path $extension.PSPath 'OpenWithProgids'
            if(Test-Path -LiteralPath $open){$k=Get-Item -LiteralPath $open;if($k.GetValueNames() -contains $backup){$uses.Add([pscustomobject]@{Kind='ExtensionOpenWithProgids';Key=$k.Name;Value=$backup})}}
        }
    }
    $fileExts='HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts'
    foreach($extension in Get-ChildItem -LiteralPath $fileExts){
        $choice=Join-Path $extension.PSPath 'UserChoice'
        if(Test-Path -LiteralPath $choice){$k=Get-Item -LiteralPath $choice;if([string]$k.GetValue('ProgId') -eq $backup){$uses.Add([pscustomobject]@{Kind='UserChoice';Key=$k.Name;Value=$backup})}}
        $open=Join-Path $extension.PSPath 'OpenWithProgids'
        if(Test-Path -LiteralPath $open){$k=Get-Item -LiteralPath $open;if($k.GetValueNames() -contains $backup){$uses.Add([pscustomobject]@{Kind='UserOpenWithProgids';Key=$k.Name;Value=$backup})}}
    }
    $pdfChoice=(Get-Item -LiteralPath (Join-Path $fileExts '.pdf\UserChoice')).GetValue('ProgId')
    $currentCommand=(Get-Item -LiteralPath 'HKCU:\Software\Classes\KWPS.PDF.9\shell\open\command').GetValue('')
    [pscustomobject]@{BackupClass=$backup;ActualReferences=$uses.ToArray();PdfUserChoice=$pdfChoice;CurrentWpsPdfOpenCommand=$currentCommand;Scope='Only extension default values, OpenWithProgids value names, UserChoice ProgId, and current WPS PDF command; no account or secret values read.'}
}
function Get-Safety($target) {
    $inventory=Get-Inventory $target
    Assert-NormalPath $target.Current $target.Current
    $currentExe=Get-Item -LiteralPath $target.Exe -Force
    if($currentExe.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Current executable is a reparse point'}
    $regs=@(Get-Registrations)
    $expected=if($target.Name -eq 'WPS-old'){@($regs | Where-Object {$_.DisplayVersion -eq $target.Version -and $_.UninstallString -like "$($target.Current)*"})}else{@($regs | Where-Object {$_.InstallLocation.Trim('"') -eq $target.Current})}
    if($expected.Count -ne 1){throw 'Current installation registration changed or missing'}
    $active=@(Get-CimInstance Win32_Process | Where-Object { $_.Name -match $target.ProcessPattern -or ($_.ExecutablePath -and ($_.ExecutablePath.StartsWith($target.Path+'\',[StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith($target.Current+'\',[StringComparison]::OrdinalIgnoreCase))) } | Select-Object ProcessId,Name,ExecutablePath)
    $references=@(Get-OperationalReferences $target)
    $classUse=if($target.Name -eq 'WPS-old'){Get-BackupClassUseEvidence}else{$null}
    $backupInUse=$null -ne $classUse -and @($classUse.ActualReferences).Count -gt 0
    [pscustomobject]@{Name=$target.Name;Path=$target.Path;Current=$target.Current;CurrentExe=$target.Exe;CurrentExeVersion=$currentExe.VersionInfo.FileVersion;RegisteredCurrent=$expected;Inventory=$inventory;ActiveProcesses=$active;OperationalReferences=$references;BackupClassUseEvidence=$classUse;Safe=($inventory.SuspectPersonalData.Count -eq 0 -and $active.Count -eq 0 -and $references.Count -eq 0 -and -not $backupInUse)}
}
if(-not (Test-Path -LiteralPath $work)){New-Item -ItemType Directory -Path $work | Out-Null}
if($Mode -eq 'Preflight'){
    $results=@(foreach($target in $targets){try{Get-Safety $target}catch{[pscustomobject]@{Name=$target.Name;Path=$target.Path;Safe=$false;Error=$_.Exception.Message}}})
    [pscustomobject]@{CreatedUtc=[DateTime]::UtcNow.ToString('o');Stage='PreflightOnly';Deleted=$false;Targets=$results;RegistryClassPathEvidence=@();RegistryEvidenceReviewed=$false;Note='Targeted operational and backup-class usage checks are complete. Existing backup-class registry values are preserved. Execute requires reviewed evidence and fresh checks.'} | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $planPath -Encoding UTF8
    $results | ForEach-Object {[pscustomobject]@{Name=$_.Name;Safe=$_.Safe;Bytes=$_.Inventory.Bytes;Files=$_.Inventory.Files.Count;Directories=$_.Inventory.Directories.Count;PersonalDataCandidates=$_.Inventory.SuspectPersonalData.Count;References=$_.OperationalReferences.Count;Error=$_.Error}} | ConvertTo-Json -Depth 4
    exit
}
$plan=Get-Content -LiteralPath $planPath -Raw | ConvertFrom-Json
if($plan.Stage -ne 'PreflightOnly' -or $plan.Deleted -or $plan.RegistryEvidenceReviewed -ne $true){throw 'Missing reviewed preflight or reviewed registry evidence'}
$outcomes=[Collections.Generic.List[object]]::new()
foreach($target in $targets){
    try{
        $reviewed=@($plan.Targets | Where-Object {$_.Path -eq $target.Path})
        if($reviewed.Count -ne 1 -or -not $reviewed[0].Safe){throw 'Plan did not approve this exact target'}
        $fresh=Get-Safety $target
        if(-not $fresh.Safe){throw 'Fresh safety check failed'}
        if($fresh.Inventory.Bytes -ne $reviewed[0].Inventory.Bytes -or $fresh.Inventory.Files.Count -ne $reviewed[0].Inventory.Files.Count){throw 'Inventory changed since review'}
        $reviewedFiles=@{};foreach($f in $reviewed[0].Inventory.Files){$reviewedFiles[$f.Path]=$f}
        foreach($f in $fresh.Inventory.Files){
            $old=$reviewedFiles[$f.Path]
            if($null -eq $old -or $f.Length -ne $old.Length){throw "File changed since review: $($f.Path)"}
            $freshTicks=([datetimeoffset]$f.LastWriteTimeUtc).UtcDateTime.Ticks
            $reviewedTicks=([datetimeoffset]$old.LastWriteTimeUtc).UtcDateTime.Ticks
            if($freshTicks -ne $reviewedTicks){throw "File changed since review: $($f.Path)"}
        }
        Assert-NormalPath $target.Path $target.Path
        # Delete individual literal files and then empty directories. Do not recurse during deletion.
        foreach($file in $fresh.Inventory.Files){
            Assert-NormalPath ([IO.Path]::GetDirectoryName($file.Path)) ([IO.Path]::GetDirectoryName($file.Path))
            $item=Get-Item -LiteralPath $file.Path -Force
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw "New reparse point: $($file.Path)"}
            if(-not $file.Path.StartsWith($target.Path+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'File escaped target'}
            [IO.File]::Delete($file.Path)
        }
        foreach($dir in ($fresh.Inventory.Directories | Sort-Object Length -Descending)){
            Assert-NormalPath $dir $dir
            if($dir -ne $target.Path -and -not $dir.StartsWith($target.Path+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Directory escaped target'}
            [IO.Directory]::Delete($dir,$false)
        }
        $outcomes.Add([pscustomobject]@{Path=$target.Path;Status='Deleted';Bytes=$fresh.Inventory.Bytes;TargetAbsent=(-not (Test-Path -LiteralPath $target.Path));CurrentExeExists=(Test-Path -LiteralPath $target.Exe)})
    }catch{$outcomes.Add([pscustomobject]@{Path=$target.Path;Status='FailedOrSkipped';Error=$_.Exception.Message;TargetExists=(Test-Path -LiteralPath $target.Path)})}
}
$log=Join-Path $work ('old-apps-execution-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
[pscustomobject]@{TimeUtc=[DateTime]::UtcNow.ToString('o');Mode=$Mode;Results=$outcomes.ToArray()} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $log -Encoding UTF8
$outcomes.ToArray() | ConvertTo-Json -Depth 6
