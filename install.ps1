# PowerManager-v2 installer (Windows PowerShell 5.1 / PowerShell 7+, ASCII).
# Run from an elevated (Administrator) PowerShell:
#   powershell -ExecutionPolicy Bypass -File install.ps1
#   powershell -ExecutionPolicy Bypass -File install.ps1 -ImportOriginalFrom "C:\Users\...\PowerManager\agent\var\original.json"
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall
#
# What it registers (docs/design.md & ADR-0006):
#   1. Scheduled task "Battery-ChargeLimit": S4U highest privileges, PT2M, Queue, battery allowed,
#      triggers: Boot, Logon, Power-Troubleshooter 1, Kernel-Power 105. Passes explicit --data-dir.
#   2. Scheduled task "PowerManager-Agent": InteractiveToken least privilege, PT0S, IgnoreNew,
#      battery allowed, restart 1m x 5, trigger: Logon. Passes explicit --data-dir.
#   3. HKCU\Software\Classes\AppUserModelId\PowerManager for unpackaged WinRT Toast notifications.
#   4. Desktop + Start Menu PowerManager.lnk pointing to PowerManager.UI.exe, with System.AppUserModel.ID
#      set on the Start Menu shortcut so Windows displays Toast banners.
param(
    [switch]$Uninstall,
    [string]$DistDir = '',
    [string]$DataDir = '',
    [string]$ImportOriginalFrom = '',
    [switch]$ForceImport,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $DistDir) {
    $DistDir = Join-Path $repoRoot 'dist'
}
if (-not $DataDir) {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    if (-not $localAppData) {
        throw 'Cannot resolve %LOCALAPPDATA%; pass -DataDir explicitly.'
    }
    $DataDir = Join-Path $localAppData 'PowerManager'
}
$DataDir = [IO.Path]::GetFullPath($DataDir)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Not elevated: please run this script from an Administrator PowerShell.'
    exit 1
}

$user = $identity.Name
$desktop = [Environment]::GetFolderPath('Desktop')
$startMenu = [Environment]::GetFolderPath('Programs')
$aumid = 'PowerManager'
$aumidKey = "HKCU:\Software\Classes\AppUserModelId\$aumid"

function Remove-TaskIfExists([string]$name) {
    Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}

function Stop-AgentProcess {
    Get-Process -Name 'PowerManager.Agent' -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        Write-Output "Stopped PowerManager.Agent (pid $($_.Id))"
    }
}

function Set-ShortcutAppUserModelId([string]$lnkPath, [string]$appUserModelId) {
    if (-not ('PowerManagerShortcutHelper' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

public static class PowerManagerShortcutHelper
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public IntPtr p2;
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        int GetCount(out uint cProps);
        int GetAt(uint iProp, out PROPERTYKEY pkey);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        int Commit();
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pvar);

    public static void SetAumid(string lnkPath, string aumid)
    {
        var link = new ShellLink();
        var persist = (IPersistFile)link;
        persist.Load(lnkPath, 2); // STGM_READWRITE

        var store = (IPropertyStore)link;
        var key = new PROPERTYKEY
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5 // System.AppUserModel.ID
        };

        var pv = new PROPVARIANT
        {
            vt = 31, // VT_LPWSTR
            p = Marshal.StringToCoTaskMemUni(aumid)
        };

        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref pv));
            Marshal.ThrowExceptionForHR(store.Commit());
            persist.Save(lnkPath, true);
        }
        finally
        {
            PropVariantClear(ref pv);
            Marshal.ReleaseComObject(link);
        }
    }
}
'@
    }

    [PowerManagerShortcutHelper]::SetAumid($lnkPath, $appUserModelId)
}

if ($Uninstall) {
    Remove-TaskIfExists 'PowerManager-Agent'
    Stop-AgentProcess
    Remove-TaskIfExists 'Battery-ChargeLimit'
    foreach ($dir in @($desktop, $startMenu)) {
        $lnk = Join-Path $dir 'PowerManager.lnk'
        if (Test-Path $lnk) {
            Remove-Item $lnk -Force -ErrorAction SilentlyContinue
            Write-Output "Removed $lnk"
        }
    }
    Remove-Item $aumidKey -Recurse -Force -ErrorAction SilentlyContinue
    Write-Output 'Uninstalled PowerManager-v2 scheduled tasks, shortcuts, and AUMID registration.'
    Write-Output "Runtime data preserved at: $DataDir"
    return
}

$DistDir = [IO.Path]::GetFullPath($DistDir)
$agentExe = Join-Path $DistDir 'PowerManager.Agent.exe'
$bcExe = Join-Path $DistDir 'PowerManager.BatteryCharge.exe'
$uiExe = Join-Path $DistDir 'PowerManager.UI.exe'
$pmctlExe = Join-Path $DistDir 'pmctl.exe'
$iconIco = Join-Path $DistDir 'icon.ico'
$iconPng = Join-Path $DistDir 'icon.png'

foreach ($req in @($agentExe, $bcExe, $uiExe, $pmctlExe)) {
    if (-not (Test-Path $req)) {
        throw "Missing published binary: $req. Run scripts\publish.ps1 first."
    }
}

# Prepare runtime data directory layout
foreach ($sub in @('', 'agent', 'battery-charge', 'ui', 'requests')) {
    $d = if ($sub) { Join-Path $DataDir $sub } else { $DataDir }
    New-Item -Path $d -ItemType Directory -Force | Out-Null
}

# Optional read-only import of original.json (e.g., from v1)
if ($ImportOriginalFrom) {
    if (-not (Test-Path $ImportOriginalFrom)) {
        throw "ImportOriginalFrom file not found: $ImportOriginalFrom"
    }
    $targetOriginal = Join-Path $DataDir 'agent\original.json'
    if (-not (Test-Path $targetOriginal) -or $ForceImport) {
        Copy-Item -Path $ImportOriginalFrom -Destination $targetOriginal -Force
        Write-Output "Imported original.json (read-only copy) -> $targetOriginal"
    }
    else {
        Write-Output "Skipped import: $targetOriginal already exists (pass -ForceImport to overwrite)."
    }
}

$xmlEscape = { param([string]$s) [Security.SecurityElement]::Escape($s) }
$escapedUser = & $xmlEscape $user
$escapedDist = & $xmlEscape $DistDir
$escapedDataDir = & $xmlEscape $DataDir
$escapedBcExe = & $xmlEscape $bcExe
$escapedAgentExe = & $xmlEscape $agentExe

$commonSettings = @"
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled>
"@

$eventTrigger = {
    param([string]$provider, [int]$eventId)
    "<EventTrigger><Enabled>true</Enabled><Subscription>&lt;QueryList&gt;&lt;Query Id=`"0`" Path=`"System`"&gt;&lt;Select Path=`"System`"&gt;*[System[Provider[@Name='$provider'] and EventID=$eventId]]&lt;/Select&gt;&lt;/Query&gt;&lt;/QueryList&gt;</Subscription></EventTrigger>"
}

# 1. Battery-ChargeLimit (S4U elevated)
$bcXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PowerManager-v2: Huawei MateBook EC charge threshold (75%-80% + temporary boost). Event-driven S4U task.</Description>
  </RegistrationInfo>
  <Triggers>
    <BootTrigger><Enabled>true</Enabled></BootTrigger>
    <LogonTrigger><Enabled>true</Enabled><UserId>$escapedUser</UserId></LogonTrigger>
    $(& $eventTrigger 'Microsoft-Windows-Power-Troubleshooter' 1)
    $(& $eventTrigger 'Microsoft-Windows-Kernel-Power' 105)
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>$escapedUser</UserId><LogonType>S4U</LogonType><RunLevel>HighestAvailable</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>Queue</MultipleInstancesPolicy>
    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>
$commonSettings
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$escapedBcExe</Command><Arguments>--data-dir "$escapedDataDir"</Arguments><WorkingDirectory>$escapedDist</WorkingDirectory></Exec>
  </Actions>
</Task>
"@
Register-ScheduledTask -TaskName 'Battery-ChargeLimit' -Xml $bcXml -Force | Out-Null
Write-Output 'Registered scheduled task: Battery-ChargeLimit'

# 2. PowerManager-Agent (Interactive least privilege)
$agentXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>PowerManager-v2 resident agent: power modes, lid, sleep fallbacks, wake notifications. Event-driven, no polling.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger><Enabled>true</Enabled><UserId>$escapedUser</UserId></LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author"><UserId>$escapedUser</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure><Interval>PT1M</Interval><Count>5</Count></RestartOnFailure>
$commonSettings
  </Settings>
  <Actions Context="Author">
    <Exec><Command>$escapedAgentExe</Command><Arguments>--data-dir "$escapedDataDir"</Arguments><WorkingDirectory>$escapedDist</WorkingDirectory></Exec>
  </Actions>
</Task>
"@
Stop-ScheduledTask -TaskName 'PowerManager-Agent' -ErrorAction SilentlyContinue
Stop-AgentProcess
Register-ScheduledTask -TaskName 'PowerManager-Agent' -Xml $agentXml -Force | Out-Null
Write-Output 'Registered scheduled task: PowerManager-Agent'

# 3. Toast AUMID registration
New-Item -Path $aumidKey -Force | Out-Null
Set-ItemProperty -Path $aumidKey -Name 'DisplayName' -Value 'PowerManager'
if (Test-Path $iconPng) {
    Set-ItemProperty -Path $aumidKey -Name 'IconUri' -Value $iconPng
}
Write-Output "Registered Toast AUMID: $aumidKey"

# 4. Shortcuts (Desktop + Start Menu with System.AppUserModel.ID)
$shell = New-Object -ComObject WScript.Shell
$uiArgs = "--data-dir `"$DataDir`""
foreach ($dir in @($desktop, $startMenu)) {
    $lnkPath = Join-Path $dir 'PowerManager.lnk'
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath = $uiExe
    $lnk.Arguments = $uiArgs
    $lnk.WorkingDirectory = $DistDir
    if (Test-Path $iconIco) {
        $lnk.IconLocation = "$iconIco,0"
    }
    $lnk.Description = 'PowerManager: power mode and charge boost'
    $lnk.Save()
}
Set-ShortcutAppUserModelId (Join-Path $startMenu 'PowerManager.lnk') $aumid
Write-Output 'Created shortcuts: PowerManager.lnk (Desktop + Start Menu with AppUserModelID)'

if (-not $NoStart) {
    Start-ScheduledTask -TaskName 'Battery-ChargeLimit'
    Start-ScheduledTask -TaskName 'PowerManager-Agent'
    Start-Sleep -Seconds 3
    foreach ($n in @('Battery-ChargeLimit', 'PowerManager-Agent')) {
        $t = Get-ScheduledTask -TaskName $n
        $info = $t | Get-ScheduledTaskInfo
        Write-Output ("{0}: state={1}, triggers={2}, last result=0x{3:x}" -f $n, $t.State, $t.Triggers.Count, $info.LastTaskResult)
    }
}
else {
    Write-Output 'Skipped starting scheduled tasks (-NoStart).'
}
