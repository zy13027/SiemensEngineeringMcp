using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SiemensEngineeringMcp;

try
{
    var profile = WorkstationProfile.Load(args);
    using var server = Host.CreateDefaultBuilder()
        .ConfigureLogging(logging => logging.ClearProviders().AddConsole(options =>
            options.LogToStandardErrorThreshold = LogLevel.Trace))
        .ConfigureServices(services =>
        {
            services.AddSingleton(profile);
            services.AddSingleton<EngineeringService>();
            services.AddMcpServer(options =>
                {
                    options.ServerInfo = new() { Name = "Siemens Engineering MCP", Version = "0.1.0" };
                })
                .WithStdioServerTransport()
                .WithTools<WorkstationTools>()
                .WithTools<PlcTools>()
                .WithTools<UnifiedTools>()
                .WithTools<SimulationTools>();
        })
        .Build();
    await server.RunAsync();
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
