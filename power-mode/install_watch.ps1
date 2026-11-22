# Register (or remove) the scheduled task PowerManager-ModeWatch: runs `power_mode.py --watch`
# in the current user's session from logon (no admin needed). In normal mode the watcher only sleeps.
# Keep this file ASCII (Windows PowerShell 5.1).
param([switch]$Uninstall)

$name = 'PowerManager-ModeWatch'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$py = 'D:\miniforge3\pythonw.exe'
$script = Join-Path $here 'power_mode.py'
$user = [Security.Principal.WindowsIdentity]::GetCurrent().Name

if ($Uninstall) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    Write-Output "removed $name"
    return
}

$xml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PowerManager power-mode watcher (run mode on battery: sleep fallback, low battery, hot battery). See Desktop\PowerManager\power-mode\README.md.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>$user</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>$user</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$py</Command><Arguments>"$script" --watch</Arguments><WorkingDirectory>$here</WorkingDirectory></Exec>
  </Actions>
</Task>
"@

Register-ScheduledTask -TaskName $name -Xml $xml -Force | Out-Null
Start-ScheduledTask -TaskName $name
$t = Get-ScheduledTask -TaskName $name
Write-Output ("registered {0}: state={1}" -f $name, $t.State)
