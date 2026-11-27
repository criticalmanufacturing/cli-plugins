# Action Group wiring and findings compatibility

A DEE can be wired to several Action Groups. Every distinct wiring contributes to each group's
summary, and repeated pairs retain the highest numeric version across all MasterData roots.
[rules.md](rules.md#action-group-summary) defines counts, risk and supported limits.

## Compatibility decision

`scan` and `review` continue to write the existing JSON **array** by default. The `Finding` shape,
severity serialization, baselines, suppression and failure thresholds remain unchanged. Local
consumers include `ReportCommand`, scanner/Process Rule/SARIF tests and command examples; direct
`JsonSerializer.Deserialize<List<Finding>>` callers must continue to work. Package smoke consumers
use ordinary scan output. External dashboard/PR-comment consumers have not been migrated or assumed
to understand a new top-level object.

Select the enriched contract explicitly:

```powershell
cmf-codereview scan --repository-root . --include-action-group-summary --output findings.json
cmf-codereview review --repository-root . --include-action-group-summary --findings-path findings.json
cmf-codereview report --findings-path findings.json --report-format Html --report-path report.html
```

The enriched object has `Findings` (the same array) and `ActionGroupSummaries` (the scan's resolved
summary array). Each summary includes `ActionGroup`, `DeeCount`, `RiskLevel`, `Dees`,
`PossibleServiceEndpoints` and `Origin`. Risk and origin use enum names. `FindingsIo.Deserialize`
accepts both top-level shapes and existing numeric severity values. Invalid top-level input or a
missing/null/non-array `Findings` member fails instead of silently producing a clean report.

An absent/null summary means it was not recorded; an empty array means a scan found no wiring.
`report` consumes the recorded array even if the repository has changed. For legacy input,
explicit `--repository-root` enables a MasterData-only count/risk fallback with empty endpoint lists.
Without that option, legacy report generation requires no repository and adds no wiring counts.
The report explains that endpoint context is unavailable: empty fallback lists do not prove that a
group has no callers. Use the enriched scan option or review to retain scan context. SARIF includes
this explanation in `runs[0].properties.actionGroupSummaryNote`.

`review` renders its in-memory scan summary in either JSON mode, using the existing scan graph
without a second repository analysis. Use the enriched findings file to preserve that graph-derived
context for later independent report generation. Summaries include all repository MasterData wiring,
even when findings are narrowed by scope, changed-file filtering, disabled rules or baselines.

For `scan --report-format Sarif`, `--include-action-group-summary` adds
`runs[0].properties.actionGroupSummaries`; default scan SARIF stays unchanged. `review` and `report`
include summaries in SARIF when available. Summary risk never creates SARIF results or changes
diagnostic severity. Markdown and HTML show the same counts in both Standard and Detailed modes.
