using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace SiemensEngineeringMcp;

[McpServerToolType]
internal sealed class WorkstationTools
{
    [McpServerTool(Name = "doctor", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Report the configured targets and installed Siemens components.")]
    public static Task<CallToolResult> Doctor(EngineeringService service) =>
        service.Doctor();

    [McpServerTool(Name = "engineering_discover", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Discover the configured project and its devices.")]
    public static Task<CallToolResult> Discover(EngineeringService service) =>
        service.Read("engineering_discover");

    [McpServerTool(Name = "operation_status", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Check a submitted write by UUID. Wait for success before submitting the next step.")]
    public static Task<CallToolResult> Status(EngineeringService service, Guid requestId) =>
        Task.FromResult(service.Status(requestId));
}

[McpServerToolType]
internal sealed class PlcTools
{
    [McpServerTool(Name = "plc_blocks", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List blocks in the configured PLC.")]
    public static Task<CallToolResult> Blocks(EngineeringService service) =>
        service.Read("plc_blocks");

    [McpServerTool(Name = "plc_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read a block's SCL source and declaration. Use the path returned by plc_blocks.")]
    public static Task<CallToolResult> Read(EngineeringService service, string blockPath) =>
        service.Read("plc_read", new { blockPath });

    [McpServerTool(Name = "plc_preview", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Preview replacing the SCL implementation of an existing block using a complete SIMATIC source document.")]
    public static Task<CallToolResult> Preview(EngineeringService service, string blockPath, string source) =>
        service.Read("plc_preview", new { blockPath, source });

    [McpServerTool(Name = "plc_apply", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Apply the PLC preview in the private TIA project. Poll operation_status using requestId.")]
    public static CallToolResult Apply(EngineeringService service, Guid requestId, string previewId) =>
        service.Start("plc_apply", requestId, new { previewId });

    [McpServerTool(Name = "plc_compile", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Compile the configured PLC and return diagnostics. Poll operation_status using requestId.")]
    public static CallToolResult Compile(EngineeringService service, Guid requestId) =>
        service.Start("plc_compile", requestId);

    [McpServerTool(Name = "engineering_save", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Save the private project after checking both compile reports. Poll operation_status using requestId.")]
    public static CallToolResult Save(EngineeringService service, Guid requestId) =>
        service.Start("engineering_save", requestId);

    [McpServerTool(Name = "engineering_reopen", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Reopen the saved private project for readback. Save completed changes first. Poll operation_status using requestId.")]
    public static CallToolResult Reopen(EngineeringService service, Guid requestId) =>
        service.Start("engineering_reopen", requestId);
}

[McpServerToolType]
internal sealed class UnifiedTools
{
    [McpServerTool(Name = "hmi_discover", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("List connections, tag tables, screens and items in the configured Unified device.")]
    public static Task<CallToolResult> Discover(EngineeringService service) =>
        service.Read("hmi_discover");

    [McpServerTool(Name = "hmi_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read a screen item property binding and the named HMI tag's engineering metadata.")]
    public static Task<CallToolResult> Read(EngineeringService service, string screenPath, string itemName, string property, string tagName) =>
        service.Read("hmi_read", new { screenPath, itemName, property, tagName });

    [McpServerTool(Name = "hmi_preview", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Preview an external tag and supported HMI I/O field binding. Provide tagTablePath when the target table is ambiguous.")]
    public static Task<CallToolResult> Preview(EngineeringService service, string screenPath, string itemName, string property, string tagName, string plcTag, string connectionName, string? tagTablePath = null) =>
        service.Read("hmi_preview", new { screenPath, itemName, property, tagName, plcTag, connectionName, tagTablePath });

    [McpServerTool(Name = "hmi_apply", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Apply a Unified engineering preview in the private project. Poll operation_status using requestId.")]
    public static CallToolResult Apply(EngineeringService service, Guid requestId, string previewId) =>
        service.Start("hmi_apply", requestId, new { previewId });

    [McpServerTool(Name = "hmi_compile", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Compile the configured Unified device and return diagnostics. Poll operation_status using requestId.")]
    public static CallToolResult Compile(EngineeringService service, Guid requestId) =>
        service.Start("hmi_compile", requestId);

    [McpServerTool(Name = "runtime_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read a permitted PC Runtime tag via Open Pipe with its quality and observation time.")]
    public static Task<CallToolResult> ReadRuntime(EngineeringService service, string tagName) =>
        service.ReadRuntime(tagName);
}

[McpServerToolType]
internal sealed class SimulationTools
{
    [McpServerTool(Name = "sim_instances", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Check registration of the configured virtual PLC instance.")]
    public static Task<CallToolResult> Instances(EngineeringService service) =>
        service.Read("sim_instances");

    [McpServerTool(Name = "sim_state", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read the configured virtual PLC's identity and operating state.")]
    public static Task<CallToolResult> State(EngineeringService service) =>
        service.Read("sim_state");

    [McpServerTool(Name = "sim_refresh", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Refresh symbolic tags after downloading to the configured virtual PLC.")]
    public static Task<CallToolResult> Refresh(EngineeringService service) =>
        service.Read("sim_refresh");

    [McpServerTool(Name = "sim_read", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Read a typed symbolic tag on the configured virtual PLC.")]
    public static Task<CallToolResult> Read(EngineeringService service, string tag) =>
        service.Read("sim_read", new { tag });

    [McpServerTool(Name = "sim_write", ReadOnly = false, Destructive = true, OpenWorld = false)]
    [Description("Write one allowlisted symbolic test input on the configured virtual PLC. Poll operation_status using requestId.")]
    public static CallToolResult Write(EngineeringService service, Guid requestId, string tag, JsonElement value) =>
        service.Start("sim_write", requestId, new { tag, value });

    [McpServerTool(Name = "sim_wait", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("Wait for the specified virtual PLC tag to equal value.")]
    public static Task<CallToolResult> Wait(EngineeringService service, string tag, JsonElement value, int timeoutMs) =>
        service.Read("sim_wait", new { tag, value, timeoutMs });
}
