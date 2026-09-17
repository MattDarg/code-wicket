<#
.SYNOPSIS
    Runs the offline post-implementation gates concurrently, each isolated from the others.

.DESCRIPTION
    Build ONCE, then fan out. The build is the only step that cannot overlap — every gate reads
    the same bin/, and two `dotnet build`s over one tree fight over the same intermediate and
    output files — so it runs serially first and the matrix runs against the finished binaries,
    which are read-only for the rest of the run.

    Isolation, per gate:

      * Its own CWKT_SCRATCH_DIR (see run-smoke.ps1 -ScratchDir). That one lever covers everything
        an automated Desktop run persists: the redirected config.json, the attachments dir, the
        sessions store the headless modes delete on startup, crash.txt, and the result artifact.
      * Its own PROCESS. Not a runspace: ForEach-Object -Parallel shares $env: across runspaces in
        one process, so the scratch override would leak between gates and defeat itself. Start-Job
        would also do, but a plain child pwsh keeps the existing Start-Process/WaitForExit
        convention and puts each gate's whole console output in a file you can open.
      * Its own stdout/stderr log under the gate root.

    What is genuinely shared, and why each is fine:

      * The built binaries — read-only once the build has finished. Hence: never rebuild while a
        matrix is in flight (the same rule --perf carries). Replacing assemblies mid-run gives a
        long run that exits 0 and writes no report at all; 4m14s has been measured.
      * The log directory (%LOCALAPPDATA%\code-wicket\logs) — untouched, because the Desktop
        host opens no tee in the automated modes and the render log is off there by design. A gate
        that DOES tee would braid runs into one file and needs solving before it is added here.
      * The screen — not shared at all: the screenshot modes rasterise through
        RenderTargetBitmap, off-screen, so concurrent windows cannot occlude or steal focus from
        one another the way a screen-capture harness would.

    Gates are reported in a table at the end and the exit code is the number that failed, so this
    is usable as a single command before launching Visual Studio.

.PARAMETER Gates
    Which gates to run. Default: tests, smoke-net10, smoke-net472, screenshot.
    Also available: any screenshot-* mode (as its own gate name), and perf-net472 / perf-net10.

    perf is NOT in the default set on purpose — it is a measurement run, not a gate (it always
    exits 0), and it takes 4-7 minutes. Ask for it when the question is performance.

    screenshot-held is not in the default set either, for a different reason: it hangs on SHUTDOWN
    intermittently (3 of 4 runs measured 2026-08-22, at 20s/60s/180s ceilings). It is the WORK that
    completes — both PNGs are written every time, freshly, with no error file — so the mode does its
    job and then fails to exit, which the timeout then reports as a failure of the whole gate. Not
    caused by running it in parallel: it reproduces standalone, sequential, with no scratch override.
    Left out rather than papered over with a longer timeout, since a gate that is red 3 runs in 4
    trains you to ignore the table.

.PARAMETER Configuration
    Debug (default) | Release

.PARAMETER NoBuild
    Skip the serial build and run the matrix against whatever is already in bin/.

.PARAMETER MaxParallel
    How many gates may run at once (default 4). Each Desktop gate is a real WPF process; the cap
    is about not thrashing the box, not about correctness.

.PARAMETER TimeoutSeconds
    Per-gate ceiling (default 180; a perf gate gets 1800 regardless, matching run-smoke.ps1).

.NOTES
    Requires PowerShell 7 (pwsh). Enforced at the top of the script, because under Windows
    PowerShell 5.1 the run does not fail — it reports every gate as FAIL with an empty exit code
    while each gate's own log says PASS.

.EXAMPLE
    ./scripts/run-gates.ps1
    ./scripts/run-gates.ps1 -NoBuild
    ./scripts/run-gates.ps1 -Gates tests,smoke-net10,smoke-net472,screenshot,screenshot-held
    ./scripts/run-gates.ps1 -Gates perf-net472
#>
[CmdletBinding()]
param(
    [string[]]$Gates = @('tests', 'smoke-net10', 'smoke-net472', 'screenshot'),
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [int]$MaxParallel = 4,
    [int]$TimeoutSeconds = 180
)

# PowerShell 7 (pwsh), not Windows PowerShell 5.1 — and this is a HARD failure rather than a note in
# the help, because 5.1 does not error, it LIES: every gate comes back "FAIL ()" with an empty exit
# code while its own log says PASS. A red table over a green run is the worst of both, and the third
# instance of the family AGENTS.md already records twice ("the exit code proves nothing and the
# artifact is the result", for --perf and for the screenshot modes).
#
# Two independent 5.1 dependencies, both measured rather than inferred:
#   * Start-Process -PassThru with redirected streams yields a Process whose ExitCode is EMPTY once
#     it exits, though HasExited is True. Measured: 5.1 gives HasExited=True ExitCode=[], pwsh 7
#     gives ExitCode=[3] for the same cmd.exe /c exit 3. That is what turns every gate red.
#   * Process.Kill(bool entireProcessTree) does not exist in .NET Framework, so the TIMEOUT path
#     throws into its own catch and a timed-out gate's process tree is left running. Silent, and
#     only reachable on the day a gate hangs.
#
# Checked here rather than with `#requires -Version 7.0` so the message can say what actually goes
# wrong; the engine's own wording names a version and not a symptom.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "run-gates.ps1 requires PowerShell 7 (pwsh); this is $($PSVersionTable.PSVersion). " +
          "Under 5.1 every gate reports FAIL with an empty exit code while its log says PASS, and a " +
          "timed-out gate is not killed. Re-run with: pwsh -NoProfile -File scripts/run-gates.ps1"
}

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$smokeScript = Join-Path $PSScriptRoot 'run-smoke.ps1'
$gateRoot = Join-Path $repoRoot "src\CodeWicket.Desktop\bin\gates"

# Translate a gate name into how it is launched. Kept as data rather than a switch in the runner so
# adding a gate is one row, and so the table below can name every gate before any of them starts.
function Resolve-Gate([string]$name) {
    if ($name -eq 'tests') {
        return [pscustomobject]@{
            Name = $name; Exe = 'dotnet'; Timeout = $TimeoutSeconds
            Args = @('test', (Join-Path $repoRoot 'src\CodeWicket.Tests\CodeWicket.Tests.csproj'),
                     '-c', $Configuration, '--no-build', '--nologo')
        }
    }

    # smoke-net10 / smoke-net472 / perf-net472 / screenshot / screenshot-held / ...
    $mode = $name; $framework = 'net10.0-windows'
    if ($name -match '^(smoke|perf)-net472$') { $mode = $Matches[1]; $framework = 'net472' }
    elseif ($name -match '^(smoke|perf)-net10$') { $mode = $Matches[1]; $framework = 'net10.0-windows' }

    $timeout = if ($mode -eq 'perf') { 1800 } else { $TimeoutSeconds }
    return [pscustomobject]@{
        Name = $name; Exe = 'pwsh'; Timeout = $timeout
        Args = @('-NoProfile', '-File', $smokeScript,
                 '-Mode', $mode, '-Framework', $framework, '-Configuration', $Configuration,
                 '-TimeoutSeconds', "$timeout",
                 '-ScratchDir', (Join-Path $gateRoot "$name\scratch"),
                 '-Full')
    }
}

$plan = $Gates | ForEach-Object { Resolve-Gate $_ }

if (-not $NoBuild) {
    Write-Host "Building CodeWicket.slnx ($Configuration) ..." -ForegroundColor Cyan
    $buildStart = Get-Date
    # -warnaserror because the release pipeline builds with it and this is the gate that stands in
    # for the pipeline offline. Without it a warning is emitted, scrolls past, and breaks the build
    # only on the tag push: a `string testId = null` in a Nullable-enable test project (CS8625) was
    # green here and failed the release build. The house target is 0 warn / 0 err either way, so the
    # flag costs nothing that wasn't already meant to be fixed.
    & dotnet build (Join-Path $repoRoot 'CodeWicket.slnx') -c $Configuration -warnaserror
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE) - matrix not started." }
    Write-Host ("Build OK in {0:n0}s" -f ((Get-Date) - $buildStart).TotalSeconds) -ForegroundColor Green
}

Write-Host ""
Write-Host "Gates: $($plan.Name -join ', ')  (max $MaxParallel at once)" -ForegroundColor Cyan
Write-Host "Logs:  $gateRoot" -ForegroundColor DarkGray
Write-Host ""

$queue = [System.Collections.Generic.Queue[object]]::new()
$plan | ForEach-Object { $queue.Enqueue($_) }
$running = [System.Collections.Generic.List[object]]::new()
$done = [System.Collections.Generic.List[object]]::new()
$matrixStart = Get-Date

function Start-Gate($gate) {
    $dir = Join-Path $gateRoot $gate.Name
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $out = Join-Path $dir 'gate.log'
    $err = Join-Path $dir 'gate.err.log'
    # Start-Process needs two distinct files; a shared one throws.
    $p = Start-Process -FilePath $gate.Exe -ArgumentList $gate.Args -PassThru -NoNewWindow `
        -RedirectStandardOutput $out -RedirectStandardError $err
    Write-Host ("  -> {0,-22} started (pid {1})" -f $gate.Name, $p.Id) -ForegroundColor DarkGray
    return [pscustomobject]@{
        Gate = $gate; Proc = $p; Started = Get-Date; Out = $out; Err = $err
    }
}

while ($queue.Count -gt 0 -or $running.Count -gt 0) {
    while ($running.Count -lt $MaxParallel -and $queue.Count -gt 0) {
        $running.Add((Start-Gate $queue.Dequeue()))
    }

    Start-Sleep -Milliseconds 250

    foreach ($r in @($running)) {
        $elapsed = ((Get-Date) - $r.Started).TotalSeconds
        if ($r.Proc.HasExited) {
            $r | Add-Member Elapsed $elapsed -Force
            $r | Add-Member Code $r.Proc.ExitCode -Force
            $done.Add($r); $running.Remove($r) | Out-Null
            $colour = if ($r.Code -eq 0) { 'Green' } else { 'Red' }
            Write-Host ("  <- {0,-22} {1} in {2:n0}s" -f $r.Gate.Name,
                $(if ($r.Code -eq 0) { 'PASS' } else { "FAIL (exit $($r.Code))" }), $elapsed) -ForegroundColor $colour
        }
        elseif ($elapsed -gt $r.Gate.Timeout) {
            try { $r.Proc.Kill($true) } catch { }
            $r | Add-Member Elapsed $elapsed -Force
            $r | Add-Member Code -1 -Force
            $r | Add-Member TimedOut $true -Force
            $done.Add($r); $running.Remove($r) | Out-Null
            Write-Host ("  <- {0,-22} TIMEOUT after {1:n0}s" -f $r.Gate.Name, $elapsed) -ForegroundColor Red
        }
    }
}

$wall = ((Get-Date) - $matrixStart).TotalSeconds
$failed = @($done | Where-Object { $_.Code -ne 0 })

Write-Host ""
Write-Host ("=" * 64)
foreach ($r in $done | Sort-Object { $_.Gate.Name }) {
    $status = if ($r.PSObject.Properties['TimedOut']) { 'TIMEOUT' }
              elseif ($r.Code -eq 0) { 'PASS' } else { "FAIL ($($r.Code))" }
    Write-Host ("{0,-24} {1,-12} {2,6:n0}s" -f $r.Gate.Name, $status, $r.Elapsed) `
        -ForegroundColor $(if ($r.Code -eq 0) { 'Green' } else { 'Red' })
}
Write-Host ("=" * 64)

# Serial cost is the sum; the matrix cost is the slowest chain. Print both, because the whole
# point of this script is the difference and it is the one number that says whether it is working.
$serial = ($done | Measure-Object -Property Elapsed -Sum).Sum
Write-Host ("{0}/{1} passed in {2:n0}s wall ({3:n0}s if run serially)" -f `
    ($done.Count - $failed.Count), $done.Count, $wall, $serial) `
    -ForegroundColor $(if ($failed.Count -eq 0) { 'Green' } else { 'Red' })

foreach ($r in $failed) {
    Write-Host ""
    Write-Host "--- $($r.Gate.Name) ---" -ForegroundColor Red
    if (Test-Path $r.Out) { Get-Content $r.Out -Tail 40 | ForEach-Object { Write-Host "  $_" } }
    # Get-Content -Raw on a ZERO-BYTE file emits nothing at all - not $null, not '' - so an
    # if-expression assigning it produces no output and the variable lands null. A cast inside the
    # branch does not save it (there is no value for the cast to apply to). An empty stderr is the
    # normal case for a gate that failed on its own terms, so this threw on nearly every failure -
    # i.e. exactly and only in the path that exists to explain a failure. Guard with a static that
    # accepts null rather than calling a method on it.
    $errText = if (Test-Path $r.Err) { Get-Content $r.Err -Raw } else { $null }
    if (-not [string]::IsNullOrWhiteSpace($errText)) {
        Write-Host "  [stderr] $($errText.Trim())" -ForegroundColor DarkRed
    }
    Write-Host "  full log: $($r.Out)" -ForegroundColor DarkGray
}

exit $failed.Count
