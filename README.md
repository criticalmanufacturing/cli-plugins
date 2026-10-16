# Critical Manufacturing CLI plugins

Plugins for the [Critical Manufacturing CLI](https://github.com/criticalmanufacturing/cli).

| Plugin | Description |
| --- | --- |
| [cmf-codereview](cmf-codereview/README.md) | Static analysis of CM customization C# code, with JSON findings and Markdown or HTML reports. |

## Repository layout

Each plugin has its own source, tests, documentation, and npm package. Run the
commands below from `cmf-codereview/` unless otherwise noted. `NuGet.Config` at the
repository root supplies the nuget.org package feed. `.versionrc.json` supplies shared
changelog formatting and GitHub links. Build and release commands use dotnet and npm.

## Prerequisites

- A .NET 8 SDK compatible with `cmf-codereview/global.json`.
- Node.js and npm for packaging and release scripts.
- Access to nuget.org to restore dependencies.
- Git history and the plugin's release tags when generating release notes.

## Build and test

```sh
cd cmf-codereview
dotnet restore cmf-codereview.sln
dotnet build cmf-codereview.sln --no-restore
dotnet test cmf-codereview.sln --no-build
```

To run the scanner from source:

```sh
dotnet run --project src/cmf-codereview.csproj -- review --repository-root /path/to/customization
```

## Package

Install the builder dependencies, build the self-contained executables for
Windows, Linux, and macOS x64, then create the npm archive:

```sh
npm install
npm run build:bundle
npm run pack
```

The executables are written to `cmf-codereview/npm/dist/`. The archive is written
to `cmf-codereview/npm/packs/`. These are generated outputs and are not committed.
The package name remains `@criticalmanufacturing/codereview`.

## Prepare a release

From `cmf-codereview/`, choose one of:

```sh
npm run bump:patch
npm run bump:feature
npm run bump:breaking
```

Each command updates the npm package version and C# project version, then writes
`cmf-codereview/CHANGES.md` using the repository root `.versionrc.json`. The
changelog command considers changes under this plugin and uses the
`cmf-codereview/` tag prefix. The changelog configuration does not create a release
commit or Git tag. Review the changes, commit them, and manage the release tag
separately. Development-version scripts that use shell substitution require a
POSIX shell.

## Publish

Publishing currently targets the Collaboration Hub dev npm registry:

```text
https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/
```

Configure credentials for this registry in your user npm configuration or CI
environment before publishing. The previous Gradle authentication task is not
part of this repository. Environment variables used by that task do not configure
npm authentication by themselves.

After testing and building the bundle, select a distribution tag:

```sh
npm run publish        # next
npm run publish:live   # latest
```

These commands publish the existing contents of `npm/`; they do not build the
executables or increment the version. The binary bundle must already exist.
