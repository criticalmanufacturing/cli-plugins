<#
.SYNOPSIS
    Finds CANDIDATE nested-Action-Group edges in the CM Framework MES platform source, to seed/refresh
    src/Data/nested-action-groups.json without re-running a full token-costly agent sweep every time.

.DESCRIPTION
    This is a cheap, regex-based heuristic scan over the *Orchestration*.cs files under an Orchestration
    root (default: MES/Host/Biz/Orchestration in a checkout of the CM Framework Product repo). It flags
    three shapes, mirroring exactly what the manual/agent research this catalog was built from looked for:

      - same-class: an outer public method calling another public method of the SAME class
        (e.g. ComplexTrackOutMaterials calling TrackOutMaterials).
      - cross-class: an outer method calling a method on an injected field whose type name ends in
        "Orchestration" (e.g. this._stateModelOrchestration.FullUpdateStateModel(...)) -- a DIFFERENT
        orchestration class's method.
      - bo-wrap: a call shaped like <output-ish variable>.<Entity>.<Verb>(...), which is how every
        confirmed terminal BusinessObject-level Action Group wrap in the catalog looks (e.g.
        output.Materials.TrackOut(...)). Common LINQ methods are excluded via a blacklist so they don't
        drown out real candidates.

    IMPORTANT: this script produces CANDIDATES, not confirmed edges. It has no type information (it's
    text/regex-based, not a real C# parser), so it WILL have false positives (e.g. a same-named but
    unrelated method, or a bo-wrap-shaped call that isn't actually an Action Group) and false negatives
    (an edge phrased in a way the regexes don't happen to match). Every candidate still needs a human or
    a targeted agent to open the file, read the actual method body, and confirm it before it's added to
    src/Data/nested-action-groups.json -- exactly the verification step already applied throughout that
    file's "comment" field. What this script saves is the expensive part: reading every orchestration
    method in every module by hand/by agent just to find where to LOOK.

    Does NOT attempt BusinessObject-to-BusinessObject nesting (whether a BO verb method itself calls
    another BO's verb method) -- that needs real type resolution to locate the BO's implementing class,
    which this lightweight, dependency-free script deliberately doesn't attempt. Use a targeted agent for
    that, the same way this catalog's BO-layer entries were confirmed.

.PARAMETER RepoRoot
    Root of a checkout of the CM Framework Product repo (the one containing MES/Host/Biz/Orchestration).

.PARAMETER OrchestrationSubPath
    Relative path from RepoRoot to the Orchestration folder. Default: MES/Host/Biz/Orchestration.

.PARAMETER OutputPath
    Where to write the JSON candidates report. Default: ./candidates.json (in the
    current directory).

.EXAMPLE
    ./discover-nested-action-groups.ps1 -RepoRoot <repository-root>

    Scans the default Orchestration path and writes candidates to ./candidates.json.

.EXAMPLE
    ./discover-nested-action-groups.ps1 -RepoRoot <repository-root> -OutputPath ./candidates.json | Format-Table -AutoSize

    Same, but also prints a summary table to the console (the script writes a per-module count summary
    regardless; piping the returned candidate objects lets you inspect them directly).
#>

param(
    [Parameter(Mandatory = $true)]
    [string]$RepoRoot,

    [string]$OrchestrationSubPath = "MES/Host/Biz/Orchestration",

    [string]$OutputPath = "./candidates.json"
)

$ErrorActionPreference = "Stop"

# LINQ/collection methods that are never an Action Group wrap -- excluded from the bo-wrap heuristic so
# they don't drown out real candidates (confirmed via the same exclusion used by every manual/agent pass
# this catalog was built from).
$LinqBlacklist = @(
    "Where", "Select", "SelectMany", "Any", "All", "Count", "First", "FirstOrDefault", "Last",
    "LastOrDefault", "Single", "SingleOrDefault", "ElementAt", "ElementAtOrDefault", "ToDictionary",
    "ToList", "ToArray", "ToHashSet", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
    "GroupBy", "Sum", "Max", "Min", "Average", "Contains", "Distinct", "Skip", "Take", "Cast", "OfType",
    "Aggregate", "Concat", "Union", "Intersect", "Except", "Reverse", "Zip", "Chunk"
)

$orchestrationRoot = Join-Path $RepoRoot $OrchestrationSubPath
if (-not (Test-Path $orchestrationRoot)) {
    throw "Orchestration root not found: $orchestrationRoot"
}

$publicMethodDeclRegex = [regex]'^\s*public\s+(?:virtual\s+|override\s+|async\s+|sealed\s+)*[\w<>\[\],\.\?\s]+?\s+(\w+)\s*\('
$orchestrationFieldRegex = [regex]'(?:private|protected)(?:\s+readonly)?\s+(I?\w*[Oo]rchestration\w*)\s+(_\w+)\s*;'
$sameClassCallRegex = [regex]'(?<!\.)\b(\w+)\s*\('
$crossClassCallRegex = [regex]'\b(_\w*[Oo]rchestration\w*)\.(\w+)\s*\('
$boWrapCallRegex = [regex]'\b(\w*[Oo]utput|\w*[Rr]esult)\.(\w+)\.(\w+)\s*\('

$candidates = @()
$files = Get-ChildItem -Path $orchestrationRoot -Recurse -Filter "*.cs" -File |
    Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | Sort-Object FullName

Write-Host "Scanning $($files.Count) files under $orchestrationRoot ..."

foreach ($file in $files) {
    $lines = @(Get-Content -LiteralPath $file.FullName)
    if (-not $lines) { continue }

    # Pass 1: every public method declaration in this file -- (name, startLine), used both as the
    # same-class call target set and as method-range boundaries for pass 2.
    $methodStarts = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $match = $publicMethodDeclRegex.Match($lines[$i])
        if ($match.Success) {
            $methodStarts += [PSCustomObject]@{ Name = $match.Groups[1].Value; Line = $i }
        }
    }
    if ($methodStarts.Count -eq 0) { continue }

    $methodNames = [System.Collections.Generic.HashSet[string]]::new([string[]]($methodStarts | ForEach-Object { $_.Name }))

    # Orchestration-typed fields declared anywhere in the file -- used for the cross-class heuristic.
    $orchestrationFields = @{}
    foreach ($line in $lines) {
        $fieldMatch = $orchestrationFieldRegex.Match($line)
        if ($fieldMatch.Success) {
            $orchestrationFields[$fieldMatch.Groups[2].Value] = $fieldMatch.Groups[1].Value
        }
    }

    # Pass 2: scan each method's approximate body (its start line to the line before the next method
    # declaration, or EOF) for the three call shapes.
    for ($m = 0; $m -lt $methodStarts.Count; $m++) {
        $outerName = $methodStarts[$m].Name
        $bodyStart = $methodStarts[$m].Line
        $bodyEnd = if ($m + 1 -lt $methodStarts.Count) { $methodStarts[$m + 1].Line - 1 } else { $lines.Count - 1 }

        for ($ln = $bodyStart; $ln -le $bodyEnd; $ln++) {
            $text = $lines[$ln]
            $lineNumber = $ln + 1

            foreach ($sameClassMatch in $sameClassCallRegex.Matches($text)) {
                $target = $sameClassMatch.Groups[1].Value
                if ($target -ne $outerName -and $methodNames.Contains($target)) {
                    $candidates += [PSCustomObject]@{
                        File = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
                        Line = $lineNumber
                        Outer = $outerName
                        Kind = "same-class"
                        Target = $target
                        Evidence = $text.Trim()
                    }
                }
            }

            foreach ($crossClassMatch in $crossClassCallRegex.Matches($text)) {
                $fieldName = $crossClassMatch.Groups[1].Value
                $method = $crossClassMatch.Groups[2].Value
                $typeName = if ($orchestrationFields.ContainsKey($fieldName)) { $orchestrationFields[$fieldName] } else { $fieldName }
                $candidates += [PSCustomObject]@{
                    File = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
                    Line = $lineNumber
                    Outer = $outerName
                    Kind = "cross-class"
                    Target = "$typeName.$method"
                    Evidence = $text.Trim()
                }
            }

            foreach ($boWrapMatch in $boWrapCallRegex.Matches($text)) {
                $verb = $boWrapMatch.Groups[3].Value
                if ($LinqBlacklist -notcontains $verb) {
                    $entity = $boWrapMatch.Groups[2].Value
                    $candidates += [PSCustomObject]@{
                        File = $file.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
                        Line = $lineNumber
                        Outer = $outerName
                        Kind = "bo-wrap"
                        Target = "$entity.$verb"
                        Evidence = $text.Trim()
                    }
                }
            }
        }
    }
}

ConvertTo-Json -InputObject @($candidates) -Depth 4 | Set-Content -Path $OutputPath -Encoding utf8
Write-Host "Wrote $($candidates.Count) candidates to $OutputPath"
Write-Host ""
Write-Host "By kind:"
$candidates | Group-Object Kind | Sort-Object Count -Descending | ForEach-Object { Write-Host "  $($_.Name): $($_.Count)" }
Write-Host ""
Write-Host "Next step: review $OutputPath (grouped/filtered as needed), open each candidate's File/Line to"
Write-Host "confirm it's real, then add confirmed edges to src/Data/nested-action-groups.json."

return $candidates
