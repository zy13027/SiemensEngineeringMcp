using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

namespace SiemensEngineeringMcp;

internal static class Program
{
    private const string VendorAssemblyName = "Siemens.Simatic.Simulation.Runtime.Api.x64";
    private static readonly Version ApiVersion = new(9, 0, 0, 0);

    private static int Main()
    {
        string? loadedPath = null;
        ISimulationSession? session = null;
        try
        {
            return WorkerProtocol.Run((operation, arguments, profile) =>
            {
                var path = ReadApiPath(profile);
                if (operation == "sim_probe")
                {
                    var identity = InspectApi(path);
                    return new
                    {
                        apiPath = path,
                        apiVersion = identity.Version!.ToString(),
                        apiFileVersion = FileVersionInfo.GetVersionInfo(path).FileVersion,
                        runtimeManagerContacted = false
                    };
                }

                var configuredInstance = RequiredString(profile, "simInstance");
                if (loadedPath is null)
                {
                    InspectApi(path);
                    AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                    loadedPath = path;
                }
                else if (!string.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The PLCSIM API path changed. Restart the simulation worker.");
                }

                session ??= CreateSession(configuredInstance);
                return session.Dispatch(operation, arguments, profile, configuredInstance);
            });
        }
        finally
        {
            session?.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ISimulationSession CreateSession(string instanceName) => new SimulationSession(instanceName);

    private static string ReadApiPath(JsonElement profile)
    {
        var configured = RequiredString(profile, "simApiPath");
        if (!Path.IsPathFullyQualified(configured) || configured.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidOperationException("simApiPath must be an absolute local path to the installed V9 x64 API.");

        return Path.GetFullPath(configured);
    }

    private static AssemblyName InspectApi(string path)
    {
        if (!Environment.Is64BitProcess)
            throw new InvalidOperationException("The PLCSIM worker requires an x64 process.");

        if (!File.Exists(path))
            throw new FileNotFoundException($"PLCSIM Advanced V9 x64 API was not found at '{path}'.");

        var identity = AssemblyName.GetAssemblyName(path);
        if (identity.Name != VendorAssemblyName || identity.Version != ApiVersion)
            throw new InvalidOperationException(
                $"Expected {VendorAssemblyName} {ApiVersion} at '{path}', found {identity.FullName}.");

        return identity;
    }

    internal static string RequiredString(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException($"'{property}' must be a non-empty string.");

        return value.GetString()!;
    }
}

internal interface ISimulationSession : IDisposable
{
    object Dispatch(string operation, JsonElement arguments, JsonElement profile, string configuredInstance);
}
