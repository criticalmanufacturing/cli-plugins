# Critical Manufacturing CLI plugins

Plugins for the [Critical Manufacturing CLI](https://github.com/criticalmanufacturing/cli).

| Plugin | Description |
| --- | --- |
| [cmf-codereview](cmf-codereview/README.md) | Static analysis of CM customization C# code, with JSON findings and Markdown or HTML reports. |

## Install Code Reviewer from RepoFlow

The Code Reviewer npm package is `@criticalmanufacturing/codereview`, hosted in
the Collaboration Hub dev registry on RepoFlow.

### 1. Install Node.js and npm

Install the LTS version of [Node.js](https://nodejs.org/), which includes npm,
and confirm both commands are available:

```sh
node --version
npm --version
```

The package includes self-contained executables for Windows x64, Linux x64, and
Intel macOS x64. A .NET SDK is only needed when building from source.

### 2. Configure the registry (optional)

RepoFlow's setup command sets the default npm registry for your user:

```sh
npm config set registry https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/
```

This applies to all packages without a separate scope registry. You can skip
this step because the commands below specify `--registry` explicitly.

### 3. Authenticate (if required)

If the registry requires authentication and you have not already configured
credentials, run RepoFlow's login command:

```sh
npm login --registry https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/ --auth-type legacy
```

Enter the credentials provided for this RepoFlow registry when prompted.

### 4. Download and install Code Reviewer

Install the package globally to make `cmf-codereview` available on your PATH.
You can run this command from any directory:

```sh
npm install --global @criticalmanufacturing/codereview --registry https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/
```

This selects the registry's `latest` distribution tag. To select a specific
published version, list the available versions and install one explicitly:

```sh
npm view @criticalmanufacturing/codereview versions --json --registry https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/
npm install --global "@criticalmanufacturing/codereview@<version>" --registry https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/
```

Replace `<version>` with a version returned by `npm view`. A release published
only under `next` must be selected by its version or with
`@criticalmanufacturing/codereview@next`.

### 5. Verify the installation

```sh
cmf-codereview --version
cmf-codereview --help
```

To scan a customization repository and generate a report, replace the example
path with your repository's path:

```sh
cmf-codereview review --repository-root /path/to/customization
```

If the [CM CLI](https://github.com/criticalmanufacturing/cli) is installed, you
can also run `cmf codereview`. See the [command reference](cmf-codereview/docs/commands.md)
for scan and report options.

## Repository layout

Each plugin has its own source, tests, documentation, and npm package. Run the
commands below from `cmf-codereview/` unless otherwise noted. `NuGet.Config` at the
repository root supplies the nuget.org package feed. `.versionrc.json` supplies shared
changelog formatting and GitHub links. Build and release commands use dotnet and npm.

## Development prerequisites

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
