# Action Group wiring and findings compatibility

US [#47928](https://dev.azure.com/criticalmanufacturing/Micron/_workitems/edit/47928) introduces
the missing GitHub resolver and summary foundation. It selectively ports the many-to-many model
from ADO DevOps `d2f20358396cc2fc466641dbe190405cf036953c`, onto GitHub main
`4472dee43857a38b2c9b317cc1ee963061424c3d`. Global version precedence corrects the ADO implementation's
per-root ordering. [rules.md](rules.md#action-group-summary) defines counts, risk and remaining scope.

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
Without that option, legacy report generation requires no repository and adds no summary.

`review` renders its in-memory scan summary in either JSON mode, using the existing scan graph
without a second repository analysis. Use the enriched findings file to preserve that graph-derived
context for later independent report generation. Summaries include all repository MasterData wiring,
even when findings are narrowed by scope, changed-file filtering, disabled rules or baselines.

For `scan --report-format Sarif`, `--include-action-group-summary` adds
`runs[0].properties.actionGroupSummaries`; default scan SARIF stays unchanged. `review` and `report`
include summaries in SARIF when available. Summary risk never creates SARIF results or changes
diagnostic severity. Markdown and HTML show the same counts in both Standard and Detailed modes.

## Acceptance evidence

The implementation starts from a fresh 279-test passing baseline. Generic fixtures in
`MasterDataActionGroupResolverTests` and `ActionGroupIntegrationTests` cover:

- Three groups for one DEE in one file; distinct DEE counts and all risk thresholds.
- Repeated pairs across numeric versions and two roots, reversed discovery order, equal-version
  tie-breaking, duplicate roots, malformed JSON and manifest-based root discovery.
- Static endpoint traversal, transitive callers and cycles; summary context under scope/filter/baseline.
- Default arrays, opt-in envelopes, legacy numeric severity, command binding and failure exit codes.
- Summary round trips in both modes of Markdown, HTML and schema-validated SARIF after MasterData
  is removed; authoritative empty summaries and HTML/Markdown escaping.

These tests require no Product binaries and execute no DEE business logic. Integration evidence is
synthetic source/metadata, not deployment or runtime validation. Review, merge, acceptance and
publication remain separate states.

Local validation on 24 November 2026: restore and Release build succeeded; all 322 .NET tests and
13 package-helper tests passed. Restore/build reported NU1900 warnings because the NuGet
vulnerability feed was unreachable. The built executable also passed a synthetic scan/report/review
smoke covering both JSON contracts, failure exit codes and all three report formats.
