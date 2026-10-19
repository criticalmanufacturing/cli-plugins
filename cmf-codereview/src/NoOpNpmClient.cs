using Cmf.CLI.Core;
using Cmf.CLI.Core.Objects;

namespace Cmf.Cli.Plugin.CodeReview;

/// <summary>
/// cmf-codereview never checks for updates or searches npm registries for plugins -- unlike
/// cmf-dev/cmf-pipeline, it has no need for a real npm client, so this avoids depending on the
/// common project's VerdaccioService (and its much heavier transitive dependencies) just to
/// satisfy StartupModule.Configure's required npmClient parameter.
/// </summary>
public class NoOpNpmClient : INPMClient
{
    public Task<string> GetLatestVersion(bool preRelease = false) => Task.FromResult<string>(null!);

    public IPackage[] FindPlugins(Uri[] registries) => Array.Empty<IPackage>();
}
