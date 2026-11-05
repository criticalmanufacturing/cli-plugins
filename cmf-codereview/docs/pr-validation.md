# PR deployment and DEE reference validation

The reviewer validates literal `UseReference(assembly, namespace)` declarations in `DeeDevBase`
methods and checks new deployment content against its owning Data package version.

## DEE references

Restore the DEE project and its project references before the scan. The reviewer reads the owning
`.csproj`, `obj/project.assets.json`, reference HintPaths and referenced project sources. It uses
Roslyn to bind type/extension usages and reads the referenced CMF compiler's default namespace and
assembly arrays from metadata. No product assemblies or type initializers are executed.

- `DeeInvalidReference` (High): a declared namespace is known in the available metadata but is
  unavailable from the combined default and explicitly declared assemblies.
- `DeeMissingReference` (High): a bound type/extension needs an import or assembly not supplied by
  either a declaration or the compiler defaults. The finding points to the usage.
- `DeeReferenceUnresolved` (Medium): project/dependency metadata, compiler defaults, a type, an
  assembly or non-literal arguments cannot be resolved. This is a validation limitation, not proof
  that the reference is invalid. Unknown compiler initializer layouts are not executed or guessed.

An unused C# `using` alone does not require a declaration. The product compiler combines the assembly
arguments independently of the namespace arguments: a namespace may be supplied by any default or
explicitly declared assembly, including a declaration that appears later. Empty assembly arguments,
assembly-only references, fully qualified type usages, and the harmless `UseReference("", "")` no-op
are supported. Framework assemblies supplied by the runtime take precedence over older NuGet facade
assets when binding symbols. The check covers direct `DeeDevBase` classes and deployed
`DeeActionCode`/`DeeTestCondition` methods; it does not evaluate MSBuild conditions, generate code,
execute helpers or prove completeness for arbitrary dynamic/alias-based imports.

### Deferred Product parsing parity

The reviewer currently inspects complete method bodies rather than extracting only the regions
between deployed-code markers. Its C# syntax analysis also accepts declaration forms that Product's
regex extraction does not, such as whitespace between `UseReference` and `(`. Declarations outside
the deployed region can incorrectly satisfy a reference, and syntax differences can cause missed
findings or false positives. Marker extraction and declaration-syntax parity are deferred follow-up
work, alongside host configuration, as recorded in
[User Story 53349](https://dev.azure.com/criticalmanufacturing/Micron/_workitems/edit/53349).

### Product compiler verification

The product implementation in `Platform/Host/Biz/Core/Common/DynamicExecutionEngine/Compiler.cs`
was inspected at Product commit `306f9c47cb0`. `GenerateFullActionCode` creates a new class from the
action and condition bodies. `GetUsings` combines default imports with the second arguments of
literal declarations; `GetReferences` independently combines default references with their first
arguments. `GetDefaultReferences` resolves framework DLLs from the .NET runtime directory.
`CompileActionCode` additionally applies host-configured `alsoInclude` reference rules.
`RuleEvaluatorAdapter.UseReference` itself has an empty body.

The reviewer does not reproduce a running host's configuration, keyword substitutions, arbitrary
reference paths or `alsoInclude` rules. It uses the dependencies restored in the reviewed workspace
and cannot prove that these match a deployed host. Core-only hosting and unsupported compiler
layouts also need separate validation; the current default import check targets MES projects.

An opt-in check exercises the **actual compiler binary restored by a DEE project**, rather than the
unit tests' compiler fixture:

```powershell
dotnet run --project tools/DeeCompilerCheck -c Release -- `
  C:\workspaces\Micron\Cmf.Custom.Data\Cmf.Custom.Data.Packages\DEEs\Cmf.Custom.Micron.Actions.csproj `
  compiler-check.json
```

Restore that project first. This helper invokes `GenerateFullActionCode`, `GetReferences` and
`CompileSources`, resolving returned DLL names to the runtime and restored package files instead
of a deployed BusinessTier directory. It verifies missing assembly/import failures, the corrected
reference, independent inputs, and the empty no-op. It writes compilation results and exits nonzero
if an expectation fails. It creates artifacts under the system temporary directory and does not
execute any compiled DEE. This verifies generation and compilation; it does not exercise deployment,
execution, debug mode or host-specific `alsoInclude` configuration. Normal unit tests remain
independent of Product packages and do not execute product assemblies.

## Added deployment content

Pass **both** lists when scanning a PR:

```bash
cmf-codereview scan --repository-root . \
  --changed-files-list changed-files.txt \
  --added-files-list added-files.txt \
  --output findings.json
```

Both files contain one repository-relative (or absolute in-repository) path per line. The added
list must describe added **or renamed** destinations, not all modified files. An empty list is valid.
Without the added list, deployment version validation is skipped; the reviewer warns when a PR
changed-file list was provided without it. It never guesses that historical edits are new files.

`DeploymentVersionMismatch` (High) compares the new file's folder version with the owning Data
package's `cmfpackage.json` version from the checked-out PR head. Content is classified from
`contentToPack`: `MasterData`, `EntityTypes`, `ProcessRulesPre`, and `ProcessRulesPost`. This supports
Before/After and EntityTypes/EntityTypesAttributes folders. Unversioned content such as Services
and untouched historical folders are excluded. An unreadable manifest or missing version produces
`DeploymentVersionUnresolved` (Medium).

For a package version `2.61.0`, a new `MasterData/2.60.0/data.json` or
`DEEs/ProcessRules/2.60.0/After/Rule.cs` is flagged with the expected destination under `2.61.0`.
The metadata finding is placed at line 1 of the new file. This repository-level check uses the
explicit added-file list, independently of the C# `--roots` selection, so MasterData-only PRs work.

All new diagnostics participate in rule enable/severity configuration, suppression/baselines where
applicable, `--fail-on`, and the existing JSON, HTML, Markdown and SARIF outputs.

## Changes to the Bash PR-Changes task

The pipeline YAML in Micron is intentionally not changed here. Apply these edits to the Bash task
once `CmfCodeReviewVersion` selects a published version containing this feature. Keep findings in
JSON for the existing inline-comment consumer. Restore/build must precede reference validation;
the reviewer does not perform restores or acquire feed credentials.

**1. Add a path and generate the new-file list beside the existing changed-file list:**

```bash
addedFilesPath="$(Agent.TempDirectory)/codereview-added-files.txt"
git -c core.quotepath=off diff --name-only --diff-filter=AR --find-renames \
    "origin/${targetBranch}...HEAD" > "$addedFilesPath"
```

Use the same merge-base range for both lists. Rename detection ensures that version-folder moves
are checked using their destination path. `--added-files-list` is a separate input so older release
files modified by the PR are not incorrectly treated as new content.

**2. Add to `scanArgs` before invoking npx:**

```bash
scanArgs+=(--added-files-list "$addedFilesPath")
```

**3. After the changed-hunk loop, add a line-1 anchor for added/renamed files:**

```bash
while IFS= read -r file || [[ -n "$file" ]]; do
    if [[ -n "$file" && -s "$(Build.SourcesDirectory)/$file" ]]; then
        printf '%s\t1\t1\n' "$file" >> "$changedLinesPath"
    fi
done < "$addedFilesPath"
```

This keeps the TSV format of the supplied Bash task. A pure rename can have no changed-line hunks;
without this anchor, a folder-version finding can fail the gate yet be omitted by the PR comment
step's changed-line filter. Existing overlapping ranges for added files are harmless.

**4. Fix the findings counter's zero-result case under `set -euo pipefail`:**

```bash
findingsCount="$(grep -c '^[[:space:]]*"RelativePath"[[:space:]]*:' "$findingsPath" || true)"
echo "Findings count: $findingsCount"
```

The original `grep -o ... | wc -l` returns nonzero when no finding matches. That can fail an otherwise
successful clean scan before output variables are exposed. The revised counter tolerates zero
findings while preserving the reviewer's exit code and JSON output.