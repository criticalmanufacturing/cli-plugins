# Discovering candidate nested Action Groups

Run the three tools from one scratch directory, passing a source repository root:

```powershell
& <plugin>/tools/discover-nested-action-groups.ps1 -RepoRoot <repository-root> -OutputPath ./candidates.json
& <plugin>/tools/join-chains.ps1
& <plugin>/tools/dedupe-chains.ps1
```

Discovery defaults to `MES/Host/Biz/Orchestration`; use `-OrchestrationSubPath` for another layout.
The tools write arrays to candidates.json, joined-chains.json and unique-pairs.json. Join and dedupe
also accept `-InputPath` and `-OutputPath`. Keep these reports outside the analyzed repository.

Candidates are regex matches, not curated facts. Read each reported file/line and confirm both hops
and the terminal BusinessObject call before editing the embedded catalog. CRUD and same-named methods
can produce noise. Method ranges are approximate; overloads, unusual formatting and BusinessObject
nesting need manual analysis. The scanner never runs discovery or updates the catalog automatically.
