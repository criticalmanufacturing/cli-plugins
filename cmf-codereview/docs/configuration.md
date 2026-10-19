# Configuration

All configuration is optional — with no config files present, the scanner runs every built-in rule
at its default severity, exactly as documented in [rules.md](rules.md). Three mechanisms exist, all
read from the **root of the scanned repository** (`--repository-root`), so they live with the
project being reviewed, not with the tool:

1. `.codereview.json` — disable rules, override severities, define custom project-specific rules.
2. Inline suppression comments — silence one finding at one specific line, in the code itself.
3. `.codereview.baseline.json` — accept all existing findings as known debt and only report new ones.

## `.codereview.json`

```jsonc
{
  // Per-rule settings, keyed by rule name (see rules.md for the full list).
  "rules": {
    "ExcessiveLazyLoading": { "enabled": false },          // turn a rule off entirely
    "LoadReferenceForFkOnly": { "severity": "High" }       // override the severity (Medium | High | Critical)
  },

  // Project-specific custom rules, declared as data — no code required.
  "customRules": [
    {
      // Flags the named method call(s) inside a loop body, with the same nested-loop and
      // caller-induced escalation as the built-in loop rules (LoadInsideLoop etc.).
      "name": "SyncWidgetInsideLoop",
      "kind": "methodCallInLoop",
      "methodNames": ["SyncWidget", "SyncWidgets"],
      "receiverContains": "widgetService",                  // optional: receiver text filter (case-insensitive)
      "actionNoun": "Widget sync",                          // used in the finding description
      "insideLoopFix": "Batch the widget ids and call SyncWidgets once outside the loop.",
      "inducedByCallerFix": "Hoist the sync to the caller and pass the synced data in."
    },
    {
      // Flags the named property/properties dereferenced off a known entity-type reference
      // (x.Material.WidgetCode), including the null-conditional form (x.Material?.WidgetCode).
      "name": "WidgetCodeAccess",
      "kind": "entityPropertyAccess",
      "propertyNames": ["WidgetCode"],
      "severity": "High",                                   // Medium (default) | High | Critical
      "description": "WidgetCode is dereferenced off an entity reference.",
      "suggestedFix": "Read it via GetNativeValue<string>(\"WidgetCode\") instead.",
      "onlyInLoopOrLambda": true                            // optional: restrict to loop bodies / LINQ lambdas
    }
  ]
}
```

Notes:

- Rule names in `rules` also cover custom rules and `LoopInducedByCaller` (the shared name that
  loop-sensitive rules emit for caller-induced repetition).
- A severity override applies to every finding the rule emits, including ones that would have
  escalated — the override wins. (The finding's `Escalation` object, when present, still records
  what the scanner computed, so the compounding context isn't lost.)
- Comments and trailing commas are allowed in the file.
- A malformed file fails the scan with a clear error rather than being silently ignored.

## Inline suppression comments

```csharp
entity.Load();                       // codereview-disable-line
entity.Load();                       // codereview-disable-line LoadInsideLoop
// codereview-disable-next-line LoadInsideLoop, ExcessiveLazyLoading
var x = entity.Load(item.Material.Description);
```

- Bare marker → suppresses **every** rule on that line.
- With rule name(s) (comma or space separated, case-insensitive) → suppresses only those rules.
- Use these for individually-reviewed false positives; prefer `.codereview.json` when a whole rule
  is noisy for the project.

## `.codereview.baseline.json`

Generate/refresh it with:

```bash
cmf-codereview scan --repository-root . --update-baseline
```

This writes every current finding (file + rule + flagged code — line numbers are deliberately not
part of the match, so unrelated edits that shift lines don't un-baseline anything) to
`.codereview.baseline.json` and exits 0. From then on, any scan of that repository only reports
findings **not** in the baseline — letting a team adopt the tool (and a `--fail-on` CI gate) on an
existing codebase without first fixing hundreds of historical findings.

Commit the baseline file to the repository. Shrink it over time by deleting entries (or
regenerating) as the underlying debt gets fixed.

## Tutorial: your first custom rule

Say your project has an in-house service, `_labelPrinter.PrintLabel(...)`, that makes a network
round-trip per call — a classic "fine once, terrible in a loop" pattern the built-in rules don't
know about. Make the scanner enforce it in three steps:

**1. Create `.codereview.json` at the root of the scanned repository** (the path you pass as
`--repository-root` — not the tool's install directory):

```jsonc
{
  "customRules": [
    {
      "name": "PrintLabelInsideLoop",
      "kind": "methodCallInLoop",
      "methodNames": ["PrintLabel"],
      "receiverContains": "labelPrinter",   // optional — drop it to match any receiver
      "actionNoun": "Label printing (network round-trip)",
      "insideLoopFix": "Collect the label requests and send them in one PrintLabels batch call after the loop.",
      "inducedByCallerFix": "This method is called from a loop elsewhere - hoist the printing to the caller and batch it there."
    }
  ]
}
```

**2. Run a scan** — no flags needed, the file is picked up automatically:

```bash
cmf-codereview scan --repository-root .
```

Code like this:

```csharp
foreach (var lot in lots)
{
    _labelPrinter.PrintLabel(lot.Name);   // <- flagged
}
```

now produces a `PrintLabelInsideLoop` finding at High severity — Critical if the loop is nested or
the enclosing method is itself called from a loop elsewhere (custom `methodCallInLoop` rules get
the same escalation, `LoopInducedByCaller` cross-method detection, and structured `Escalation`
output as the built-in loop rules).

**3. Tune it like any built-in rule.** The same `rules` section, suppression comments, and
baseline all apply to custom rules by name:

```jsonc
{
  "rules": {
    "PrintLabelInsideLoop": { "severity": "Critical" }   // or "enabled": false
  },
  "customRules": [ /* ... */ ]
}
```

```csharp
_labelPrinter.PrintLabel(summary);   // codereview-disable-line PrintLabelInsideLoop
```

For "flag property X dereferenced off an entity reference" patterns, use
`"kind": "entityPropertyAccess"` instead — see the reference example at the top of this page.
Those are the two supported kinds; a pattern that fits neither needs a code rule (see
`docs/rules.md` and `IReviewRule`) — or ask for the kind to be added.

Commit `.codereview.json` alongside the code so every teammate and CI run enforces the same rules.
