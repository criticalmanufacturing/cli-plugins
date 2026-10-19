# Command Reference

`cmf-codereview` has three subcommands: `scan`, `report`, and `review`. All of them are built with
`System.CommandLine`, so `--help` is available on the root command and on every subcommand
(`cmf-codereview --help`, `cmf-codereview scan --help`, etc.) and always reflects the current option set.

Exit code is `0` on success and non-zero on any unhandled error (e.g. a missing required option, an
invalid path, a malformed findings file), or when `scan --fail-on` is used and a qualifying finding exists
(see below).

## `scan`

Runs the review engine over a repository and prints the findings as a JSON array. Optionally also saves
that JSON to a file.

```bash
cmf-codereview scan --repository-root <path> [--scope DEEs|Orchestration|Both] [--roots <rel1> <rel2> ...] [--output <file>] [--changed-files-list <file>] [--fail-on Critical|High|Medium] [--update-baseline]
```

| Option | Required | Default | Description |
|---|---|---|---|
| `--repository-root` | Yes | — | Root of the repository to scan. |
| `--scope` | No | `Both` | Which default folder set to scan: `DEEs`, `Orchestration`, or `Both`. Ignored if `--roots` is given. The concrete folders for `--scope` are auto-detected per repository (see below) — not hardcoded to one project name. |
| `--roots` | No | (derived from `--scope`) | One or more explicit relative folders to scan instead of the `--scope` default. Accepts multiple values, e.g. `--roots Cmf.Custom.Data/Cmf.Custom.Data.Packages/DEEs Cmf.Custom.Business/Cmf.Custom.Common`. |
| `--output` | No | (none — stdout only) | Path to also write the findings JSON to disk. Parent directories are created automatically. |
| `--changed-files-list` | No | (none — reports on every scanned file) | Path to a text file listing changed files, one relative or absolute path per line (e.g. the output of `git diff --name-only`). When given, only findings on those files are returned — but the whole repository is still analyzed for cross-reference and loop-exposure context, so a finding on a changed file caused by an unchanged caller elsewhere is still correctly reported. Intended for CI gates that should only comment on files actually touched by a change. |
| `--fail-on` | No | (none — always exits `0`) | `Critical`, `High`, or `Medium`. When given, the command exits `1` if any reported finding is at or above this severity. Omit it to keep `scan` purely informational. |
| `--update-baseline` | No | `false` | Write all current findings (ignoring any existing baseline) to `.codereview.baseline.json` at the repository root, accepting them as known debt — subsequent scans only report findings not in the baseline. Always exits `0` (ignores `--fail-on`). See [docs/configuration.md](configuration.md). |

`scan` (and `review`) also honor the scanned repository's optional configuration: `.codereview.json`
(disable rules / override severities / custom rules), inline `// codereview-disable-line`
suppression comments, and the `.codereview.baseline.json` accepted-debt baseline — see
[docs/configuration.md](configuration.md).

**Root discovery.** The `--scope` default folders aren't hardcoded to one customization project's
naming — they're discovered from the repository's own `cmfpackage.json` package manifests, the same
metadata the CM Framework build/pack tooling reads:
- Every manifest with `"packageType": "Business"` contributes its `*.Orchestration`/`*.Common`/`*.Services`
  sibling subfolders as Orchestration roots.
- Every manifest with `"packageType": "Data"` contributes a DEE root for each `contentToPack` entry
  whose `"contentType": "DEE"`, resolved from that entry's own `source` glob (e.g. `"DEEs/*"` under
  the manifest's directory) — so both the nested Micron-style `Cmf.<Project>.Data.Packages/DEEs` shape
  and a flat `Cmf.<Project>.Data/DEEs` shape resolve correctly, and a repository with more than one
  Data package (e.g. a Tests package bundling its own DEE fixtures) gets every one of them.

This works for any customization project name (`Custom`, `Semi`, ...) since it reads what the
manifests actually say instead of assuming a naming convention. The manifest search is bounded and
skips build/VCS/package-cache folders (`bin`, `obj`, `.git`, `node_modules`, `packages`, ...) so it
stays fast even on a large repository. If no manifest in the repository resolves to a Business or Data
package at all, `scan` prints a warning to stderr (`no .cs files found under the resolved scan
roots ...`) instead of silently reporting zero findings — pass `--roots` explicitly for a repository
with a non-standard layout.

**Entity catalog (requires a restored repository).** The entity-aware rules (`DirectEntityInstantiation`,
`ExcessiveLazyLoading`, `LoadReferenceForFkOnly`, `DeleteRemoveInsideLoop`, and the entity-specific
suggested-fix text on the Save/Create/Delete-in-loop rules) recognize entity types by reflecting over
the CM platform assemblies the repository references — resolved from each project's
`obj/project.assets.json` and loaded (metadata-only) from the NuGet cache. This means **the repository
must have been restored** (`dotnet restore`, which also runs as part of `dotnet build`): without a
`project.assets.json` the catalog is empty, those rules quietly stop firing, and `scan` prints a
warning to stderr. The loop/orchestration/config rules do not depend on the catalog and are unaffected.

> **CI / Azure DevOps:** run the scan in a job that has already restored the solution — the natural
> ordering (restore/build, then scan, same working directory and agent) satisfies this automatically,
> and a CI restore fetches the exact referenced platform version. If code review runs as a *separate*
> job or on a fresh checkout with no restore, add a `dotnet restore` step first (or reuse the build
> job's restored workspace); otherwise entity-aware rules will be silently skipped (with the warning).

**Examples**

Scan everything and just look at the output:
```bash
cmf-codereview scan --repository-root C:\repos\YourProject
```

Scan only DEEs and save the findings for later:
```bash
cmf-codereview scan --repository-root C:\repos\YourProject --scope DEEs --output findings.json
```

Scan a custom set of folders:
```bash
cmf-codereview scan --repository-root C:\repos\YourProject --roots Cmf.Custom.Data/Cmf.Custom.Data.Packages/DEEs/Materials
```

Scan only the files changed in a pull request, and fail the build if any Critical finding is on one of them:
```bash
git diff --name-only origin/main...HEAD > changed-files.txt
cmf-codereview scan --repository-root . --changed-files-list changed-files.txt --fail-on Critical --output findings.json
```

**Output shape**: a JSON array of `Finding` objects — each with `FilePath`, `RelativePath`, `LineNumber`,
`Severity`, `Rule`, `Description`, `Snippet`, `SuggestedFix`, `MethodName`, `ServiceEndpoint`,
`ServiceEndpointSource`, `CrossReference`, `LoopExposure`, and `Escalation`. See [docs/rules.md](rules.md)
for what each `Rule` value means.

`Severity` is serialized by name (`"Medium"`, `"High"`, `"Critical"`), never as a number. (Scan files
written by versions before this change carried numbers — `0`=Medium, `1`=High, `2`=Critical, the reverse of
the P0-style convention — and `report` still reads that legacy form.)

`Escalation` is non-null only when the finding's severity was raised above its rule's base severity, and
records why machine-readably: `BaseSeverity`, `Reasons` (`"NestedLoop"` and/or `"CalledFromLoopElsewhere"`),
and `NestedLoopDepth` (set only for the nested-loop reason). The same information appears as prose in
`Description`; dashboards and CI gates should read `Escalation` instead of parsing that prose.

## `report`

Renders a report from a findings JSON file previously produced by `scan` (or `review`), in Markdown
(the default) or a self-contained HTML page.

```bash
cmf-codereview report --findings-path <file> [--repository-root <path>] [--report-path <file>] [--report-mode Standard|Detailed] [--report-format Markdown|Html] [--title <text>] [--summary <text>] [--stdout]
```

| Option | Required | Default | Description |
|---|---|---|---|
| `--findings-path` | Yes | — | Path to a findings JSON file produced by `scan`. |
| `--repository-root` | No | current directory | Used only to compute the default `--report-path` when `--report-path` is not given. Ignored when `--stdout` is set. |
| `--report-path` | No | `Tools/CodeReview/Reports/cm-framework-scan-<mode>-<timestamp>.<md\|html>` under `--repository-root` | Output path for the report. The default file extension follows `--report-format`. Ignored when `--stdout` is set. |
| `--report-mode` | No | `Standard` | `Standard` (severity-grouped) or `Detailed` (rule-grouped) report shape — see [docs/rules.md](rules.md#cross-reference-and-loop-exposure-context). |
| `--report-format` | No | `Markdown` | `Markdown` (portable — for PR comments, plain viewers) or `Html` (a single self-contained page — no external assets, opens straight from disk — with a live text filter, collapsible severity-colored rule sections, and light/dark theming, for triaging findings in a browser). Both formats honour `--report-mode`. |
| `--title` | No | `"C# Code Review for CM Framework Scan"` | Report heading. |
| `--summary` | No | `"Automated CM framework scan for looped loads, smart table resolves, config access, direct entity construction, and related review heuristics."` | Report summary line. |
| `--stdout` | No | `false` | Print the report to stdout instead of writing a file — no file is created when this is set. Works for either format (`--report-format Html` prints the HTML). Useful for piping the content elsewhere (e.g. into a pull request comment) without leaving a report artifact on disk. |

Prints the resolved report path to stdout on success (or the report content itself, when `--stdout` is set).

**Examples**

Render the default (Standard, Markdown) report next to a findings file:
```bash
cmf-codereview report --findings-path findings.json --repository-root C:\repos\YourProject
```

Render a Detailed, rule-grouped report with a custom title:
```bash
cmf-codereview report --findings-path findings.json --report-mode Detailed --title "Q3 DEE Performance Audit" --report-path C:\reports\q3-audit.md
```

Render a Detailed report as a self-contained, filterable HTML page for browser triage:
```bash
cmf-codereview report --findings-path findings.json --report-mode Detailed --report-format Html --repository-root C:\repos\YourProject
```

Get the report Markdown on stdout, without writing any file (e.g. to capture into a pipeline variable):
```bash
cmf-codereview report --findings-path findings.json --stdout
```

## `review`

One-shot: runs `scan` and `report` together against a repository, so both the findings JSON and the
report are produced from a single command.

```bash
cmf-codereview review --repository-root <path> [--scope DEEs|Orchestration|Both] [--roots <rel1> <rel2> ...] [--findings-path <file>] [--report-path <file>] [--report-mode Standard|Detailed] [--report-format Markdown|Html] [--title <text>] [--summary <text>]
```

| Option | Required | Default | Description |
|---|---|---|---|
| `--repository-root` | Yes | — | Root of the repository to scan. |
| `--scope` | No | `Both` | Same as `scan --scope`. |
| `--roots` | No | (derived from `--scope`) | Same as `scan --roots`. |
| `--findings-path` | No | `Tools/CodeReview/Reports/cm-framework-scan-<timestamp>.json` under `--repository-root` | Where to write the intermediate findings JSON. |
| `--report-path` | No | (see `report` command) | Output path for the report. |
| `--report-mode` | No | `Standard` | Same as `report --report-mode`. |
| `--report-format` | No | `Markdown` | Same as `report --report-format`. |
| `--title` | No | (see `report` command default) | Same as `report --title`. |
| `--summary` | No | (see `report` command default) | Same as `report --summary`. |

`review` always writes both a findings JSON file and a report file — it has no `--changed-files-list`,
`--fail-on`, or `--stdout` equivalent. For a CI gate that scopes to changed files, fails the build, and/or
avoids writing a report file, chain `scan` and `report` separately instead (see the changed-files example
under `scan`, and the `--stdout` example under `report`).

Prints `Scope`, `ReportMode`, `ReportFormat`, `FindingsPath`, and `ReportPath` to stdout on success.

**Examples**

Simplest form — scan and report in one step, everything defaulted:
```bash
cmf-codereview review --repository-root C:\repos\YourProject
```

Scan only Orchestration and produce a Detailed report:
```bash
cmf-codereview review --repository-root C:\repos\YourProject --scope Orchestration --report-mode Detailed
```

Scan and produce a Detailed, self-contained HTML report for browser triage:
```bash
cmf-codereview review --repository-root C:\repos\YourProject --report-mode Detailed --report-format Html
```

## Choosing between the three commands

- Use **`review`** for everyday use — it's the one-command path from repository to report.
- Use **`scan`** on its own when you want the raw findings JSON to feed into other tooling (a dashboard, a
  CI gate, a diffing script) without generating a Markdown report at all — or when you need
  `--changed-files-list`/`--fail-on` for a PR/CI gate.
- Use **`report`** on its own to re-render a report (e.g. switch from Standard to Detailed, change the
  title, or get Markdown on stdout via `--stdout`) from a findings JSON you already have, without
  re-scanning the repository.

## Example: gating a pull request without writing a report file

A minimal pattern for a CI step that fails a PR build on Critical findings in changed files, and prints a
PR-comment-ready Markdown summary — without ever writing a `.md` report to disk:

```bash
git diff --name-only origin/$(TargetBranch)...HEAD > changed-files.txt
cmf-codereview scan --repository-root . --changed-files-list changed-files.txt --fail-on Critical --output findings.json
scanExitCode=$?
cmf-codereview report --findings-path findings.json --report-mode Detailed --stdout > comment.md
# ...post comment.md's content to the PR however your CI platform supports (e.g. an Azure DevOps
# REST API call, or an existing CLI/task for that), then propagate $scanExitCode as the step's exit code.
```

This plugin does not post pull request comments itself (it has no knowledge of Azure DevOps, GitHub, or
any other host) — that step belongs in your pipeline, using whatever mechanism your CI platform/toolchain
already provides for commenting on a PR.
