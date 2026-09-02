# nav.ps1 — convenience wrapper for flaxmcp-nav daemon (community standalone).
#
# Usage:
#   pwsh nav.ps1 csharp/find_definition symbolName=Foo
#   pwsh nav.ps1 csharp/symbol_search query=Bridge
#
# Community default: FLAXMCP_NAV_AUTOSPAWN=1 (auto-spawn if missing).
# Set FLAXMCP_NAV_AUTOSPAWN=0 for connect-only (factory default).
# Daemon log: %TEMP%/flaxmcp-nav/daemon.log
#
# What it does:
#   1. Sends the query to running daemon or auto-spawns (if enabled).
#   2. Prints the JSON response.

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$Exe = $null
$candidates = @(
    (Join-Path $ScriptDir 'bin\Release\net8.0\flaxmcp-nav.exe')
    (Join-Path $ScriptDir 'bin\Debug\net8.0\flaxmcp-nav.exe')
)
$existing = $candidates | Where-Object { Test-Path $_ }
if ($existing) {
    $Exe = ($existing | Get-Item | Sort-Object @{Expression='LastWriteTimeUtc'; Descending=$true}, @{Expression={[array]::IndexOf($candidates, $_.FullName)}; Ascending=$true})[0].FullName
}

if (-not $Exe) {
    # also check beside script (release zip layout)
    $beside = Join-Path $ScriptDir 'flaxmcp-nav.exe'
    if (Test-Path $beside) { $Exe = $beside }
}

if (-not $Exe) {
    Write-Error "flaxmcp-nav.exe not built. Run: dotnet build $ScriptDir/flaxmcp-nav.csproj"
    exit 99
}

if ($args.Count -ge 1 -and $args[0] -eq '--shutdown') {
    $force = ($args -contains '--force')
    & $Exe --shutdown --callerPid $PID @(if ($force) { '--force' })
    exit $LASTEXITCODE
}

$DaemonVerbs = @('--status', '--health', '--version', '--help', '-h')
if ($args.Count -ge 1 -and ($DaemonVerbs -contains $args[0])) {
    & $Exe @args
    exit $LASTEXITCODE
}

# Community default: auto-spawn unless explicitly disabled.
# Set FLAXMCP_NAV_AUTOSPAWN=0 to force connect-only.
$autospawnEnv = $env:FLAXMCP_NAV_AUTOSPAWN
if ($null -eq $autospawnEnv -or $autospawnEnv -eq '') { $autospawn = $true }
elseif ($autospawnEnv -eq '1') { $autospawn = $true }
elseif ($autospawnEnv -eq '0') { $autospawn = $false }
else { $autospawn = $autospawnEnv -eq '1' }

if (-not $autospawn) {
    & $Exe @args
    $code = $LASTEXITCODE
    if ($code -ne 0 -and $code -ne 3) {
        $proc = Get-Process -Name flaxmcp-nav -ErrorAction SilentlyContinue
        if ($proc) {
            & $Exe --health *> $null
            if ($LASTEXITCODE -eq 0) {
                Write-Host "[nav] daemon is UP but this query did not complete — retry in ~10-20s." -ForegroundColor Yellow
            } else {
                Write-Host "[nav] daemon process exists but is not yet accepting. Retry in a few seconds." -ForegroundColor Yellow
            }
        } else {
            Write-Host "[nav] no daemon running. Start with: flaxmcp-nav.exe --daemon (or set FLAXMCP_NAV_AUTOSPAWN=1)" -ForegroundColor Yellow
        }
    }
    exit $code
}

& $Exe --auto-start @args
exit $LASTEXITCODE
