<#
.SYNOPSIS
    Runs a headless Desktop self-check (offline verification before launching Visual Studio).

.DESCRIPTION
    Launches CodeWicket.Desktop in one of its headless modes and waits for the GUI
    process to exit (a bare `& exe` returns before the WPF app closes, so this uses
    Start-Process -PassThru + WaitForExit per the AGENTS.md convention). Prints the
    exit code and the mode's result artifact (smoke-result.txt / *.png path).

    A FAILING run is also APPENDED to smoke-failures.log beside the artifact, with a
    timestamp and the framework. That log is the point of this script's error handling:
    smoke-result.txt is overwritten by every run, so a later passing run destroys the
    evidence from a failing one — and re-running to find out whether a failure was
    intermittent is precisely what overwrites it. The appending log survives that, and
    it accumulates, so an intermittent failure builds a record rather than a rumour.

    It does NOT survive a clean of bin/, which is acceptable: it exists to outlive the
    next run, not the next rebuild.

.PARAMETER Mode
    smoke (default) | perf | screenshot | screenshot-history | screenshot-resume | screenshot-edit
    screenshot-flagged | screenshot-typing | screenshot-held | screenshot-attachment | screenshot-permission
    screenshot-subagents | screenshot-drilldown | screenshot-cli-sessions | screenshot-debug-context
    screenshot-session-info | screenshot-mcp-rows

    perf sweeps the transcript size and times the message-box-growth and permission-banner
    gestures at each, separating layout cost from rasterisation cost.

.PARAMETER Build
    Rebuild CodeWicket.slnx before running (the fake provider lives in the engine
    exe, so a code change needs a rebuild to take effect).

.PARAMETER Configuration
    Debug (default) | Release

.PARAMETER Framework
    net10.0-windows (default) | net472

    The Desktop host multi-targets, and net472 is the slice devenv actually loads (issue #106): the
    net472 build of CodeWicket.UI, its BAML and theming, the Markdig.Signed / ColorCode / Emoji.Wpf
    closure, and the VS-SDK-baseline StreamJsonRpc that CodeWicket.Shell pins only there. A net472
    smoke run is therefore strictly more coverage than the net10 one, not a duplicate of it, and it is
    the only offline check that touches the shipping slice at all.

    For perf it matters for a second reason: devenv is net472, so #86's VDI sluggishness report is
    about that framework. A net10 sweep measures a framework no user runs.

.PARAMETER TimeoutSeconds
    How long to wait for the headless run to exit (default 90; perf defaults to PerfDefaultTimeout
    below, since a full sweep runs for many minutes on either TFM).

.PARAMETER ScratchDir
    Base scratch directory for this run, exported as CWKT_SCRATCH_DIR for the child process
    (HostScratch.ResolveDir reads it). Default: <binDir>\scratch, i.e. next to the binary.

    This is the isolation lever for running several checks AT ONCE, and it is sufficient because
    EVERY piece of per-run state an automated mode touches hangs off that one base: the redirected
    config.json (whose chatZoom writes are the reason the redirect exists at all), the attachments
    dir, the sessions store, crash.txt, and the result artifact this script then reads back. Two
    concurrent runs sharing one base race on all five — and the sessions store is the sharp one,
    because the headless modes DELETE it recursively at startup, so the second run to start wipes
    the directory the first is restoring from and the failure lands in unrelated assertions.

    Runs on DIFFERENT frameworks need nothing: the default base sits under binDir, which is already
    per-TFM. It is two runs on the SAME framework that collide, which is most of the matrix — nine
    screenshot modes and the smoke all share one net10 bin.

    Deliberately NOT extended to the log directory: ExtensionConfig.LogDirectory has no override,
    and it needs none here, because the Desktop host writes nothing there in the automated modes
    (it opens no engine.log tee, and the render log is off by design for exactly that reason).
    A host that DOES tee — the VSIX, or a Console proof run with a log flag — would braid, and
    would need its own answer before it joined a parallel matrix.

.PARAMETER Full
    Print the whole result artifact rather than a truncated summary. Off by default because
    the smoke result is ONE line of several thousand characters, which is unreadable in a
    terminal and gets truncated by whoever is reading it — this script's own caller included.
    A FAILING run always prints in full regardless, since that is the case where the detail is
    the reason you ran it.

.PARAMETER Width
    Window width in px for the shot (default = the Desktop window's own 440). The chat is
    width-sensitive by design (the user bubble is a fraction of the transcript), so a rule
    change wants eyeballing at both ends of the range a VS tool window spans.

.EXAMPLE
    ./scripts/run-smoke.ps1
    ./scripts/run-smoke.ps1 -Build
    ./scripts/run-smoke.ps1 -Mode screenshot
    ./scripts/run-smoke.ps1 -Mode screenshot -Width 1400
    ./scripts/run-smoke.ps1 -Mode perf -Build
    ./scripts/run-smoke.ps1 -Framework net472
    ./scripts/run-smoke.ps1 -Mode perf -Framework net472
#>
[CmdletBinding()]
param(
    [ValidateSet('smoke', 'perf', 'screenshot', 'screenshot-history', 'screenshot-resume', 'screenshot-edit', 'screenshot-flagged', 'screenshot-typing', 'screenshot-held', 'screenshot-attachment', 'screenshot-permission', 'screenshot-subagents', 'screenshot-drilldown', 'screenshot-cli-sessions', 'screenshot-debug-context', 'screenshot-session-info', 'screenshot-mcp-rows')]
    [string]$Mode = 'smoke',
    [switch]$Build,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [ValidateSet('net10.0-windows', 'net472')]
    [string]$Framework = 'net10.0-windows',
    [int]$TimeoutSeconds = 90,
    [int]$Width = 0,
    [switch]$Full,
    [string]$ScratchDir = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if ($Build) {
    Write-Host "Building CodeWicket.slnx ($Configuration)..." -ForegroundColor Cyan
    & dotnet build (Join-Path $repoRoot 'CodeWicket.slnx') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

$binDir = Join-Path $repoRoot "src\CodeWicket.Desktop\bin\$Configuration\$Framework"
$exe = Join-Path $binDir 'CodeWicket.Desktop.exe'
if (-not (Test-Path $exe)) {
    throw "Desktop exe not found at $exe. Run with -Build first."
}

# Set for THIS process so the child inherits it (Start-Process has no portable -Environment).
# That is safe only because each parallel gate is its own pwsh process — run-gates.ps1 launches
# processes rather than runspaces for this exact reason: ForEach-Object -Parallel shares $env:
# across its runspaces, so the isolation would be undone by the thing meant to provide it.
if ($ScratchDir) {
    $env:CWKT_SCRATCH_DIR = $ScratchDir
    # HostScratch.ResolveDir combines the base with the host's own name.
    $scratch = Join-Path $ScratchDir 'desktop'
}
else {
    $scratch = Join-Path $binDir 'scratch\desktop'
}
$artifact = switch ($Mode) {
    'smoke'             { Join-Path $scratch 'smoke-result.txt' }
    'perf'              { Join-Path $scratch 'perf-result.txt' }
    'screenshot'        { Join-Path $scratch 'screenshot.png' }
    'screenshot-history'{ Join-Path $scratch 'screenshot-history.png' }
    'screenshot-resume' { Join-Path $scratch 'screenshot-resume.png' }
    'screenshot-edit'   { Join-Path $scratch 'screenshot-edit.png' }
    'screenshot-flagged'{ Join-Path $scratch 'screenshot-flagged.png' }
    'screenshot-typing' { Join-Path $scratch 'screenshot-typing.png' }
    'screenshot-held' { Join-Path $scratch 'screenshot-held.png' }
    'screenshot-attachment' { Join-Path $scratch 'screenshot-attachment.png' }
    'screenshot-permission' { Join-Path $scratch 'screenshot-permission.png' }
    'screenshot-subagents' { Join-Path $scratch 'screenshot-subagents.png' }
    'screenshot-drilldown' { Join-Path $scratch 'screenshot-drilldown.png' }
    'screenshot-cli-sessions' { Join-Path $scratch 'screenshot-cli-sessions.png' }
    'screenshot-debug-context' { Join-Path $scratch 'screenshot-debug-context.png' }
    'screenshot-session-info' { Join-Path $scratch 'screenshot-session-info.png' }
    'screenshot-mcp-rows' { Join-Path $scratch 'screenshot-mcp-rows.png' }
}

Write-Host "Running Desktop --$Mode ($Framework)$(if ($ScratchDir) { " scratch=$ScratchDir" }) ..." -ForegroundColor Cyan
$argList = @("--$Mode")
if ($Width -gt 0) { $argList += @('--width', "$Width") }
# A full perf sweep runs for many minutes and the old 300 s default cut it off mid-run on BOTH TFMs.
# Measured, default sweep, Debug: net10 4m41s, net472 7m21s. The knobs barely help - the drag and
# large-reply passes use hardcoded sizes, so a smaller --perf-items shrinks only part of the run.
# A kill mid-sweep loses the whole report, which is written only at the end, so this is generous on
# purpose - a ceiling for a run that already exited, not a wait anyone sits through.
#
# Note --perf ALWAYS exits 0 (it catches into a FAILED: report and calls Shutdown(0)), so the exit
# code below says nothing about a perf run. The artifact is the result - read perf-result.txt.
$PerfDefaultTimeout = 1800
if ($Mode -eq 'perf' -and $PSBoundParameters.ContainsKey('TimeoutSeconds') -eq $false) { $TimeoutSeconds = $PerfDefaultTimeout }
# Stamped BEFORE the run so a stale artifact is detectable afterwards. AGENTS.md's rule for --perf
# ("the exit code proves nothing and the artifact is the result") has a corollary this had missed: a
# run that produced no artifact at all leaves the PREVIOUS run's file sitting there, and reading it
# reports someone else's result as this run's.
$runStart = Get-Date
$p = Start-Process -FilePath $exe -ArgumentList $argList -PassThru
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) {
    try { $p.Kill() } catch { }
    throw "Timed out after $TimeoutSeconds s (process did not exit)."
}

Write-Host "exited: $($p.HasExited)  code: $($p.ExitCode)" -ForegroundColor ($(if ($p.ExitCode -eq 0) { 'Green' } else { 'Red' }))

if ($Mode -eq 'smoke' -or $Mode -eq 'perf') {
    if (-not (Test-Path $artifact)) {
        Write-Warning "No result file at $artifact"
        exit $(if ($p.ExitCode -eq 0) { 1 } else { $p.ExitCode })
    }

    $item = Get-Item $artifact
    $text = (Get-Content $artifact -Raw)
    if ($null -eq $text) { $text = '' }

    # A result older than the run is the previous run's. Never read it as this one's.
    $stale = $item.LastWriteTime -lt $runStart
    if ($stale) {
        Write-Warning "$($item.Name) was not written by this run (last modified $($item.LastWriteTime.ToString('HH:mm:ss')), run started $($runStart.ToString('HH:mm:ss'))) - the run produced no result."
    }

    # --perf always exits 0 by design, so the exit code cannot be the only failure signal; the text is.
    $failed = $stale -or $p.ExitCode -ne 0 -or $text -match '(?m)^(FAIL|FAILED)'

    if ($failed) {
        $failLog = Join-Path $scratch 'smoke-failures.log'
        $header = "===== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  mode=$Mode  framework=$Framework  configuration=$Configuration  exit=$($p.ExitCode)$(if ($stale) { '  STALE-ARTIFACT' }) ====="
        Add-Content -Path $failLog -Value $header
        Add-Content -Path $failLog -Value $text
        Add-Content -Path $failLog -Value ''
        Write-Host $text
        Write-Host "Appended to $failLog (this log accumulates; smoke-result.txt does not)." -ForegroundColor Yellow
    }
    elseif ($Full) {
        Write-Host $text
    }
    else {
        # Truncation that ANNOUNCES itself, per the house rule for every other capped output here.
        $oneLine = ($text -replace '\r?\n', ' ').Trim()
        $limit = 200
        if ($oneLine.Length -gt $limit) {
            Write-Host ($oneLine.Substring(0, $limit) + " ... (+$($oneLine.Length - $limit) chars - rerun with -Full, or read $artifact)")
        }
        else {
            Write-Host $oneLine
        }
    }

    exit $(if ($failed -and $p.ExitCode -eq 0) { 1 } else { $p.ExitCode })
}

# A screenshot mode's exit code says NOTHING about whether it worked: every one of them catches its
# own exception, writes <mode>-error.txt, and still calls Shutdown(0). So the old check here — the
# artifact merely EXISTING — passed on the PREVIOUS run's PNG whenever a run threw, which is the
# --perf trap ("the exit code proves nothing and the artifact is the result") in the one mode that
# had not been given the answer. Harmless while a human was looking at the picture afterwards; not
# harmless in a matrix of nine of them where the table is the only thing anyone reads.
#
# Three signals, same shape as the smoke/perf branch above: the artifact must be FRESH, no error
# file may have been written by this run, and a failure is APPENDED so re-running cannot erase it.
$errorFiles = @(
    Get-ChildItem -Path $scratch -Filter '*-error.txt' -File -ErrorAction SilentlyContinue
    Get-ChildItem -Path $scratch -Filter 'crash.txt' -File -ErrorAction SilentlyContinue
) | Where-Object { $_.LastWriteTime -ge $runStart }

$missing = -not (Test-Path $artifact)
$stale = -not $missing -and (Get-Item $artifact).LastWriteTime -lt $runStart
$failed = $missing -or $stale -or $p.ExitCode -ne 0 -or $errorFiles.Count -gt 0

if (-not $failed) {
    Write-Host "Screenshot: $artifact"
    exit 0
}

$reason = if ($missing) { "no screenshot at $artifact" }
          elseif ($stale) { "screenshot was not written by this run (last modified $((Get-Item $artifact).LastWriteTime.ToString('HH:mm:ss')), run started $($runStart.ToString('HH:mm:ss')))" }
          elseif ($errorFiles.Count -gt 0) { "the run threw: $($errorFiles.Name -join ', ')" }
          else { "exit code $($p.ExitCode)" }
Write-Warning "$Mode FAILED - $reason"

$failLog = Join-Path $scratch 'smoke-failures.log'
Add-Content -Path $failLog -Value "===== $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  mode=$Mode  framework=$Framework  configuration=$Configuration  exit=$($p.ExitCode) ====="
Add-Content -Path $failLog -Value $reason
foreach ($f in $errorFiles) {
    Write-Host "--- $($f.Name) ---" -ForegroundColor Red
    # Never let this be null: Get-Content -Raw emits NOTHING for a zero-byte file, and Add-Content
    # -Value $null throws - which would lose the very report this block exists to write.
    $detail = "$(Get-Content $f.FullName -Raw)"
    Write-Host $detail
    Add-Content -Path $failLog -Value "--- $($f.Name) ---"
    Add-Content -Path $failLog -Value $detail
}
Add-Content -Path $failLog -Value ''
Write-Host "Appended to $failLog." -ForegroundColor Yellow

exit $(if ($p.ExitCode -eq 0) { 1 } else { $p.ExitCode })
