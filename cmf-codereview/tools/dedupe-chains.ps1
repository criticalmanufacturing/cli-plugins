<#
.SYNOPSIS
    Step 3 of the discovery pipeline: collapses join-chains.ps1's output to unique (Outer -> Mid)
    structural pairs -- one row per real candidate 2-hop chain worth confirming, instead of one row per
    individual BO-level "leaf" call inside Mid's body (most of which are CRUD noise like .Load/.Add
    around the one real Action-Group-firing verb).

.DESCRIPTION
    Run from the tools/ folder after join-chains.ps1 has written ./joined-chains.json. Writes
    ./unique-pairs.json. The output is still just a candidate list -- confirm each (Outer, Mid) pair by
    opening OuterFile at OuterLine, reading Mid's own body to find which single call is the genuine
    Action Group wrap (ignore Load/TryLoad/Add/Get* noise), then add the confirmed edge(s) to
    src/Data/nested-action-groups.json using the same {outer, nested, comment} shape as the rest of the
    file.
#>
param([string]$InputPath = "./joined-chains.json", [string]$OutputPath = "./unique-pairs.json")
$ErrorActionPreference = "Stop"
$chains = @(Get-Content -LiteralPath $InputPath -Raw | ConvertFrom-Json | ForEach-Object { $_ } | Where-Object { $null -ne $_ })
$unique = @($chains | Select-Object Outer, Mid, OuterFile, OuterLine -Unique | Sort-Object OuterFile, Outer)
Write-Host "Unique (Outer -> Mid) structural pairs: $($unique.Count)"
ConvertTo-Json -InputObject @($unique) -Depth 4 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$unique | Format-Table -AutoSize | Out-String -Width 300
