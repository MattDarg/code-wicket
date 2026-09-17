<#
.SYNOPSIS
  Measures which tools the agent actually reaches for, from the saved session logs.

.DESCRIPTION
  Counts which tools the agent reached for, from the saved session logs, WITHOUT the logs leaving the
  machine: our IDE tools against the backend's built-ins, repeated searches for the same target (the
  agent hunting for a file), edit loops with no build in between, and semantic tools against walking
  the directory tree.

  PRIVACY: the report contains counts only. No file paths, no source code, no prompt or
  reply text, no workspace or solution names, no session titles. Search targets and edited
  files are identified by an 8-hex hash so repeats can be counted but nothing is
  recoverable from them. Tool titles are echoed only when they match a fixed allowlist of
  generic built-in names; anything else is counted as "other" and never printed.

  Read the output before sending it on. Windows PowerShell 5.1 and pwsh 7 both work.

.EXAMPLE
  .\measure-tool-usage.ps1
  .\measure-tool-usage.ps1 -SinceDays 30
  .\measure-tool-usage.ps1 -OutFile $HOME\Desktop\usage.txt
#>
[CmdletBinding()]
param(
    [string]   $SessionsRoot,
    [string]   $OutFile,
    [int]      $SinceDays = 0,                  # 0 = all history
    [string[]] $ScratchExt = @('.tmp', '.log')  # agent scratch, excluded from the edit-loop stats
)

$ErrorActionPreference = 'Stop'

# ---- locate the store -------------------------------------------------------
if (-not $SessionsRoot) {
    $p = Join-Path $env:APPDATA 'code-wicket\sessions'
    if (Test-Path $p) { $SessionsRoot = $p }
}
if (-not $SessionsRoot -or -not (Test-Path $SessionsRoot)) {
    throw "No sessions folder found. Looked in %APPDATA%\code-wicket\sessions. Pass -SessionsRoot explicitly."
}

$files = @(Get-ChildItem -Path $SessionsRoot -Filter *.json -Recurse -File)
if ($files.Count -eq 0) { throw "No session files under $SessionsRoot" }

# ---- helpers ----------------------------------------------------------------
$sha = [System.Security.Cryptography.SHA256]::Create()
function Get-Tag([string] $s) {
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    $b = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($s.ToLowerInvariant()))
    return (-join ($b[0..3] | ForEach-Object { $_.ToString('x2') }))
}
function Pct($arr, [double] $q) {
    if ($arr.Count -eq 0) { return 0 }
    $s = @($arr | Sort-Object)
    $i = [int][Math]::Floor($q * ($s.Count - 1))
    return $s[$i]
}

# our IDE tools, matched as a token inside whatever namespacing the backend applies
$IdeTools = @(
    'find_symbol', 'find_references', 'find_implementations', 'rename_symbol', 'apply_code_fix',
    'get_diagnostics', 'build_solution', 'run_tests', 'run_command', 'open_file',
    'set_breakpoint', 'read_expression', 'execute_expression'
)
$Semantic = @('find_symbol', 'find_references', 'find_implementations', 'rename_symbol', 'apply_code_fix')
$Buildish = @('build_solution', 'run_tests')

# generic built-in titles that are safe to name in the report
$SafeTitles = @(
    'list directory', 'read file', 'read files', 'glob', 'grep', 'ls', 'search', 'search files',
    'file search', 'find', 'write file', 'replace in file', 'edit', 'read', 'task', 'task list',
    'terminal', 'run command', 'bash', 'todowrite', 'webfetch', 'websearch', 'fetch', 'think'
)
$SearchWords = @('list directory', 'glob', 'ls', 'search', 'find', 'directory')

# a search's input shape decides WHAT it is: looking up a file/dir, or matching text.
# Re-running a grep after an edit is correct behaviour; re-hunting the same PATH is not.
$PathKeys = @('path', 'directorypath', 'dir', 'filepath', 'file', 'folder', 'directory')
$PatternKeys = @('pattern', 'query', 'glob', 'regex', 'search', 'searchterm', 'text')

# ---- accumulators -----------------------------------------------------------
# NB: PowerShell variable names are case-INSENSITIVE, so a per-session local must not
# differ from its accumulator by case alone ($prompts / $Prompts are one variable).
$Sessions = 0; $TotalPrompts = 0; $ZeroPromptSessions = 0; $First = $null; $Last = $null
$ToolCalls = 0; $SearchCalls = 0; $SemanticCalls = 0
$SessionsWithSearch = 0; $SessionsWithSemantic = 0; $SessionsWithBoth = 0
$Edits = 0; $EditsOutsideRoot = 0; $EditsRelative = 0
$TurnsDone = 0; $Cancelled = 0; $Errors = 0

# search shape split
$ShapePath = 0; $ShapePattern = 0; $ShapeUnknown = 0
$PathPairs = 0; $PathRepeat = 0; $PathOver2 = 0; $PathWorst = 0
$PattPairs = 0; $PattRepeat = 0; $PattOver2 = 0; $PattWorst = 0

# edit loop
$SourceEdits = 0; $ScratchEdits = 0
$EditCountsPerFile = New-Object 'System.Collections.Generic.List[int]'
$BurstsPerFile = New-Object 'System.Collections.Generic.List[int]'
$FilesReSearched = 0

$Workspaces = @{}; $Providers = @{}; $Models = @{}; $Modes = @{}
$ByKind = @{}; $ByIdeTool = @{}; $ByTitle = @{}; $OtherTitles = @{}
$EditFiles = @{}; $EditByExt = @{}

function Bump($h, $k) {
    if ($null -eq $k) { return }
    if ($h.ContainsKey($k)) { $h[$k] = $h[$k] + 1 } else { $h[$k] = 1 }
}

$cutoff = $null
if ($SinceDays -gt 0) { $cutoff = (Get-Date).ToUniversalTime().AddDays(-$SinceDays) }

$scan = 0
foreach ($f in $files) {
    $scan++
    Write-Progress -Activity 'Scanning sessions' -Status "$scan / $($files.Count)" -PercentComplete (100 * $scan / $files.Count)

    try { $d = Get-Content -LiteralPath $f.FullName -Raw -Encoding UTF8 | ConvertFrom-Json } catch { continue }
    if (-not $d.log) { continue }

    $updUtc = $null
    if ($d.updatedUtc) { try { $updUtc = ([datetime]$d.updatedUtc).ToUniversalTime() } catch { } }
    if ($cutoff -and $updUtc -and $updUtc -lt $cutoff) { continue }

    $Sessions++
    Bump $Workspaces (Get-Tag $d.workspaceRootPath)
    Bump $Providers  $d.providerId
    Bump $Models     $d.modelId
    Bump $Modes      $d.permissionMode
    if ($updUtc) {
        if ((-not $First) -or $updUtc -lt $First) { $First = $updUtc }
        if ((-not $Last) -or $updUtc -gt $Last) { $Last = $updUtc }
    }

    # An edit's path roots against the AGENT's cwd, not the solution root (issue #54), and
    # the two differ whenever WorkspaceRootLocator walked up. Outside-root means outside both.
    $roots = @()
    if ($d.workspaceRootPath) { $roots += $d.workspaceRootPath.ToLowerInvariant() }
    if ($d.agentWorkingDirectory) { $roots += $d.agentWorkingDirectory.ToLowerInvariant() }

    $sessPrompts = 0; $sessSearch = 0; $sessSemantic = 0
    $tgtPath = @{}          # tag -> times looked up by path, this session
    $tgtPatt = @{}          # tag -> times matched by pattern, this session
    $editCount = @{}        # tag -> edits, this session (source files only)
    $editBursts = @{}       # tag -> edit bursts separated by a build/test
    $editAtBuild = @{}      # tag -> value of $buildTick at its last edit
    $buildTick = 0          # increments on every build/test in this session

    foreach ($e in $d.log) {
        if ($e.role -eq 'user') { $sessPrompts++ }
        $ev = $e.event
        if (-not $ev) { continue }

        if ($ev.type -eq 'turnDone') {
            $TurnsDone++
            if ($ev.stopReason -eq 'cancelled') { $Cancelled++ }
            continue
        }
        if ($ev.type -eq 'error') { $Errors++; continue }

        if ($ev.type -eq 'edit') {
            $Edits++
            if (-not $ev.path) { continue }

            $tag = Get-Tag $ev.path
            Bump $EditFiles $tag
            $ext = [IO.Path]::GetExtension($ev.path)
            if (-not $ext) { $ext = '(none)' }
            $ext = $ext.ToLowerInvariant()
            Bump $EditByExt $ext

            if (-not [IO.Path]::IsPathRooted($ev.path)) { $EditsRelative++ }
            elseif ($roots.Count -gt 0) {
                $lp = $ev.path.ToLowerInvariant()
                $inside = $false
                foreach ($r in $roots) { if ($lp.StartsWith($r)) { $inside = $true; break } }
                if (-not $inside) { $EditsOutsideRoot++ }
            }

            # scratch (commit messages, query files) is not source churn - keep it out of the loop stats
            if ($ScratchExt -contains $ext) { $ScratchEdits++; continue }
            $SourceEdits++

            Bump $editCount $tag
            if (-not $editBursts.ContainsKey($tag)) {
                $editBursts[$tag] = 1
            }
            elseif ($buildTick -gt $editAtBuild[$tag]) {
                # a build/test ran since we last touched this file, and here we are again
                $editBursts[$tag] = $editBursts[$tag] + 1
            }
            $editAtBuild[$tag] = $buildTick
            continue
        }

        if ($ev.type -ne 'toolStart') { continue }

        $ToolCalls++
        $kind = $ev.kind
        if (-not $kind) { $kind = '(none)' }
        Bump $ByKind $kind

        $title = ''
        if ($ev.title) { $title = [string]$ev.title }
        $lower = $title.ToLowerInvariant()

        # decode rawInput once - used for the build tick and the search shape
        $ri = $null
        if ($ev.rawInputJson) { try { $ri = $ev.rawInputJson | ConvertFrom-Json } catch { } }
        # rawInput is usually an object, but can be an array or a scalar - only an object has keys
        $riKeys = @()
        if ($ri -is [psobject] -and $ri -isnot [array]) {
            $riKeys = @($ri.PSObject.Properties.Name | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })
        }
        else { $ri = $null }

        # our IDE tools, however the backend namespaced them
        $matched = $false
        foreach ($t in $IdeTools) {
            if ($lower -match "(^|[^a-z_])$t([^a-z_]|`$)") {
                Bump $ByIdeTool $t
                $matched = $true
                if ($Semantic -contains $t) { $SemanticCalls++; $sessSemantic++ }
                if ($Buildish -contains $t) { $buildTick++ }
                break
            }
        }

        # a build/test run through the agent's own shell counts too - classified, never printed
        if (-not $matched -and $riKeys -contains 'command') {
            $cmd = [string]$ri.command
            if ($cmd -and $cmd.ToLowerInvariant() -match '(^|[^a-z])(build|msbuild|test|dotnet)([^a-z]|$)') { $buildTick++ }
        }

        if (-not $matched) {
            if ($SafeTitles -contains $lower) { Bump $ByTitle $lower }
            else { Bump $OtherTitles (Get-Tag $title) }
        }

        # file/dir search shapes -> gate (a)
        $isSearch = ($kind -eq 'search')
        if ((-not $isSearch) -and (-not $matched)) {
            foreach ($s in $SearchWords) {
                if ($lower -eq $s -or $lower.StartsWith("$s ")) { $isSearch = $true; break } }
        }
        if (-not $isSearch) { continue }

        $SearchCalls++; $sessSearch++

        # WHICH shape: a path lookup, or a text/glob match? pattern wins when both appear,
        # because a path there is the scope of the match, not the thing being looked for.
        $target = $null; $shape = 'unknown'
        foreach ($k in $PatternKeys) {
            if ($riKeys -contains $k -and $ri.$k) { $target = [string]$ri.$k; $shape = 'pattern'; break }
        }
        if (-not $target) {
            foreach ($k in $PathKeys) {
                if ($riKeys -contains $k -and $ri.$k) { $target = [string]$ri.$k; $shape = 'path'; break }
            }
        }

        switch ($shape) {
            'path' { $ShapePath++;    Bump $tgtPath (Get-Tag $target) }
            'pattern' { $ShapePattern++; Bump $tgtPatt (Get-Tag $target) }
            default { $ShapeUnknown++ }
        }

        # did we just go looking for a file we already edited in this session?
        if ($shape -eq 'path') {
            $t2 = Get-Tag $target
            if ($editCount.ContainsKey($t2)) { $FilesReSearched++ }
        }
    }

    $TotalPrompts += $sessPrompts
    if ($sessPrompts -eq 0) { $ZeroPromptSessions++ }
    if ($sessSearch -gt 0) { $SessionsWithSearch++ }
    if ($sessSemantic -gt 0) { $SessionsWithSemantic++ }
    if ($sessSearch -gt 0 -and $sessSemantic -gt 0) { $SessionsWithBoth++ }

    foreach ($kv in $tgtPath.GetEnumerator()) {
        $PathPairs++
        if ($kv.Value -gt 1) { $PathRepeat++ }
        if ($kv.Value -gt 2) { $PathOver2++ }
        if ($kv.Value -gt $PathWorst) { $PathWorst = $kv.Value }
    }
    foreach ($kv in $tgtPatt.GetEnumerator()) {
        $PattPairs++
        if ($kv.Value -gt 1) { $PattRepeat++ }
        if ($kv.Value -gt 2) { $PattOver2++ }
        if ($kv.Value -gt $PattWorst) { $PattWorst = $kv.Value }
    }
    foreach ($v in $editCount.Values) { $EditCountsPerFile.Add([int]$v) }
    foreach ($v in $editBursts.Values) { $BurstsPerFile.Add([int]$v) }
}
Write-Progress -Activity 'Scanning sessions' -Completed

# ---- report -----------------------------------------------------------------
$o = New-Object 'System.Collections.Generic.List[string]'
function W($s) { $o.Add([string]$s) }
function Table($h, $top) {
    if ($h.Count -eq 0) { W '      (none)'; return }
    $h.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First $top | ForEach-Object {
        W ('  {0,6}  {1}' -f $_.Value, $_.Key)
    }
}

$totalPairs = $PathPairs + $PattPairs
$totalOver2 = $PathOver2 + $PattOver2

W '=== code-wicket tool-usage measurement (v2) ==='
W ('generated {0}  |  counts only: no paths, code, titles or prompt text' -f (Get-Date -Format 'yyyy-MM-dd HH:mm'))

# An instrument that reports plausible zeros because the log schema moved under it is worse
# than no instrument. Read sessions but no tool calls can only mean the shape changed.
if ($Sessions -gt 0 -and $ToolCalls -eq 0) {
    W ''
    W ('  *** STOP: read {0} sessions and found ZERO tool calls. ***' -f $Sessions)
    W '  *** That is a schema change, not a finding. This script reads log[].event.type'
    W '  *** == "toolStart" / "edit" / "turnDone"; check those names before believing any'
    W '  *** number below. Do NOT report these counts.'
}
W ''
W '-- corpus --'
W ('  sessions              {0}   (files scanned {1})' -f $Sessions, $files.Count)
W ('  distinct workspaces   {0}' -f $Workspaces.Count)
if ($First) { W ('  date range            {0} .. {1}' -f $First.ToString('yyyy-MM-dd'), $Last.ToString('yyyy-MM-dd')) }
W ('  user prompts          {0}' -f $TotalPrompts)
W ('  sessions w/ 0 prompts {0}   <- representativeness check' -f $ZeroPromptSessions)
W ('  turns                 {0}   (cancelled {1}, errors {2})' -f $TurnsDone, $Cancelled, $Errors)
W '  providers:'; Table $Providers 6
W '  models:'; Table $Models 8
W '  permission modes:'; Table $Modes 6
W ''
W '-- tool calls --'
W ('  total                 {0}' -f $ToolCalls)
W '  by kind:'; Table $ByKind 12
W ''
W '-- our IDE tools (invocations) --'
foreach ($t in $IdeTools) {
    $c = 0
    if ($ByIdeTool.ContainsKey($t)) { $c = $ByIdeTool[$t] }
    $mark = ''
    if ($Semantic -contains $t) { $mark = '   *semantic' }
    W ('  {0,6}  {1}{2}' -f $c, $t, $mark)
}
W ''
W '-- built-in tools (allowlisted generic titles only) --'
Table $ByTitle 15
$otherSum = 0
if ($OtherTitles.Count -gt 0) { $otherSum = ($OtherTitles.Values | Measure-Object -Sum).Sum }
W ('  {0,6}  other, unnamed ({1} distinct titles, deliberately not printed)' -f $otherSum, $OtherTitles.Count)
W ''
W '-- GATE (a): repeated search for the same target, within one session --'
W ('  file/dir search calls        {0}' -f $SearchCalls)
W ('  distinct (session,target)    {0}' -f $totalPairs)
W ('  searched >2x same session    {0}   <- gate criterion (a), BOTH shapes' -f $totalOver2)
W ''
W '  split by input shape (a repeated PATTERN is re-grepping and expected;'
W '  a repeated PATH is the agent hunting for a file, which is the find_file signal):'
W ('  {0,6}  path-like calls      ({1} distinct, {2} repeated, {3} over 2x, worst {4}x)' -f $ShapePath, $PathPairs, $PathRepeat, $PathOver2, $PathWorst)
W ('  {0,6}  pattern-like calls   ({1} distinct, {2} repeated, {3} over 2x, worst {4}x)' -f $ShapePattern, $PattPairs, $PattRepeat, $PattOver2, $PattWorst)
W ('  {0,6}  no recognisable target (not counted above)' -f $ShapeUnknown)
W ''
W ('  looked up a path already edited this session   {0}   <- exact-path matches only' -f $FilesReSearched)
W ''
W '-- GATE (b): edits --'
W ('  edit events                  {0}   (source {1}, scratch {2}: {3})' -f $Edits, $SourceEdits, ($ScratchExt -join '/'), $ScratchEdits)
W ('  distinct files edited        {0}' -f $EditFiles.Count)
W ('  edits outside both roots     {0}   (workspace root and agent cwd)' -f $EditsOutsideRoot)
W ('  edits via a relative path    {0}' -f $EditsRelative)
W '  by extension (all edits):'; Table $EditByExt 12
W ''
W '  edit LOOP, source files only - a burst is a run of edits to one file with no'
W '  build/test in between, so >=3 bursts means edit -> build -> edit again -> build ...'
W '  which is what "the agent edits it, build reports no change, it theorises" looks like.'
W '  Build detection is deliberately broad (our tools + any shell command mentioning'
W '  build/msbuild/test/dotnet), so a burst count errs HIGH - treat it as an upper bound:'
if ($EditCountsPerFile.Count -gt 0) {
    W ('  {0,6}  (session,file) pairs' -f $EditCountsPerFile.Count)
    W ('          edits per file    p50={0}  p90={1}  max={2}' -f (Pct $EditCountsPerFile 0.5), (Pct $EditCountsPerFile 0.9), (Pct $EditCountsPerFile 1.0))
    W ('          bursts per file   p50={0}  p90={1}  max={2}' -f (Pct $BurstsPerFile 0.5), (Pct $BurstsPerFile 0.9), (Pct $BurstsPerFile 1.0))
    $b3 = @($BurstsPerFile | Where-Object { $_ -ge 3 }).Count
    $b5 = @($BurstsPerFile | Where-Object { $_ -ge 5 }).Count
    W ('  {0,6}  files with >=3 bursts   <- gate criterion (b)' -f $b3)
    W ('  {0,6}  files with >=5 bursts' -f $b5)
}
else { W '      (no source edits)' }
W ''
W '-- semantic vs walking --'
W ('  semantic tool calls          {0}' -f $SemanticCalls)
W ('  file/dir search calls        {0}' -f $SearchCalls)
W ('  sessions using semantic      {0} of {1}' -f $SessionsWithSemantic, $Sessions)
W ('  sessions using file search   {0} of {1}' -f $SessionsWithSearch, $Sessions)
W ('  sessions using both          {0}' -f $SessionsWithBoth)

$text = $o -join [Environment]::NewLine
Write-Output $text
if ($OutFile) {
    $text | Set-Content -LiteralPath $OutFile -Encoding UTF8
    Write-Host ''
    Write-Host "written to $OutFile" -ForegroundColor Green
}
