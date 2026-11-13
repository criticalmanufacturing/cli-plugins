# Build a package for manual testing

The `Package-CodeReview` GitHub Actions workflow automatically builds and validates an npm package on every pull request targeting `main` and every push to `main`. It can also be rerun with **Actions > Package-CodeReview > Run workflow**, without any inputs.

## Choose the next version

Edit `version` in **`cmf-codereview/npm/package.json`**. The current target is **`1.1.0`**. Use an exact npm version without a `v` prefix:

```json
"version": "1.1.0"
```

This manifest is the workflow's source for the package version. The workflow reads it and sets the same version in `src/cmf-codereview.csproj` before building, so the executable and npm package agree. There is no workflow version input, automatic version bump, source commit, Git tag or GitHub release. The root `cmf-codereview/package.json` belongs to the build tooling and does not set the distributed plugin's version.

Multiple runs can build the same version while it is being tested. Each artifact belongs to its own workflow run; `manifest.json` records the exact source commit and SHA-512 integrity. Use those values to distinguish builds of the same version. For a PR run, the commit is the GitHub checkout commit, normally the PR test merge commit.

## What must pass

1. Package helper tests validate version handling, executable stamping, archive contents and integrity checks.
2. The .NET solution is restored, built in Release configuration and tested.
3. Self-contained Windows x64, Linux x64 and Intel macOS x64 executables are bundled into one npm `.tgz`.
4. The archive is checked for all three executables, and its version, source commit and SHA-512 integrity are saved in `manifest.json`.
5. Separate native jobs on Windows, Linux and Intel macOS install that exact archive, check the executable version and scan a small fixture containing an expected `LoadInsideLoop` finding.
6. Only after all those jobs pass does **Package ready for manual testing** verify the archive again and upload the final **`codereview-<version>`** artifact.

No approval, publishing environment, registry credential or release switch is needed. Normal NuGet restore and public npm dependency access must be available to the runners. The workflow does not publish to RepoFlow, change `next`/`latest`, or run a consuming project's integration pipeline.

## Download and test

1. Open the PR's **Package-CodeReview** check or select the run under **Actions > Package-CodeReview**.
2. Confirm the workflow completed successfully, including **Package ready for manual testing**. The separate `CI-Build` check should also pass before merging the PR.
3. Download **`codereview-1.1.0`** from the run's **Artifacts** section. Extract the downloaded ZIP to obtain the `.tgz`, `manifest.json` and `pack.json`.
4. Install the extracted `.tgz` in your manual test environment. For example, from a temporary npm project:

   ```bash
   npm install /path/to/criticalmanufacturing-codereview-1.1.0.tgz
   npx --no-install cmf-codereview --version
   npx --no-install cmf-codereview scan --repository-root /path/to/customization --output findings.json
   ```

5. Test the package in a consuming project's PR validation pipeline, including dependency restoration and inline PR comments. Use the same archive and keep the source commit/integrity with your test results.

The final artifact is kept for 30 days. An **`unvalidated-codereview-<version>`** artifact is used to transfer the archive between jobs and is kept for one day. It can exist even if a platform test fails; it is not ready for manual testing. If the workflow fails, fix the failure and use the final artifact from a successful run.

All integration testing, publication and npm tag changes are manual for now. The previously encountered RepoFlow upload limit must be handled as part of any manual publication; this workflow does not exercise or resolve it. Publish the exact tested archive and use a new version after a version has already been published.
