using Cmf.CLI.Core;
using Cmf.CLI.Core.Enums;
using Cmf.CLI.Core.Objects;
using Cmf.CLI.Utilities;
using Cmf.Cli.Plugin.CodeReview;
using Microsoft.Extensions.DependencyInjection;
using System.CommandLine.Parsing;
using ExecutionContext = Cmf.CLI.Core.Objects.ExecutionContext;

try
{
    // Telemetry is opt-in (cmf_codereview_enable_telemetry=1). It was force-enabled here once, but
    // the CLI core's OTLP exporter uses a SYNCHRONOUS export processor and StartupModule blocks on
    // its initialization -- when the telemetry host is unreachable (offline, restricted network),
    // every invocation, even --help, stalled ~11s in connect timeouts while exporting nothing.
    var (rootCommand, parser) = await StartupModule.Configure(
        packageId: "@criticalmanufacturing/codereview",
        envVarPrefix: "cmf_codereview",
        description: "cmf-codereview — CM Framework code review scanner for DEEs and Orchestration code.",
        args: args,
        npmClient: new NoOpNpmClient(),
        registerExtraServices: collection => { });

    using var activity = ExecutionContext.ServiceProvider.GetService<ITelemetryService>()!.StartActivity("Main");

    var result = await parser.InvokeAsync(args);
    activity?.SetTag("execution.success", true);
    return result;
}
catch (CliException e)
{
    Log.Error(e.Message);
    Log.Debug(e.StackTrace);
    return (int)e.ErrorCode;
}
catch (Exception e)
{
    Log.Debug("Caught exception at program.");
    Log.Exception(e);
    ExecutionContext.ServiceProvider.GetService<ITelemetryService>()!.LogException(e);
    return (int)ErrorCode.Default;
}
