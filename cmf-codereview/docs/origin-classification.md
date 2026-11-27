# Action Group Origin classification

Origin is a static heuristic based on declarations in the analyzed repository. It helps distinguish
custom implementations from platform-provided behavior; it is not runtime or assembly-level proof.

## Classification evidence

The scanner collects type and method names from the full Both scope using its shared syntax cache.
A matching implementation type gives `Custom`. A distinctive matching method can also give `Custom`
when an Action Group runtime module name differs from its C# type name. With an available type index
but no matching evidence, Origin is `Oob`. Without a type index, Origin is `Unknown`.

An exact, case-sensitive `Constants` directory segment is excluded from both indexes before traversal.
Windows, Unix and mixed separators are supported, including relative paths starting with `Constants/`.
Similar directory names and a file named `Constants.cs` are not excluded. A genuine same-named
implementation outside Constants still contributes evidence. Ordinary rules continue to analyze
Constants files.

Generic lifecycle method names alone do not establish custom ownership. The full guard and its
limitations are documented in [the rule reference](rules.md#action-group-origin).

## Retaining Origin in reports

```powershell
cmf-codereview scan --repository-root . --include-action-group-summary --output findings.json
cmf-codereview report --findings-path findings.json --report-format Html --report-path report.html
```

The enriched findings file records classified summaries for later reports. `review` uses its in-memory
scan context for the immediate report and can save the enriched file with the same option. Recorded
summaries, including an empty summary, remain authoritative if source changes later.

The default findings array does not record declaration indexes or graph-derived summaries. Legacy
report fallback can reconstruct wiring counts from current MasterData, but Origin remains `Unknown`
and possible endpoints are unavailable. Markdown, HTML and SARIF explain this limitation and how to
retain scan context. Empty endpoint lists in this fallback do not prove that no callers exist.

See [the findings compatibility contract](action-group-wiring.md) for the supported JSON shapes.
