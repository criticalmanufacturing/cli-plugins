<#
.SYNOPSIS
    Step 2 of the discovery pipeline: joins discover-nested-action-groups.ps1's raw candidates into real
    2-hop chains, so you don't have to eyeball ~1700+ mostly-noisy bo-wrap candidates.

.DESCRIPTION
    A bo-wrap candidate only matters if something actually calls into it first -- an isolated bo-wrap
    with no same-class/cross-class edge pointing at its Outer method is just a normal single-level Action
    Group (no nesting, nothing to catalog). This script joins every same-class/cross-class candidate's
    Target against the bo-wrap candidates' Outer, keeping only bo-wraps that are reachable that way. This
    typically shrinks the confirmation set by >90% (e.g. ~1900 raw candidates -> ~200 joined chains in the
    sweep that built this catalog).

    Run from the tools/ folder after discover-nested-action-groups.ps1 has written ./candidates.json.
    Writes ./joined-chains.json. Follow with dedupe-chains.ps1 to collapse to unique (Outer, Mid) pairs
    before confirming each one by hand or via a targeted agent.
#>
param([string]$InputPath = "./candidates.json", [string]$OutputPath = "./joined-chains.json")
$ErrorActionPreference = "Stop"
$candidates = @(Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json | ForEach-Object { $_ } | Where-Object { $null -ne $_ })
$sameOrCross = $candidates | Where-Object { $_.Kind -in @("same-class","cross-class") }
$boWraps = $candidates | Where-Object { $_.Kind -eq "bo-wrap" }
$boWrapsByOuter = $boWraps | Group-Object Outer -AsHashTable -AsString

$chains = @()
foreach ($edge in $sameOrCross) {
    $targetMethod = ($edge.Target -split '\.')[-1]
    if ($boWrapsByOuter.ContainsKey($targetMethod)) {
        foreach ($wrap in $boWrapsByOuter[$targetMethod]) {
            $chains += [PSCustomObject]@{
                Outer = $edge.Outer
                OuterFile = $edge.File
                OuterLine = $edge.Line
                Kind = $edge.Kind
                Mid = $edge.Target
                MidFile = $wrap.File
                MidLine = $wrap.Line
                TerminalBO = $wrap.Target
            }
        }
    }
}
Write-Host "Same/cross-class candidates: $($sameOrCross.Count)"
Write-Host "Joined 2-hop chains (outer -> mid -> BO wrap): $($chains.Count)"
ConvertTo-Json -InputObject @($chains) -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$chains | Select-Object Outer, Mid, TerminalBO, OuterFile -Unique | Format-Table -AutoSize | Out-String -Width 300
