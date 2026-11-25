# PowerManager-v2 unified Framework-Dependent publish script (ADR-0005 / ADR-0006).
# Publishes all 4 executables into a single directory sharing common DLLs:
#   PowerManager.Agent.exe
#   PowerManager.BatteryCharge.exe
#   PowerManager.UI.exe
#   pmctl.exe
# Keep this file ASCII for Windows PowerShell 5.1 compatibility.
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputDir = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutputDir) {
    $OutputDir = Join-Path $repoRoot 'dist'
}

if (Test-Path $OutputDir) {
    # Running binaries lock files in the output directory; fail early with instructions instead of
    # a half-deleted dist/. Processes whose path cannot be read (e.g. elevated) are skipped.
    $fullOutputDir = [IO.Path]::GetFullPath($OutputDir).TrimEnd('\') + '\'
    $running = @(Get-Process -Name 'PowerManager.Agent', 'PowerManager.UI', 'PowerManager.BatteryCharge', 'pmctl' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($fullOutputDir, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        $names = ($running | ForEach-Object { "$($_.ProcessName) (pid $($_.Id))" }) -join ', '
        throw ("Processes are running from ${OutputDir}: $names. Close the PowerManager window and run " +
            "'Stop-ScheduledTask -TaskName PowerManager-Agent; Stop-Process -Name PowerManager.Agent' " +
            "in an elevated PowerShell first (see README: upgrade).")
    }
    Remove-Item -Path $OutputDir -Recurse -Force
}
New-Item -Path $OutputDir -ItemType Directory -Force | Out-Null

$projects = @(
    'src\PowerManager.Agent\PowerManager.Agent.csproj',
    'src\PowerManager.BatteryCharge\PowerManager.BatteryCharge.csproj',
    'src\PowerManager.UI\PowerManager.UI.csproj',
    'src\PowerManager.Cli\PowerManager.Cli.csproj'
)

Write-Output "Publishing PowerManager-v2 ($Configuration, $Runtime, framework-dependent) -> $OutputDir"

foreach ($proj in $projects) {
    $fullProj = Join-Path $repoRoot $proj
    Write-Output "  -> dotnet publish $proj"
    & dotnet publish $fullProj `
        -c $Configuration `
        -r $Runtime `
        --self-contained false `
        -o $OutputDir `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $proj (exit code $LASTEXITCODE)"
    }
}

# Copy icon assets used by shortcuts and WinRT Toast IconUri
Copy-Item (Join-Path $repoRoot 'src\PowerManager.UI\Assets\icon.ico') (Join-Path $OutputDir 'icon.ico') -Force
Copy-Item (Join-Path $repoRoot 'src\PowerManager.UI\Assets\icon.png') (Join-Path $OutputDir 'icon.png') -Force

# Remove unused WPF WebView2 dll if pulled transiently
$unusedWpf = Join-Path $OutputDir 'Microsoft.Web.WebView2.Wpf.dll'
if (Test-Path $unusedWpf) {
    Remove-Item $unusedWpf -Force
}

$exes = @('PowerManager.Agent.exe', 'PowerManager.BatteryCharge.exe', 'PowerManager.UI.exe', 'pmctl.exe')
foreach ($exe in $exes) {
    $p = Join-Path $OutputDir $exe
    if (-not (Test-Path $p)) {
        throw "Expected executable missing after publish: $p"
    }
}

$totalBytes = (Get-ChildItem -Path $OutputDir -Recurse -File | Measure-Object -Property Length -Sum).Sum
$totalMb = [Math]::Round($totalBytes / 1MB, 2)
Write-Output "Publish complete: $OutputDir ($totalMb MB total)"
