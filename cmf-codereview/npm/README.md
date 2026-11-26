# Critical Manufacturing CLI - codereview plugin

## Installing

Follow the [RepoFlow installation guide](https://github.com/criticalmanufacturing/cli-plugins/blob/main/README.md#install-code-reviewer-from-repoflow)
for Node.js/npm prerequisites, registry setup, optional authentication, and
selecting a published version.

```sh
npm install --global @criticalmanufacturing/codereview --registry https://cm-collaborationhub.io/api/npm/2305290000030000002/dev/
```

Verify the installation:

```sh
cmf-codereview --version
```

The plugin is available directly as `cmf-codereview`. If the
[CM CLI](https://github.com/criticalmanufacturing/cli) is installed, it is also
available as `cmf codereview`.

## Commands

See [docs/commands.md](https://github.com/criticalmanufacturing/cli-plugins/blob/main/cmf-codereview/docs/commands.md) in the source repository for the full command reference
(`scan`, `report`, `review`).

Quick start:

```powershell
cmf codereview review --repository-root <path>
```
