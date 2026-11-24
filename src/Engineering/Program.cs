using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text.Json;

namespace SiemensEngineeringMcp
{
    internal static class Program
    {
#if TIA_21_1
        internal const string BuildVersion = "21.1";
#else
        internal const string BuildVersion = "21.0";
#endif
        private static readonly string[] RequiredAssemblies =
        {
            "Siemens.Engineering.Base",
            "Siemens.Engineering.Step7",
            "Siemens.Engineering.WinCCUnified"
        };
        private static string? _apiDirectory;
        private static IDisposable? _session;
        private static object[]? _assemblyEvidence;

        private static void Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveEngineeringAssembly;
            try
            {
                WorkerProtocol.Run(Dispatch);
            }
            finally
            {
                _session?.Dispose();
            }
        }

        private static object Dispatch(string operation, JsonElement arguments, JsonElement profile)
        {
            var requested = profile.GetProperty("tiaVersion").GetString();
            if (requested != BuildVersion)
                throw new InvalidOperationException($"This TIA worker targets {BuildVersion}; profile requests {requested}.");

            var directory = RequireAbsolutePath(profile.GetProperty("tiaApiDirectory").GetString(), "tiaApiDirectory");
            var installation = GetInstallation(directory);
            if (_apiDirectory != null && !SamePath(_apiDirectory, directory))
                throw new InvalidOperationException("The TIA SDK directory changed. Restart the engineering worker.");

            if (_assemblyEvidence == null)
                _assemblyEvidence = LoadSdk(directory);

            if (operation == "engineering_probe")
            {
                using (var identity = WindowsIdentity.GetCurrent())
                {
                    var opennessGroupMember = new WindowsPrincipal(identity)
                        .IsInRole(Environment.MachineName + @"\Siemens TIA Openness");
                    return new { tiaVersion = BuildVersion, installation, assemblies = _assemblyEvidence, opennessGroupMember };
                }
            }

            return DispatchEngineering(operation, arguments, profile, installation);
        }

        private static object[] LoadSdk(string directory)
        {
            // Inspect all headers before binding the first Siemens type.
            foreach (var name in RequiredAssemblies)
            {
                var path = AssemblyPath(directory, name);
                var version = AssemblyName.GetAssemblyName(path).Version;
                if (version != new Version(BuildVersion + ".0.0") ||
                    FileVersionInfo.GetVersionInfo(path).FileMajorPart != ReleaseBuild)
                    throw new InvalidOperationException($"Wrong API build for '{path}': got assembly {version}, expected TIA {BuildVersion}.");
            }

            _apiDirectory = directory;
            var assemblies = new List<object>();
            foreach (var name in RequiredAssemblies)
            {
                var expected = AssemblyPath(directory, name);
                var loaded = Assembly.LoadFrom(expected);
                if (!SamePath(loaded.Location, expected))
                    throw new InvalidOperationException($"Siemens assembly '{name}' loaded from '{loaded.Location}', expected '{expected}'.");

                assemblies.Add(new
                {
                    name,
                    path = loaded.Location,
                    version = loaded.GetName().Version.ToString(),
                    fileVersion = FileVersionInfo.GetVersionInfo(loaded.Location).FileVersion
                });
            }
            return assemblies.ToArray();
        }

        private static int ReleaseBuild => BuildVersion == "21.0" ? 2100 : 2101;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object DispatchEngineering(string operation, JsonElement arguments, JsonElement profile, string installation)
        {
            var session = (EngineeringSession?)_session;
            if (session == null)
                _session = session = new EngineeringSession(profile, installation);

            return session.Dispatch(operation, arguments, profile);
        }

        private static Assembly? ResolveEngineeringAssembly(object sender, ResolveEventArgs args)
        {
            var identity = new AssemblyName(args.Name);
            var name = identity.Name;
            var directory = _apiDirectory;
            if (directory == null || name == null || !name.StartsWith("Siemens.Engineering.", StringComparison.Ordinal))
                return null;

            var path = AssemblyPath(directory, name);
            if (!File.Exists(path))
                return null;

            var candidate = AssemblyName.GetAssemblyName(path);
            if (!String.Equals(candidate.FullName, identity.FullName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"SDK assembly '{path}' has identity '{candidate.FullName}', requested '{identity.FullName}'.");

            var loaded = Assembly.LoadFrom(path);
            if (!SamePath(loaded.Location, path))
                throw new InvalidOperationException($"SDK assembly '{name}' loaded from an unexpected location '{loaded.Location}'.");

            return loaded;
        }

        private static string AssemblyPath(string directory, string name) => Path.Combine(directory, name + ".dll");

        private static string GetInstallation(string apiDirectory)
        {
            var api = new DirectoryInfo(apiDirectory);
            var version = api.Parent;
            var publicApi = version?.Parent;
            var installation = publicApi?.Parent;
            var executable = Path.Combine(installation?.FullName ?? "", "Bin", "Siemens.Automation.Portal.exe");
            var versionDirectory = BuildVersion == "21.0" ? "V21" : "V21.1";
            if (installation == null || api.Name != "net48" || version!.Name != versionDirectory ||
                publicApi!.Name != "PublicAPI" ||
                !File.Exists(executable) || FileVersionInfo.GetVersionInfo(executable).FileMajorPart != ReleaseBuild)
                throw new InvalidOperationException($"'{apiDirectory}' must be the {versionDirectory} PublicAPI net48 directory inside its TIA installation.");

            return installation.FullName;
        }

        internal static string RequireAbsolutePath(string? path, string property)
        {
            if (String.IsNullOrWhiteSpace(path) || path!.Length < 3 || !Char.IsLetter(path[0]) ||
                path[1] != ':' || path[2] != '\\')
                throw new InvalidOperationException($"'{property}' must be an absolute Windows drive path.");

            return Path.GetFullPath(path);
        }

        internal static bool SamePath(string left, string right) =>
            String.Equals(Path.GetFullPath(left).TrimEnd('\\'), Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    }
}
