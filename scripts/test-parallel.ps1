<#
.SYNOPSIS
  Runs the backend test suite as three dotnet test processes side by side.

.DESCRIPTION
  The tests that start a whole API host (PosHost) run one at a time inside a process: a host is
  configured through the process environment, so two cannot be built at once in the same process
  (Hosting/PosHost.cs, HostCollection). They were the whole run's critical path, about 24 minutes.
  Separate processes have separate environments, so here the suite is cut into three groups by
  namespace, each holding about a third of the host tests, and the three run in parallel.

  The third group is "everything not in the first two", so a namespace added later still runs, and
  runs once. Each process needs about 1 GB of memory: on a machine short of it, use -Processes 1
  (the whole suite in one process, as `dotnet test` does).

.EXAMPLE
  pwsh scripts/test-parallel.ps1
  pwsh scripts/test-parallel.ps1 -NoBuild
  pwsh scripts/test-parallel.ps1 -Processes 1
#>
param(
    [ValidateSet(1, 3)][int]$Processes = 3,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'ParadisERP.Tests/ParadisERP.Tests.csproj'
$results = Join-Path ([IO.Path]::GetTempPath()) ("algeriapos-tests-" + [Guid]::NewGuid().ToString('n').Substring(0, 8))
New-Item -ItemType Directory -Path $results | Out-Null

if (-not $NoBuild) {
    Write-Host '==> Building the tests' -ForegroundColor Cyan
    dotnet build $project -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'The build failed.' }
}

# Balanced on the host tests' time (2026-11-12): about 8 minutes of them in each group.
$groupA = 'Security', 'Hr', 'Stock', 'Suppliers', 'Receivables', 'Ledger'
$groupB = 'Customers', 'Treasury', 'Persistence'
function Any([string[]]$names) { ($names | ForEach-Object { "FullyQualifiedName~ParadisERP.Tests.$_." }) -join '|' }
function None([string[]]$names) { ($names | ForEach-Object { "FullyQualifiedName!~ParadisERP.Tests.$_." }) -join '&' }

$groups = if ($Processes -eq 1) { [ordered]@{ all = $null } } else {
    [ordered]@{
        a = Any $groupA
        b = Any $groupB
        c = None ($groupA + $groupB)
    }
}

$started = Get-Date
Write-Host "==> Running $($groups.Count) test process(es); output in $results" -ForegroundColor Cyan
$jobs = foreach ($name in $groups.Keys) {
    $log = Join-Path $results "$name.log"
    $arguments = @('test', $project, '--no-build', '-nologo', '--results-directory', (Join-Path $results $name))
    if ($groups[$name]) { $arguments += @('--filter', $groups[$name]) }
    [pscustomobject]@{
        Name    = $name
        Log     = $log
        Process = Start-Process dotnet -ArgumentList $arguments -NoNewWindow -PassThru `
            -RedirectStandardOutput $log -RedirectStandardError "$log.err"
    }
}
$jobs | ForEach-Object { $_.Process.WaitForExit() }

$failed = $false
foreach ($job in $jobs) {
    $summary = Select-String -Path $job.Log -Pattern '^(Passed|Failed)!' | Select-Object -Last 1
    $failures = Select-String -Path $job.Log -Pattern '^\s+Failed ' | ForEach-Object { $_.Line.Trim() }
    if (-not $summary -or $job.Process.ExitCode -ne 0) { $failed = $true }
    Write-Host ("[{0}] {1}" -f $job.Name, ($(if ($summary) { $summary.Line.Trim() } else { "no summary (exit $($job.Process.ExitCode)); see $($job.Log)" })))
    $failures | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
}
Write-Host ("==> {0:mm\:ss} in all" -f ((Get-Date) - $started))
if ($failed) { exit 1 }

