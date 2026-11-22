# Register (or remove) the scheduled task Battery-ChargeLimit for the current user.
# Run from an elevated (admin) PowerShell: root\wmi OemWMIMethod requires admin, so the task
# runs with HighestAvailable privileges.
# LogonType S4U = "run whether the user is logged on or not" without storing a password, plus a
# boot trigger: after a reboot the EC threshold comes back as 95-100, and the machine may sit at
# the lock screen for hours (2026-11-11: Windows Update reboot at 17:13, logon at 20:43, charged to 100%).
# Keep this file ASCII (Windows PowerShell 5.1).
param([switch]$Uninstall)

$name = 'Battery-ChargeLimit'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$py = 'D:\miniforge3\pythonw.exe'
$script = Join-Path $here 'battery_charge.py'

$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Output 'not elevated: open Terminal (Admin) and run this again'
    exit 1
}

if ($Uninstall) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "removed $name"
    return
}
$user = $id.Name
$nowIso = (Get-Date).AddMinutes(1).ToString('yyyy-MM-ddTHH:mm:ss')

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Huawei MateBook EC battery charge threshold manager (75%-80% baseline + temporary boost). See Desktop\PowerManager\battery-charge\README.md.</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger><Enabled>true</Enabled></BootTrigger>
    <LogonTrigger><Enabled>true</Enabled><UserId>$user</UserId></LogonTrigger>
    <EventTrigger><Enabled>true</Enabled>
      <Subscription>&lt;QueryList&gt;&lt;Query Id="0" Path="System"&gt;&lt;Select Path="System"&gt;*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription>
    </EventTrigger>
    <TimeTrigger><StartBoundary>$nowIso</StartBoundary><Enabled>true</Enabled>
      <Repetition><Interval>PT5M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>
    </TimeTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>$user</UserId><LogonType>S4U</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$py</Command><Arguments>"$script" --task</Arguments><WorkingDirectory>$here</WorkingDirectory></Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $name -Xml $xml -Force | Out-Null
Start-ScheduledTask -TaskName $name
Start-Sleep -Seconds 2
$t = Get-ScheduledTask -TaskName $name
Write-Output ("registered {0}: state={1}, triggers={2}" -f $name, $t.State, $t.Triggers.Count)
