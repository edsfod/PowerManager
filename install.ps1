# PowerManager installer. Run from an elevated (admin) PowerShell:
#   powershell -ExecutionPolicy Bypass -File install.ps1             install / update everything
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall  remove tasks, shortcuts, toast registration
#                                                                     (run `agent\agent.py --restore-original` first)
# What it does:
#   1. task Battery-ChargeLimit: EC charge threshold, admin, S4U ("whether logged on or not"); triggers:
#      boot, logon, resume (Power-Troubleshooter 1), power source change (Kernel-Power 105); the agent
#      starts it for everything else. No clock trigger (REQUIREMENTS.md R19).
#   2. task PowerManager-Agent: the resident agent in the user's session from logon, restarted on failure.
#   3. removes the old task PowerManager-ModeWatch and its 30-second watcher.
#   4. registers the toast sender "PowerManager" (HKCU\Software\Classes\AppUserModelId\PowerManager).
#   5. one shortcut PowerManager.lnk (desktop + Start menu) instead of Battery Charge.lnk and Power Mode.lnk;
#      the Start menu one carries AppUserModelID "PowerManager" (without it toasts are not shown).
# Keep this file ASCII (Windows PowerShell 5.1).
param([switch]$Uninstall, [string]$Python = '')

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Python) {
    $cmd = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($cmd) { $Python = $cmd.Source }
}
if (-not $Python -or -not (Test-Path $Python)) {
    Write-Output 'python.exe not found on PATH: pass -Python <path\to\python.exe>'
    exit 1
}
$pyExe = (Resolve-Path $Python).Path
$pywCandidate = Join-Path (Split-Path -Parent $pyExe) 'pythonw.exe'
$py = if (Test-Path $pywCandidate) { $pywCandidate } else { $pyExe }
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Output 'not elevated: open Terminal (Admin) and run this again'
    exit 1
}
$user = $id.Name
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) ''
$aumidKey = 'HKCU:\Software\Classes\AppUserModelId\PowerManager'

function Remove-Task($name) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}

function Stop-Script($pattern) {
    Get-CimInstance Win32_Process -Filter "Name='pythonw.exe'" |
        Where-Object { $_.CommandLine -like $pattern } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force; Write-Output "stopped pid $($_.ProcessId) $pattern" }
}

# old pieces, removed in both directions
Remove-Task 'PowerManager-ModeWatch'
Stop-Script '*power_mode.py*--watch*'
foreach ($dir in @($desktop, $startMenu)) {
    foreach ($n in @('Battery Charge.lnk', 'Power Mode.lnk')) {
        $p = Join-Path $dir $n
        if (Test-Path $p) { Remove-Item $p; Write-Output "removed $p" }
    }
}

if ($Uninstall) {
    Remove-Task 'PowerManager-Agent'
    Stop-Script '*PowerManager\agent\agent.py*'
    Remove-Task 'Battery-ChargeLimit'
    foreach ($dir in @($desktop, $startMenu)) { Remove-Item (Join-Path $dir 'PowerManager.lnk') -ErrorAction SilentlyContinue }
    Remove-Item $aumidKey -Recurse -ErrorAction SilentlyContinue
    Write-Output 'uninstalled'
    return
}

$settings = @"
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
"@
$ev = { param($provider, $eventId)
    "<EventTrigger><Enabled>true</Enabled><Subscription>&lt;QueryList&gt;&lt;Query Id=`"0`" Path=`"System`"&gt;&lt;Select Path=`"System`"&gt;*[System[Provider[@Name='$provider'] and EventID=$eventId]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription></EventTrigger>"
}

# 1. EC task
$bc = Join-Path $root 'battery-charge'
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PowerManager: Huawei MateBook EC charge threshold (75%-80% + temporary boost). Event-driven; also started by PowerManager-Agent. See Desktop\PowerManager\battery-charge\README.md.</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger><Enabled>true</Enabled></BootTrigger>
    <LogonTrigger><Enabled>true</Enabled><UserId>$user</UserId></LogonTrigger>
    $(& $ev 'Microsoft-Windows-Power-Troubleshooter' 1)
    $(& $ev 'Microsoft-Windows-Kernel-Power' 105)
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>$user</UserId><LogonType>S4U</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
$settings
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$py</Command><Arguments>"$bc\battery_charge.py" --task</Arguments><WorkingDirectory>$bc</WorkingDirectory></Exec>
  </Actions>
</Task>
"@
Register-ScheduledTask -TaskName 'Battery-ChargeLimit' -Xml $xml -Force | Out-Null
Write-Output 'registered Battery-ChargeLimit'

# 2. agent
$ag = Join-Path $root 'agent'
$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PowerManager resident agent: power modes, lid, sleep fallbacks, wake notifications. Waits for Windows power notifications, no polling. See Desktop\PowerManager\README.md.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>$user</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>$user</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure><Interval>PT1M</Interval><Count>5</Count></RestartOnFailure>
$settings
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$py</Command><Arguments>"$ag\agent.py"</Arguments><WorkingDirectory>$ag</WorkingDirectory></Exec>
  </Actions>
</Task>
"@
Stop-ScheduledTask -TaskName 'PowerManager-Agent' -ErrorAction SilentlyContinue
Stop-Script '*PowerManager\agent\agent.py*'
Register-ScheduledTask -TaskName 'PowerManager-Agent' -Xml $xml -Force | Out-Null
Write-Output 'registered PowerManager-Agent'

# 4. toast sender
New-Item -Path $aumidKey -Force | Out-Null
Set-ItemProperty -Path $aumidKey -Name DisplayName -Value 'PowerManager'
Set-ItemProperty -Path $aumidKey -Name IconUri -Value (Join-Path $root 'ui\icon.png')

# 5. shortcut
$sh = New-Object -ComObject WScript.Shell
foreach ($dir in @($desktop, $startMenu)) {
    $l = $sh.CreateShortcut((Join-Path $dir 'PowerManager.lnk'))
    $l.TargetPath = $py
    $l.Arguments = '"' + (Join-Path $root 'ui\ui.py') + '"'
    $l.WorkingDirectory = Join-Path $root 'ui'
    $l.IconLocation = (Join-Path $root 'ui\icon.ico') + ',0'
    $l.Description = 'PowerManager: power mode and charge boost'
    $l.Save()
}
# toasts from an unpackaged app are only displayed if a Start menu shortcut carries its AppUserModelID
& $pyExe (Join-Path $root 'common\shortcut.py') (Join-Path $startMenu 'PowerManager.lnk') 'PowerManager'
Write-Output 'shortcuts PowerManager.lnk (desktop, Start menu)'

Start-ScheduledTask -TaskName 'Battery-ChargeLimit'
Start-ScheduledTask -TaskName 'PowerManager-Agent'
Start-Sleep -Seconds 3
foreach ($n in @('Battery-ChargeLimit', 'PowerManager-Agent')) {
    $t = Get-ScheduledTask -TaskName $n
    $i = $t | Get-ScheduledTaskInfo
    Write-Output ("{0}: state={1}, triggers={2}, last result=0x{3:x}" -f $n, $t.State, $t.Triggers.Count, $i.LastTaskResult)
}
