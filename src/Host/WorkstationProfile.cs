using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiemensEngineeringMcp;

internal sealed class WorkstationProfile
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public required string TiaVersion { get; set; }
    public required string TiaApiDirectory { get; init; }
    public required string ProjectPath { get; init; }
    public int? PortalProcessId { get; init; }
    public required string PlcPath { get; init; }
    public required string HmiPath { get; init; }
    public required string FileRoot { get; init; }
    public required string SimApiPath { get; init; }
    public required string SimInstance { get; init; }
    public bool AllowEngineeringWrite { get; init; }
    public bool AllowSimulationWrite { get; init; }
    public string[] WritableTags { get; init; } = [];
    public string[] RuntimeTagPrefixes { get; init; } = [];

    internal static WorkstationProfile Load(string[] args)
    {
        if (args.Length is not (2 or 4) || args[0] != "--profile" ||
            (args.Length == 4 && args[2] != "--tia-version"))
            throw new ArgumentException("Usage: SiemensEngineeringMcp --profile <absolute JSON path> [--tia-version 21.0|21.1]");

        var profile = JsonSerializer.Deserialize<WorkstationProfile>(File.ReadAllText(LocalPath(args[1])), Json)
            ?? throw new ArgumentException("The workstation profile is empty.");

        if (args.Length == 4)
            profile.TiaVersion = args[3];

        if (profile.TiaVersion is not ("21.0" or "21.1"))
            throw new ArgumentException("tiaVersion must be 21.0 or 21.1.");

        foreach (var path in new[] { profile.TiaApiDirectory, profile.ProjectPath, profile.SimApiPath, profile.FileRoot })
            LocalPath(path);

        foreach (var target in new[] { profile.PlcPath, profile.HmiPath, profile.SimInstance })
            if (string.IsNullOrWhiteSpace(target))
                throw new ArgumentException("plcPath, hmiPath and simInstance are required.");

        if (profile.PortalProcessId is <= 0 || profile.WritableTags == null || profile.RuntimeTagPrefixes == null ||
            profile.RuntimeTagPrefixes.Any(string.IsNullOrWhiteSpace) || profile.WritableTags.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid PID, writableTags or runtimeTagPrefixes.");

        if (profile.AllowEngineeringWrite && profile.PortalProcessId != null)
            throw new ArgumentException("Offline writes require a private Portal session. Clear portalProcessId in the write profile.");

        return profile;
    }

    internal static string LocalPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            path.Length < 3 || path[1] != ':' || path[2] is not ('\\' or '/') || path[2..].Contains(':'))
            throw new ArgumentException("Use a drive-qualified local path.");

        return Path.GetFullPath(path);
    }

    internal static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
