using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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
            services.PostConfigure<McpServerOptions>(options =>
            {
                var toolCollection = options.ToolCollection
                    ?? throw new InvalidOperationException("The MCP tool collection is not configured.");
                foreach (var name in new[] { "sim_write", "sim_wait" })
                {
                    var tool = toolCollection[name]
                        ?? throw new InvalidOperationException($"The {name} tool is not registered.");
                    var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())?.AsObject()
                        ?? throw new InvalidOperationException($"The {name} input schema must be an object.");
                    var properties = schema["properties"]?.AsObject()
                        ?? throw new InvalidOperationException($"The {name} input schema has no properties.");
                    if (!properties.ContainsKey("value"))
                        throw new InvalidOperationException($"The {name} input schema has no value property.");

                    properties["value"] = new JsonObject
                    {
                        ["description"] = "A typed PLC scalar: boolean, number, or string.",
                        ["anyOf"] = new JsonArray(
                            new JsonObject { ["type"] = "boolean" },
                            new JsonObject { ["type"] = "number" },
                            new JsonObject { ["type"] = "string" })
                    };
                    tool.ProtocolTool.InputSchema = JsonDocument.Parse(schema.ToJsonString()).RootElement.Clone();
                }
            });
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
