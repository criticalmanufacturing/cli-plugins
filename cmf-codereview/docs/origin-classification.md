# Origin classification validation for #47926

US [#47926](https://dev.azure.com/criticalmanufacturing/Micron/_workitems/edit/47926) adds the missing
declared-name Origin classifier to the #47928 foundation, excluding Constants holders from both name
sets and consolidating the walks as specified by absorbed duplicate #47935.

The branch is based on PR #11 at `859d9433bf2787575b685f179ea32d4663d9ff29`, because current main
does not yet contain its resolver/models. Review the child diff against
`feature/47928-action-group-wiring`. After #11 merges, rebase onto main, retarget the PR and rerun
validation. The ADO source reference is DevOps `d2f20358396cc2fc466641dbe190405cf036953c`.

## Acceptance reproducer

`ActionGroupOriginTests.ConstantsClassAndMethodCollision_DoesNotClassifyPlatformGroupAsCustom`
creates a generic metadata holder under `Common/Constants/Operations` with the same class and
method names as a platform Action Group. With the classifier ported but the Constants exclusion
omitted, it fails with **Expected: Oob; Actual: Custom**. The exclusion fixes this regression.
The tests also retain genuine implementations outside Constants, distinctive-method fallback,
the ADO lifecycle guard, Unknown behavior and wrapper/cache compatibility.

The public helper matches an exact Constants directory segment with either separator. Collection
uses the same predicate for both name sets, before the shared `Root.DescendantNodes()` traversal.
The scanner passes its existing file cache and calls the combined collector once. It does not
exclude Constants files from ordinary findings. Markdown/HTML expose Origin; enriched JSON and
SARIF preserve it, including when reports are generated later without the original source.

## Fresh reference measurements

Measured on 24 November 2026, Windows, .NET runtime 8.0.22, against an authorized reference checkout
at `9a1b83890fc0ddb64822ee33432f9d07c8341393`. A pre-existing build-variable YAML modification was
left untouched. The scanned C# input fingerprint was
`21d71579576b50a7a51378061c540277b8dd4991f597b4c1516ac4c115be8a80` (SHA-256 over ordered relative
paths and file bytes).

- Both scope contained 2,113 C# files; 348 were under Constants, leaving 1,765 eligible files.
- The reference used two independent file enumerations and cached syntax walks, collecting types
  and methods separately. Both reference walks applied the same Constants exclusion as the candidate.
- The candidate called `CollectDeclaredNames` once with the same warmed cache.
- Three warm-up pairs preceded 15 alternating before/after trials. Median collection time was
  **662.87 ms** for two passes and **313.40 ms** for the shared pass, a **52.7% reduction**.
  Observed ranges were 611.73–907.79 ms and 295.38–375.79 ms respectively. These are local
  collection-phase measurements, not a whole-scan performance claim or a timing gate.
- Both approaches and the public wrappers produced identical sets: 1,757 type names and 603 method
  names. All 352 call-graph-enriched summaries were byte-identical between the two-pass reference
  and combined collector, SHA-256
  `4e07166ea2c917639229bad723c9491d605147bf08a54263b83c34da6f5f5a5c`.
- Comparing the classifier with unfiltered versus Constants-filtered indexes corrected 236 summaries
  from Custom to Oob. The confirmed constant-holder names `MaterialManagementOrchestration`,
  `ExceptionManagementOrchestration` and `SmartTables` occurred in the unfiltered type set and were
  absent from the filtered set. This comparison isolates the Constants exclusion; it does not compare
  against the parent foundation's intentionally Unknown origins.

The reference and candidate used identical source, exclusion and classification semantics for the
consolidation comparison. The historical 526/252 ms figures were not reused as current evidence.

## Scan and test validation

Actual parent and candidate executable scans of the same reference checkout each produced 2,640
findings. Default findings JSON was byte-identical, SHA-256
`dd435b38aff27528968dfccc0ace0277a1508c89335c1b9afd31f0b1821ddc82`.
Enriched summaries intentionally gain classified Origin values compared with the parent foundation.
The name-collection refinement changes no finding, severity, filtering, baseline or failure behavior.

The fresh parent baseline passed 322 tests. After implementation, restore and Release build
succeeded; all **353 .NET tests** and **13 package-helper tests** passed. The 31 new generic cases
need no Product binaries and execute no DEE business logic. Local restore/build reported NU1900
warnings because vulnerability feeds were unreachable.

```powershell
dotnet restore cmf-codereview.sln
dotnet build cmf-codereview.sln -c Release --no-restore
dotnet test cmf-codereview.sln -c Release --no-build
node --test scripts/package.test.mjs
```

This is implementation and local validation evidence. Teammate review, merge, acceptance and
publication are separate states; neither #47926 nor #47927 is marked accepted by this work.
