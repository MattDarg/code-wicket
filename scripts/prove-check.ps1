<#
.SYNOPSIS
    Proves a check is load-bearing: injects the bug it guards, runs it, and restores the source.

.DESCRIPTION
    A test that passes with the fix removed pins nothing, so every new check earns its place by
    being watched to fail. The awkward half is getting the tree back afterwards, and the obvious
    way — `git checkout -- <file>` — is a trap: it reverts the file to HEAD, discarding every
    UNCOMMITTED change in that file along with the injected line, silently and unrecoverably.
    This script exists so that avoiding the trap does not depend on remembering it.

    Restoration is from a byte copy taken before the injection, in a `finally`. It cannot touch a
    file it was not given, it does not care what git thinks, and it works on a dirty tree — so
    "commit before you inject" stops being a precondition.

    Two things it checks that a hand-run sequence does not:

      * THE INJECTION ACTUALLY LANDED. A `sed` whose pattern matches nothing exits 0 having done
        nothing at all, and the verification then passes for the most misleading possible reason:
        the code under test was never changed. Every target is hashed before and after, and an
        injection that changed no file is a hard error rather than a green run.

      * THE RESULT IS INVERTED, because that is what the exercise means. The check FAILING is the
        success condition; a check that stays green over an injected bug is the finding, and it is
        reported as PINS-NOTHING rather than left for the reader to notice.

      * THE CHECK ACTUALLY RAN. There are THREE verdicts, not two, and the third is why: an
        injection that does not COMPILE makes `dotnet test` exit non-zero without executing a
        single test, and read off the exit code alone that is indistinguishable from the check
        failing — i.e. a confident LOAD-BEARING verdict over a build error. THE EXIT CODE IS NOT
        THE RESULT is a rule `run-smoke.ps1` and `run-gates.ps1` each carry for their own reasons,
        and it binds hardest here, this being the instrument every other "load-bearing" claim is
        measured with. A tool that can report green about a check that never ran is worse than no
        tool: the manual sequence at least forces you to read the numbers.

        So the verdict is decided on what the run REPORTED — a test summary line with a non-zero
        total, or a smoke run's own PASS/FAIL — and the exit code only separates the two outcomes
        once something is known to have run. INCONCLUSIVE covers a broken injected build, a filter
        matching nothing (zero tests, and exit 0 in some configurations, which would otherwise read
        as PINS-NOTHING when nothing was measured at all), and a `-Verify` whose output this cannot
        recognise.

      * THE OUTPUT TREE IS PUT BACK, not just the source. Restoring the source leaves the bin/
        folders holding a build made from the INJECTED version. The timestamp stamp below means the
        next BUILD corrects that, which covers most callers - but nothing covers a `--no-build` run
        in between, and that run measures the bug while reporting on the fix. Exactly the class of
        confidently-wrong result this script exists to prevent, so it rebuilds after restoring.
        `-NoRebuild` opts out when the caller is about to build anyway (chained prove-checks, or a
        gate run that builds first).

.NOTES
    Exit codes: 0 = LOAD-BEARING, 1 = PINS NOTHING, 2 = INCONCLUSIVE. A hard error (a missed anchor, a
    failed injection command) throws, which under `pwsh -File` also exits 1: read the printed line.

    These are VERDICTS, not counts, and that is worth knowing because the neighbouring scripts are
    not the same: `run-gates.ps1` exits with the NUMBER of failed gates, so its 2 means two gates
    failed rather than anything about evidence, and `run-smoke.ps1` maps an exit-0-with-a-failed-
    artifact to 1. Nothing aggregates any of them today; anything that ever does must read each
    one's own meaning rather than assuming a shared scale.

.PARAMETER Path
    The source files the injection touches. These, and only these, are snapshotted and restored.

.PARAMETER Inject
    A command that introduces the bug (sed, a python one-liner, whatever). Run from the repo root.

.PARAMETER Verify
    The command that runs the check — typically `dotnet test ... --filter ...` or
    `pwsh -NoProfile -File scripts/run-smoke.ps1 ...`. Its EXIT CODE is read, which is sound for
    both: dotnet test exits non-zero on a failing test, and run-smoke.ps1 already does its own
    artifact-and-freshness check before setting one. Do not point this at a bare Desktop run,
    whose exit code is documented as proving nothing.

    A SMOKE VERIFY MUST CARRY `-Build`. run-smoke.ps1 builds only when asked, so without it the
    run measures the binary already in bin/ - which is the FIXED one, the injection having been
    written to source a second earlier and never compiled. The injection-landed hash check above
    cannot see this: the file genuinely changed, so the run is reported as having tested it, and a
    check that pins its bug perfectly well comes back PINS NOTHING. Measured, on a check
    subsequently proved LOAD-BEARING by the same injection with `-Build` added. A `dotnet test`
    verify is unaffected - it builds on its own.

.PARAMETER Name
    What the injected bug is, for the report.

.EXAMPLE
    pwsh -NoProfile -File scripts/prove-check.ps1 `
        -Path src/CodeWicket.Shell/PolicyPermissionHandler.cs `
        -Name 'the policy never looks at where a write lands' `
        -Inject 'python scripts/inject/protected-path-guard-removed.py' `
        -Verify 'dotnet test src/CodeWicket.Tests/CodeWicket.Tests.csproj --nologo -v q --filter FullyQualifiedName~PolicyPermissionHandlerTests'

.EXAMPLE
    pwsh -NoProfile -File scripts/prove-check.ps1 `
        -Path src/CodeWicket.Desktop/App.xaml.cs `
        -Name 'Hyperlinks() cannot enter a bullet list' `
        -Inject 'python scripts/inject/hyperlinks-dont-enter-a-list.py' `
        -Verify 'pwsh -NoProfile -File scripts/run-smoke.ps1 -Mode smoke -Framework net10.0-windows -Build'
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [Parameter(Mandatory)][string]$Inject,
    [Parameter(Mandatory)][string]$Verify,
    [string]$Name = 'the injected bug',

    # Skip the post-restore rebuild. Two callers want this, and the second is the important one.
    #
    #   * One that builds before it next reads the output - a chained prove-check, or a gate run.
    #     Purely a time saving.
    #   * The DELIBERATE inject -> build -> restore -> run-the-already-built-binary pattern verification.md
    #     documents for a check needing a slow or live run, whose whole point is that the injected
    #     BINARY survives the restore so the tree is clean while the run is in flight. Rebuilding
    #     would quietly hand that run the fixed build and turn a proof into a formality.
    #
    # Otherwise leave it alone: a `--no-build` run after this switch measures the injected bug and
    # reports it under the fix's name.
    [switch]$NoRebuild
)

# Same guard as run-gates.ps1, and worth having for a different reason: under 5.1 a `finally` still
# runs, but the exit-code reading below is what decides PASS from PINS-NOTHING, and 5.1's handling
# of native exit codes through this shape is exactly what made every gate lie. A restore that works
# while the verdict is wrong is the worse failure, because it looks like it worked.
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "prove-check.ps1 requires PowerShell 7 (pwsh); this is $($PSVersionTable.PSVersion). " +
          "Re-run with: pwsh -NoProfile -File scripts/prove-check.ps1 ..."
}

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

# The path out of one `git status --porcelain` line. Two shapes to get right, because getting either
# wrong makes the stray check below name a file that is not the one that changed: the two status
# columns and a space come first, and a RENAME is written `R  old -> new`, where the file on disk now
# is the one after the arrow. Paths are unquoted by the callers' `-c core.quotepath=false`; a path that
# literally contains " -> " is the residual case, and it reads as its own tail.
function StatusPath([string] $line) {
    $path = $line.Substring(3)
    $arrow = $path.IndexOf(' -> ')
    if ($arrow -ge 0) { $path = $path.Substring($arrow + 4) }
    return $path
}
Push-Location $repoRoot

# THE BREADCRUMB. Everything below restores in a `finally`, which a process that is KILLED never
# reaches - so an aborted run leaves its target INJECTED, in a tree that still builds and mostly
# passes. Nothing reports that, and the next build, test run or sweep measures someone else's bug
# with complete confidence. Hashing cannot close it: after an abort the "pristine" hash you would
# compare against is already the injected content, and the instrument agrees with itself.
#
# A marker does close it, because its PRESENCE carries the fact rather than its content, and it
# survives for exactly the same reason the restore did not happen. Written before the injection,
# deleted in the same `finally` that restores.
#
# The refusal below says how to clear it, deliberately: a guard that blocks every later run without
# stating a way forward is a worse failure than the hazard it prevents, and whoever hits it is by
# definition in the state where they have least idea what is going on.
$marker = Join-Path $PSScriptRoot '.prove-check-in-flight'
if (Test-Path -LiteralPath $marker) {
    $stale = (Get-Content -LiteralPath $marker -Raw).Trim()
    throw "a previous prove-check did not finish, so its target may still be INJECTED. It was:`n" +
          "$stale`n" +
          "Check `git diff` on those files and restore them, then delete '$marker' and re-run. " +
          "Do not build, test or sweep before you have: every result until then measures the injection."
}

try {
    $targets = @()
    foreach ($p in $Path) {
        $full = (Resolve-Path -LiteralPath $p).Path
        $targets += [pscustomobject]@{
            Path   = $full
            Backup = [System.IO.Path]::GetTempFileName()
            Before = (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash
        }
    }

    # What the tree already looks like, so a file this run DIRTIES can be told from one that was
    # dirty when it started. See the stray-file check after the injection for what it is for.
    #
    # `-c core.quotepath=false` because a non-ASCII path comes back QUOTED by default and would then
    # match nothing, which in the check below would report a snapshotted target as a stray - a refusal
    # naming the opposite of the truth, which is the failure that check's ordering exists to avoid.
    $dirtyBefore = @{}
    try { git -c core.quotepath=false status --porcelain 2>$null | ForEach-Object { $dirtyBefore[(StatusPath $_)] = $true } } catch { }

    # THE PROBE IS A NATIVE COMMAND AND LEAVES $LASTEXITCODE SET. An injection written in pure
    # PowerShell sets none of its own, so without this reset the check after `Invoke-Expression $Inject`
    # reads GIT's exit code and aborts with "the injection command exited 128" - a false failure blaming
    # the injection, and the exact opposite of the advisory-where-git-cannot-answer property this probe
    # is designed for. Latent while every script here is invoked as `python`, which sets its own.
    $global:LASTEXITCODE = 0

    foreach ($t in $targets) { Copy-Item -LiteralPath $t.Path -Destination $t.Backup -Force }
    Set-Content -LiteralPath $marker -Value (
        "$Name`n  injected by: $Inject`n" +
        (($targets | ForEach-Object { "  target: $($_.Path)`n    backup: $($_.Backup)" }) -join "`n"))
    Write-Host "Snapshotted $($targets.Count) file(s). Injecting: $Name" -ForegroundColor Cyan

    $verifyExit = $null
    $changed = @()
    try {
        # --- inject ---------------------------------------------------------------------------
        Invoke-Expression $Inject
        $injectExit = $LASTEXITCODE
        if ($null -ne $injectExit -and $injectExit -ne 0) {
            throw "the injection command exited $injectExit; nothing was verified."
        }

        # A pattern that matched nothing exits 0. Without this, the verification below runs against
        # UNMODIFIED source and its result means nothing at all.
        foreach ($t in $targets) {
            if ((Get-FileHash -LiteralPath $t.Path -Algorithm SHA256).Hash -ne $t.Before) {
                $changed += $t.Path
            }
        }
        # A FILE THE INJECTION WROTE THAT -Path DID NOT NAME IS NOT RESTORED, and nothing else reports
        # it: the `finally` below puts back exactly what it snapshotted, the marker is deleted because
        # this process is exiting normally, and the tree is left with an injection in a file nobody is
        # watching. Measured - a script patching two files, run with one of them as -Path: the hash check
        # above fired with "the injection changed no file" (true of the snapshotted one), and the OTHER
        # file stayed injected through the report, the restore and the clean exit.
        #
        # Diffed against the state captured before the injection, so a tree that was already dirty is
        # fine: what is looked for is a file this run made dirty and is not going to put back. Advisory
        # where git cannot answer, because the rest of this script deliberately does not depend on it.
        #
        # THAT TOLERANCE IS ALSO THE BLIND SPOT, and it is worth knowing which way it fails: a stray
        # write into a file that was ALREADY dirty is invisible here and is not restored either. Mid-
        # refactor that is the normal state of the tree, which is the other reason to commit before a
        # sweep rather than only to keep an anchor loop from eating the work.
        $snapshotted = @{}
        foreach ($t in $targets) { $snapshotted[(Resolve-Path -LiteralPath $t.Path).Path] = $true }
        $stray = @()
        try {
            git -c core.quotepath=false status --porcelain 2>$null | ForEach-Object {
                $rel = StatusPath $_
                if (-not $dirtyBefore.ContainsKey($rel)) {
                    $full = Join-Path $repoRoot $rel
                    if (-not $snapshotted.ContainsKey($full)) { $stray += $rel }
                }
            }
        } catch { }
        $global:LASTEXITCODE = 0
        if ($stray.Count -gt 0) {
            throw "the injection also wrote $($stray -join ', '), which -Path does not name, so this " +
                  "script cannot restore it. Nothing was verified. Re-run with every file the injection " +
                  "touches in -Path, and restore those listed here first."
        }

        if ($changed.Count -eq 0) {
            throw "the injection changed no file — its anchor missed. Nothing was verified."
        }


        Write-Host "Injected into $($changed.Count) file(s). Running the check ..." -ForegroundColor Cyan

        # --- verify ---------------------------------------------------------------------------
        # Tee'd rather than merely run: the verdict below is decided on what the run REPORTED, and
        # the reader still needs to see it. 2>&1 so a build error reaches the classifier, since a
        # build error is precisely the case this exists to catch.
        #
        # 6>&1 is what stops the verdict depending on how the -Verify happens to be SPELLED.
        # run-smoke.ps1 prints its own PASS/FAIL with Write-Host, i.e. to the INFORMATION stream.
        # Spelled as a child process ("pwsh -File scripts/run-smoke.ps1 ...") that line crosses the
        # process boundary as ordinary stdout and is captured; spelled in-process
        # ("./scripts/run-smoke.ps1 ...") it goes to this runspace's information stream, which 2>&1
        # does not take - so the same injection classified LOAD-BEARING one day and INCONCLUSIVE the
        # next, on the strength of nothing but the caller's phrasing. Measured: `exit` is NOT the
        # discriminator, and neither is script-vs-command; only the process boundary is.
        #
        # It lands in the worst place. A `dotnet test` verify was never affected, its summary being
        # stdout either way; the spelling-sensitive one is the smoke verify, the only instrument that
        # spans processes and so the only one that can catch the "three places, not two" gap. And it
        # degrades to INCONCLUSIVE, which reads as a clumsy injection - so the reflex is to rewrite
        # the injection, and nothing is ever wrong enough to chase.
        Invoke-Expression "$Verify 2>&1" 6>&1 | Tee-Object -Variable verifyOutput
        $verifyExit = $LASTEXITCODE
    }
    finally {
        # The whole point. Restores exactly what was snapshotted, whatever happened above, and
        # touches nothing else in the tree.
        foreach ($t in $targets) {
            Copy-Item -LiteralPath $t.Backup -Destination $t.Path -Force

            # STAMP IT NOW, or the restore is worse than no restore at all. Copy-Item carries the
            # BACKUP's timestamp back with the content, so the recovered source ends up OLDER than the
            # build output produced from the injected version a moment ago — and MSBuild, comparing
            # exactly those two times, calls the project up to date and keeps the INJECTED BINARY. Every
            # run after that silently tests the bug instead of the fix. Found the only way it could be:
            # a test that had just been watched to fail went on failing after the source was correct.
            (Get-Item -LiteralPath $t.Path).LastWriteTimeUtc = [DateTime]::UtcNow

            Remove-Item -LiteralPath $t.Backup -Force -ErrorAction SilentlyContinue
        }
        Remove-Item -LiteralPath $marker -Force -ErrorAction SilentlyContinue
        Write-Host "Restored $($targets.Count) file(s) from snapshot." -ForegroundColor DarkGray

        # The source is back; the OUTPUT is not. Everything under bin/ was produced from the injected
        # version a moment ago, and the stamp above only guarantees that the next BUILD notices. A
        # `--no-build` run in the gap silently measures the bug and reports it as the fix - which has
        # happened, twice in one sitting, to someone who had just read the comment above explaining
        # the neighbouring half of it. Skipped when the injection never landed (nothing was built from
        # it) and when the caller says it is about to build anyway.
        if ($changed.Count -gt 0 -and -not $NoRebuild) {
            Write-Host "Rebuilding so the output tree matches the restored source ..." -ForegroundColor DarkGray
            $buildOutput = & dotnet build 'CodeWicket.slnx' -v q --nologo 2>&1
            if ($LASTEXITCODE -ne 0) {
                # Reported, never thrown: the verdict below is about the CHECK and is already decided,
                # and a build failure here is a fact about the tree rather than about the check. Loud,
                # because the state it leaves is the one this block exists to prevent.
                Write-Host "REBUILD FAILED - bin/ still holds the INJECTED build." -ForegroundColor Red
                $buildOutput | Select-Object -Last 15 | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
                Write-Host "  Fix the build before trusting any --no-build run." -ForegroundColor Red
            }
        }
    }

    # --- did the check actually run? -----------------------------------------------------------
    # Read the way run-smoke.ps1 reads its artifact: from what the run SAID, never from how it exited.
    $lines = @($verifyOutput | ForEach-Object { "$_" })
    $inconclusive = $null

    # A test summary line ("Passed!  - Failed: 0, Passed: 42, ... Total: 42") or a smoke run's own
    # verdict. Either means something executed and reported on itself.
    $summary = $lines | Where-Object { $_ -match '^\s*(Passed|Failed)!\s+-\s+Failed:' } | Select-Object -Last 1
    $smokeVerdict = $lines | Where-Object { $_ -match '^\s*(PASS|FAIL):' } | Select-Object -Last 1

    # Most specific first: a broken injection is the case this whole block exists for, and it can
    # coexist with a stale summary line from an earlier project in the same run.
    #
    # Matched in MSBuild's own shape - "<origin>: error CS1234:" - and not on the bare code. A test's
    # FAILURE output can quote a build error as data (OutputCaptureTests asserts over a pane holding
    # "error CS0103"), and the bare pattern read that assertion message as the injected build breaking,
    # so a check that had just failed exactly as intended came back INCONCLUSIVE (measured 2026-09-13,
    # output-capture-drops-the-provenance). An expectation string is printed escaped, never with the
    # colons on either side.
    $buildError = $lines | Where-Object { $_ -match ':\s*error\s+(CS|MSB)\d+\s*:' } | Select-Object -First 1
    if ($buildError) {
        $inconclusive = "the injected source did not BUILD, so no check ran: $buildError"
    }
    elseif (-not $summary -and -not $smokeVerdict) {
        $inconclusive = 'the verify command reported no test summary and no PASS/FAIL line, ' +
                        'so there is no evidence any check executed.'
    }
    elseif ($summary -and $summary -match 'Total:\s*(\d+)' -and [int]$Matches[1] -eq 0) {
        $inconclusive = 'the run executed ZERO tests - usually a --filter that matches nothing. ' +
                        'Nothing was measured, either way.'
    }

    Write-Host ''
    if ($inconclusive) {
        Write-Host "INCONCLUSIVE: $inconclusive" -ForegroundColor Yellow
        Write-Host "  '$Name' was injected and the tree DID change, but the check never reported." -ForegroundColor Yellow
        Write-Host "  This is NOT a pass and NOT a failure. Fix the injection or the filter and re-run." -ForegroundColor Yellow
        exit 2
    }

    if ($verifyExit -eq 0) {
        Write-Host "PINS NOTHING: the check passed with '$Name' in place." -ForegroundColor Red
        Write-Host "  The check does not guard what it claims to. Usually one of:" -ForegroundColor Red
        Write-Host "   * an EARLIER gate already refuses the input, so it never reaches the one under test;" -ForegroundColor Red
        Write-Host "   * the case injects its own dependency, so the real code path is never taken;" -ForegroundColor Red
        Write-Host "   * the assertion is true of both the fixed and the broken behaviour." -ForegroundColor Red
        exit 1
    }

    Write-Host "LOAD-BEARING: the check failed with '$Name' in place (exit $verifyExit)." -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}
