using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.Compiler;
using Siemens.Engineering.HW;

namespace SiemensEngineeringMcp
{
    internal sealed partial class EngineeringSession : IDisposable
    {
        private readonly JsonElement _profile;
        private readonly string _installation;
        private readonly string _projectPath;
        private readonly int? _externalProcessId;
        private TiaPortal? _portal;
        private Project? _project;
        private bool _hasUnsavedChanges;

        public EngineeringSession(JsonElement profile, string installation)
        {
            _profile = profile.Clone();
            _installation = installation;
            _projectPath = Program.RequireAbsolutePath(RequiredString(profile, "projectPath"), "projectPath");
            if (profile.TryGetProperty("portalProcessId", out var processId) && processId.ValueKind != JsonValueKind.Null)
                _externalProcessId = processId.GetInt32();
        }

        internal Project Project => _project ?? OpenProject();

        internal TiaPortal Portal
        {
            get
            {
                _ = Project;
                return _portal!;
            }
        }

        internal object Dispatch(string operation, JsonElement arguments, JsonElement profile)
        {
            if (!String.Equals(profile.GetRawText(), _profile.GetRawText(), StringComparison.Ordinal))
                throw new InvalidOperationException("The workstation profile changed. Restart the engineering worker.");

            switch (operation)
            {
                case "engineering_discover":
                    return Discover();
                case "plc_blocks":
                    return ListBlocks();
                case "plc_read":
                    return PlcRead(RequiredString(arguments, "blockPath"));
                case "plc_preview":
                    return PlcPreview(RequiredString(arguments, "blockPath"), RequiredString(arguments, "source"));
                case "plc_apply":
                    return PlcApply(RequiredString(arguments, "previewId"));
                case "plc_compile":
                    RequireEngineeringWrite();
                    _hasUnsavedChanges = true;
                    return Compile(GetPlc());
                case "engineering_save":
                    RequireEngineeringWrite();
                    Project.Save();
                    _hasUnsavedChanges = false;
                    return new { projectPath = _projectPath, saved = true };
                case "engineering_reopen":
                    return Reopen();
                default:
                    if (operation.StartsWith("hmi_", StringComparison.Ordinal))
                        return DispatchUnified(operation, arguments, profile);
                    throw new InvalidOperationException($"Unknown engineering operation '{operation}'.");
            }
        }

        private object Discover()
        {
            var portals = TiaPortal.GetProcesses().Where(IsSelectedInstallation).Select(ProcessInfo).ToArray();
            var project = Project;
            return new
            {
                tiaVersion = Program.BuildVersion,
                availablePortals = portals,
                attached = ProcessInfo(Portal.GetCurrentProcess()),
                projectPath = _projectPath,
                projectName = project.Name,
                devices = Devices().Select(entry => new { path = entry.Key, name = entry.Value.Name }).ToArray()
            };
        }

        private static object ProcessInfo(TiaPortalProcess process) => new
        {
            processId = process.Id,
            executable = process.Path.FullName,
            projectPath = process.ProjectPath?.FullName,
            mode = process.Mode.ToString()
        };

        private bool IsSelectedInstallation(TiaPortalProcess process) =>
            Program.SamePath(process.Path.FullName, Path.Combine(_installation, "Bin", "Siemens.Automation.Portal.exe"));

        private Project OpenProject()
        {
            if (!File.Exists(_projectPath))
                throw new InvalidOperationException($"Configured TIA project does not exist: '{_projectPath}'.");

            try
            {
                if (_externalProcessId.HasValue)
                {
                    var process = TiaPortal.GetProcesses().SingleOrDefault(candidate => candidate.Id == _externalProcessId.Value);
                    if (process == null || !IsSelectedInstallation(process))
                        throw new InvalidOperationException($"TIA Portal PID {_externalProcessId} does not belong to '{_installation}'.");
                    _portal = process.Attach();
                    _project = _portal.Projects.SingleOrDefault(candidate => Program.SamePath(candidate.Path.FullName, _projectPath))
                        ?? throw new InvalidOperationException($"TIA Portal PID {_externalProcessId} does not have '{_projectPath}' open.");
                }
                else
                {
                    _portal = _portal ?? new TiaPortal(TiaPortalMode.WithoutUserInterface);
                    if (!IsSelectedInstallation(_portal.GetCurrentProcess()))
                        throw new InvalidOperationException($"The Portal process started from a different installation. Expected '{_installation}'.");
                    _project = _portal.Projects.Open(new FileInfo(_projectPath));
                }

                return _project;
            }
            catch
            {
                Disconnect();
                throw;
            }
        }

        private object Reopen()
        {
            if (_externalProcessId.HasValue)
                throw new InvalidOperationException("Cannot reopen an externally owned project. Reopen it in TIA Portal or use a private worker session.");
            if (_hasUnsavedChanges)
                throw new InvalidOperationException("The worker has unsaved changes. Save explicitly before reopening.");

            _project?.Close();
            _project = null;
            _plcPreviews.Clear();
            _hmiPreviews.Clear();
            var project = Project;
            return new { projectPath = _projectPath, projectName = project.Name, reopened = true };
        }

        internal void RequireEngineeringWrite()
        {
            if (!Flag(_profile, "allowEngineeringWrite"))
                throw new InvalidOperationException("Offline engineering changes are disabled by the workstation profile.");
            if (_externalProcessId.HasValue)
                throw new InvalidOperationException("Engineering writes require a privately opened project. Omit portalProcessId in the write profile.");
        }

        internal void MarkEngineeringChanged() => _hasUnsavedChanges = true;

        internal Device FindDevice(string path) =>
            Devices().SingleOrDefault(entry => String.Equals(entry.Key, path, StringComparison.Ordinal)).Value
                ?? throw new InvalidOperationException($"Device '{path}' does not exist in '{_projectPath}'.");

        private IEnumerable<KeyValuePair<string, Device>> Devices()
        {
            foreach (var device in Project.Devices)
                yield return new KeyValuePair<string, Device>(device.Name, device);

            var ungrouped = Project.UngroupedDevicesGroup?.Devices;
            if (ungrouped != null)
            {
                foreach (var device in ungrouped)
                {
                    if (Project.Devices.Find(device.Name) == null)
                        yield return new KeyValuePair<string, Device>(device.Name, device);
                }
            }

            foreach (var group in Project.DeviceGroups)
            {
                foreach (var item in GroupDevices(group, group.Name))
                    yield return item;
            }
        }

        private static IEnumerable<KeyValuePair<string, Device>> GroupDevices(DeviceUserGroup group, string path)
        {
            foreach (var device in group.Devices)
                yield return new KeyValuePair<string, Device>(path + "/" + device.Name, device);
            foreach (var child in group.Groups)
            {
                foreach (var item in GroupDevices(child, path + "/" + child.Name))
                    yield return item;
            }
        }

        internal static object Compile(IEngineeringServiceProvider provider)
        {
            var compiler = provider.GetService<ICompilable>()
                ?? throw new InvalidOperationException("The selected target does not expose an Openness compiler.");
            var result = compiler.Compile();
            return new
            {
                state = result.State.ToString(),
                errors = result.ErrorCount,
                warnings = result.WarningCount,
                messages = result.Messages.Select(CompilerMessage).ToArray()
            };
        }

        private static object CompilerMessage(CompilerResultMessage message) => new
        {
            path = message.Path,
            description = message.Description,
            state = message.State.ToString(),
            errors = message.ErrorCount,
            warnings = message.WarningCount,
            messages = message.Messages.Select(CompilerMessage).ToArray()
        };

        internal static string RequiredString(JsonElement obj, string name)
        {
            if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
                String.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidOperationException($"'{name}' must be a nonempty string.");
            return value.GetString()!;
        }

        internal static bool Flag(JsonElement obj, string name) =>
            obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private void Disconnect()
        {
            _project = null;
            try
            {
                _portal?.Dispose();
            }
            finally
            {
                _portal = null;
            }
        }

        public void Dispose() => Disconnect();
    }
}
