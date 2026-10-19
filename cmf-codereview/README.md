# cmf-codereview

`cmf-codereview` is a static analysis CLI for CM Framework (Critical Manufacturing) customization code. It
scans DEE and Orchestration C# code for performance/correctness anti-patterns — loads inside loops,
excessive lazy loading, config misuse, direct entity instantiation, and more — and produces a report
(Markdown, a self-contained HTML page, or SARIF 2.1.0). Markdown/HTML group findings by severity or rule.
The folders to scan are
discovered from the repository's own `cmfpackage.json` package manifests, so it works with any
customization project name (`Cmf.Custom.*`, `Cmf.Semi.*`, ...), not a hardcoded layout.

It is packaged as a `cmf-*` plugin binary for the CM CLI, so once installed it can be invoked directly as
`cmf-codereview` or discovered by `cmf` as `cmf codereview` (the CM CLI resolves `cmf <name>` to a `cmf-<name>`
binary on PATH).

## Development prerequisites

Run the commands below from the `cmf-codereview` directory in this repository.
Install a .NET 8 SDK compatible with `global.json`. Node.js and npm are needed for
npm packaging and release scripts. The repository root `NuGet.Config` provides the
nuget.org package feed.

See the [repository development guide](../README.md) for build, test, packaging,
and release commands. The plugin uses dotnet and npm directly.

## Installation

Once published, install like any other CM CLI plugin:

```bash
npm install --global @criticalmanufacturing/codereview --registry https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/
```

After installation, `cmf codereview` is available alongside your other `cmf` commands.

For local development, or until the npm package is published, build and install from source instead:

```bash
dotnet pack src/cmf-codereview.csproj -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg cmf-codereview
```

Or run directly from source without installing anything:

```bash
dotnet build cmf-codereview.sln
dotnet run --project src/cmf-codereview.csproj -- scan --repository-root /path/to/your-project
```

## Usage

Quick start — scan and report in one step:

```bash
cmf-codereview review --repository-root <path>
```

`cmf-codereview` has three commands: `scan`, `report`, and `review`. Every option, default, and example is
documented in [docs/commands.md](docs/commands.md) — start there for anything beyond the quick start above.
`cmf-codereview --help` (and `<command> --help`) is also always available and reflects the current option set.

## What it looks for

See [docs/rules.md](docs/rules.md) for the full list of the 17 rules, their severity, and suggested fixes.

## Configuration

Everything is optional and lives in the scanned repository, not the tool: `.codereview.json`
(disable rules, override severities, declare project-specific custom rules as data), inline
`// codereview-disable-line` suppression comments, and a `.codereview.baseline.json` accepted-debt
baseline (`scan --update-baseline`) so CI gates only fail on new findings. See
[docs/configuration.md](docs/configuration.md).

## Project layout

- `src/` — the CLI executable, built on `Cmf.CLI.Core` (`BaseCommand`/`[CmfCommand]` discovery, same
  as `cmf-dev`/`cmf-pipeline`), wiring `scan`/`report`/`review` under `Commands/`, plus the scanning
  engine it calls into: `Analysis/` (syntax-tree analyzers), `Rules/` (the rule engine, including the
  reflection-based `EntityCatalogResolver`), `Models/`, `Enums/`, and the report renderers
  (`CmReportGenerator.cs` for Markdown, `CmHtmlReportGenerator.cs` for HTML, and
  `CmSarifReportGenerator.cs` for SARIF).
- `tests/` — xUnit tests, referencing `src/cmf-codereview.csproj` directly.
- `npm/` — the npm package (`package.json` with `bin`/`goBinary`, `postinstall.js`, `run.js`, `utils.js`)
  that wraps the self-contained per-OS binaries for distribution as a CM CLI plugin.
- `package.json` (plugin root) — build/bump/pack/publish scripts driving the npm packaging pipeline
  (`npm run build:bundle`, `npm run pack`, etc.).
- `docs/commands.md` — full command reference (options, defaults, examples).
- `docs/rules.md` — rule reference.
- `docs/configuration.md` — per-project configuration: `.codereview.json`, inline suppressions, baseline.
- `docs/architecture.md` — diagrams of how the CLI bootstraps, dispatches, and runs the rule engine
  (renders inline on platforms with native Mermaid support, e.g. GitHub).

## Testing

```bash
dotnet test
```
