# Releasing cmf-codereview

The `Release-CodeReview` GitHub Actions workflow builds Windows, Linux and macOS x64 bundles into one npm archive. Select `next` to build a candidate, then select `latest` to promote the same published version after testing it in a consuming customization project. A channel is an npm distribution tag, separate from the package version: for example, publish `1.0.1` under `next`, test `1.0.1`, then move `latest` to `1.0.1` without rebuilding.

## Current state: publication is blocked

Leave repository variable `CODE_REVIEW_PUBLISH_ENABLED` unset or `false`. The public RepoFlow endpoint previously rejected the approximately 129 MB all-platform archive with HTTP 413. Both publishing jobs are blocked until the upload route is resolved; `latest` requests fail before approval. A `next` request still builds, tests and saves the tgz as a GitHub artifact. Pull requests changing release automation also exercise this build without registry credentials.

Do not assume the raw `/api/upload/...` endpoint supports `npm publish` or npm tags. This workflow requires an npm-compatible registry endpoint that accepts the complete publish request. The registry must also be reachable from the publishing runner. Authentication and sufficient upload capacity must both be verified before enabling publication. No upload has been attempted by this implementation.

## Repository setup

An administrator must configure the following after reviewing and merging the workflow into `main`:

| Setting | Purpose |
| --- | --- |
| Environment `repoflow-next` | Holds the publishing credential for candidates. Restrict deployment branches to `main`. |
| Environment `repoflow-latest` | Configure **required reviewers**, prevent self-review if supported, and restrict deployment branches to `main`. Holds the promotion credential. |
| Environment secret `REPOFLOW_NPM_TOKEN` | An npm authentication token accepted by RepoFlow, with package publish/tag permissions. Set in each environment; never commit it. |
| Repository variable `REPOFLOW_NPM_REGISTRY` | Optional working npm registry URL. Defaults to `https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/`. |
| Repository variable `CODE_REVIEW_PUBLISH_RUNNER_JSON` | Optional JSON runner label/string, e.g. `["self-hosted","linux","repoflow"]` for a network-restricted endpoint. Defaults to `"ubuntu-24.04"`. Only the publish/promotion jobs use this runner. |
| Repository variable `CODE_REVIEW_PUBLISH_ENABLED` | Set to literal `true` **only after** the upload problem and authentication are resolved. Keep disabled for now. |

The workflow checks that `repoflow-latest` has required reviewers before entering that environment. Missing protection or inability to inspect it fails the request. `latest` runs must use `main`. Environments and secrets are repository configuration; adding their names to YAML does not configure reviewers. GitHub must allow the referenced Actions versions and artifact storage.

## Candidate and integration test

1. Open **Actions → Release-CodeReview → Run workflow**, select `main`, channel `next`, and an unused exact version, such as `1.0.1`. No automatic version bump or source commit is made: the workflow stamps the npm manifest and .NET project in its build workspace. It rejects ranges, `v` prefixes and build metadata.
2. The workflow runs .NET tests, builds all three platform bundles, produces a tgz and SHA-512 manifest, and installs/scans with that tgz on Windows, Linux and Intel macOS. With publishing disabled, download the `codereview-<version>` artifact to inspect it. Once enabled, only a successful run publishes it with `--tag next`; an existing package version is never overwritten.
3. Create a separate integration branch in a consuming customization repository, for example `test/codereview-integration`. Configure its PR validation pipeline to install the **exact version** from the successful candidate release. This avoids another candidate moving `next` during your test.
4. Open a PR from the test branch and run the consuming project's validation pipeline. Include a representative C# or process-rule change so the scan and inline-comment path are exercised. Check restored dependency resolution, expected findings, comment placement and the full pipeline result. Keep the candidate-version override scoped to the integration branch.
5. Record the installed version and registry SHA-512 integrity in the integration build's logs or artifacts. Compare them with the GitHub candidate artifact's `manifest.json`. Save the successful integration build URL. A tag resolving successfully alone is not an integration test.

The consuming pipeline needs npm authentication for the registry's metadata and package requests, plus restored project dependencies before scanning. Resolve any tag once and pass the resulting exact version to `npx`. Keep the normal release configuration on `latest`. Until a candidate is actually published and registry access works, the full integration run cannot be completed.

## Promote after testing

1. Run `Release-CodeReview` from `main`, channel `latest`, with the tested exact version and `integration_build_url`, the successful Azure DevOps integration build URL (`https://dev.azure.com/example-org/Project/_build/results?buildId=12345`).
2. The `repoflow-latest` reviewers inspect that build, confirm it belongs to the intended consuming project, succeeded and tested that exact version/integrity, and approve the deployment. The URL must be an HTTPS Azure DevOps organization/project build-results link with a numeric build ID; it is not restricted to a particular organization or project. **The workflow does not query Azure or automatically verify build success**. This is a manual approval gate.
3. The approved job requires that version to still be RepoFlow's current `next` candidate, then runs `npm dist-tag add @criticalmanufacturing/codereview@<version> latest` and verifies the resulting tag. It downloads no replacement binaries and performs no rebuild or republish. Consuming pipelines using `latest` receive the promoted version on subsequent installs.

Avoid advancing `next` while a candidate is undergoing integration testing. If it moves, the promotion fails; retest the current candidate instead of bypassing the check. Workflow runs are serialized, but approval is still responsible for choosing the intended version. If publishing succeeded but a later registry verification failed, inspect the registry before retrying: do not reuse the version for different bytes.

## Validation and assumptions

Release helper tests cover disabled publishing, version validation, approval configuration, candidate matching, archive completeness and integrity. The release workflow performs native install/scan smoke tests against the actual tgz on all three operating systems. Native GitHub jobs must complete before a publishing job can run.

The integration pipeline belongs to the consuming repository and is configured separately from this release workflow. It needs a compatible .NET/Node setup, package authentication and sufficient Git history for PR diff analysis. Private feed access, restored dependency resolution and the full PR integration flow must be verified in a real consuming-project build.

References used: the repository's Azure pipeline schema, best practices, tasks and templates guides; Bash defensive patterns; [GitHub environments and approvals](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments), [environment API](https://docs.github.com/en/rest/deployments/environments#get-an-environment), [runner images](https://github.com/actions/runner-images), [artifact upload](https://github.com/actions/upload-artifact), [artifact download](https://github.com/actions/download-artifact), and [npm distribution tags](https://docs.npmjs.com/cli/v11/commands/npm-dist-tag/).

References skipped or missing: automated Azure build verification (manual approval was selected), unrelated deployment examples, and the unavailable Azure validator. Impact: no Azure PAT is needed in GitHub; reviewers must verify the supplied build evidence, and a real Azure run remains necessary before promotion.
