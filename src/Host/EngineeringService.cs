using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace SiemensEngineeringMcp;

internal sealed class EngineeringService : IDisposable
{
    private readonly WorkstationProfile _profile;
    private readonly Dictionary<string, WorkerClient> _workers;
    private readonly Dictionary<string, FileStream> _leases = new();
    private readonly string _stateRoot;
    private readonly string _journal;
    private bool _disposed;

    public EngineeringService(WorkstationProfile profile)
    {
        _profile = profile;
        _workers = new[] { "Engineering", "Simulation", "Runtime" }
            .ToDictionary(name => name, name => new WorkerClient(name, profile));
        _stateRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SiemensEngineeringMcp");
        _journal = Path.Combine(_stateRoot, "operations",
            WorkstationProfile.Hash(JsonSerializer.Serialize(profile, WorkstationProfile.Json)));
    }

    internal async Task<CallToolResult> Read(string operation, object? args = null)
    {
        try
        {
            return Result(await Work(operation, args ?? new { }));
        }
        catch (Exception error)
        {
            return Failure(operation, error);
        }
    }

    internal async Task<CallToolResult> Doctor()
    {
        async Task<object> Probe(string operation)
        {
            try { return new { available = true, evidence = await Work(operation, new { }) }; }
            catch (Exception error) { return new { available = false, detail = error.Message }; }
        }

        var results = await Task.WhenAll(Probe("engineering_probe"), Probe("sim_probe"), Probe("runtime_probe"));
        return Result(new
        {
            _profile.TiaVersion, _profile.ProjectPath, _profile.PortalProcessId, _profile.PlcPath, _profile.HmiPath,
            _profile.SimInstance, _profile.AllowEngineeringWrite, _profile.AllowSimulationWrite,
            engineering = results[0], simulation = results[1], unifiedRuntime = results[2]
        });
    }

    internal Task<CallToolResult> ReadRuntime(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName) ||
            !_profile.RuntimeTagPrefixes.Any(prefix => tagName.StartsWith(prefix, StringComparison.Ordinal)))
            return Task.FromResult(Failure("runtime_read", new InvalidOperationException("Tag is outside the configured Runtime tag prefixes.")));
        return Read("runtime_read", new { tagName });
    }

    internal CallToolResult Start(string operation, Guid requestId, object? args = null)
    {
        try
        {
            if (requestId == Guid.Empty)
                throw new ArgumentException("requestId must be a non-empty UUID.");

            var simulation = Provider(operation) == "Simulation";
            if (simulation ? !_profile.AllowSimulationWrite : !_profile.AllowEngineeringWrite)
                throw new InvalidOperationException("This workstation profile does not permit the requested write.");

            var arguments = args ?? new { };
            var fingerprint = WorkstationProfile.Hash(JsonSerializer.Serialize(arguments, WorkstationProfile.Json));
            Directory.CreateDirectory(_journal);
            using var prepare = OpenLease(Path.Combine(_journal, requestId.ToString("N") + ".prepare"));
            var record = ReadRecord(requestId);
            if (record != null)
            {
                if (record.Operation != operation || record.Fingerprint != fingerprint)
                    throw new InvalidOperationException("requestId is already bound to different arguments.");
                return Describe(record);
            }

            AcquireTarget(simulation);
            var running = OpenLease(RunningPath(requestId));
            record = new(requestId, operation, fingerprint, "running", DateTimeOffset.UtcNow, null, null);
            try { WriteRecord(record); }
            catch { running.Dispose(); throw; }

            _ = Task.Run(() => Complete(record, arguments, running));
            return Describe(record);
        }
        catch (Exception error)
        {
            return Failure(operation, error);
        }
    }

    private async Task Complete(OperationRecord record, object arguments, FileStream running)
    {
        using (running)
        {
            try
            {
                var data = await Work(record.Operation, arguments);
                var failure = record.Operation switch
                {
                    "plc_compile" or "hmi_compile" when data.GetProperty("errors").GetInt32() != 0
                        => "Compilation reported errors. Review the compiler messages and keep the project unsaved.",
                    "sim_write" when !data.GetProperty("matched").GetBoolean()
                        => "Simulator readback did not match the requested value.",
                    _ => null
                };
                WriteRecord(record with { Status = failure == null ? "succeeded" : "failed", Data = data, Error = failure });
            }
            catch (Exception error)
            {
                try { WriteRecord(record with { Status = "failed", Error = error.Message }); }
                catch (Exception writeError) { Console.Error.WriteLine(writeError.Message); }
            }
        }
    }

    internal CallToolResult Status(Guid requestId)
    {
        try
        {
            var record = ReadRecord(requestId) ?? throw new InvalidOperationException("Operation not found for this profile.");
            return Describe(record);
        }
        catch (Exception error) { return Failure("operation_status", error); }
    }

    private CallToolResult Describe(OperationRecord record)
    {
        if (record.Status == "running")
        {
            FileStream? lease = null;
            try { lease = OpenLease(RunningPath(record.RequestId)); }
            catch (IOException error) when ((error.HResult & 0xFFFF) is 32 or 33)
            {
                // The operation still owns its lease.
            }

            if (lease != null)
            {
                using (lease)
                {
                    record = ReadRecord(record.RequestId) ?? record;
                    if (record.Status == "running")
                        record = record with
                        {
                            Status = "interrupted",
                            Error = "Worker host exited before recording the outcome. Read back the target before applying another change."
                        };
                }
            }
        }

        return Result(new { record.RequestId, record.Operation, record.Status, record.StartedAt, record.Data, record.Error },
            record.Status is "interrupted" or "failed");
    }

    private Task<JsonElement> Work(string operation, object args) => _workers[Provider(operation)].CallAsync(operation, args);

    private static string Provider(string operation) =>
        operation.StartsWith("sim_", StringComparison.Ordinal) ? "Simulation" :
        operation.StartsWith("runtime_", StringComparison.Ordinal) ? "Runtime" : "Engineering";

    private void AcquireTarget(bool simulation)
    {
        var id = simulation ? "sim:" + _profile.SimInstance : "project:" + WorkstationProfile.LocalPath(_profile.ProjectPath);
        if (!simulation)
        {
            var project = new FileInfo(WorkstationProfile.LocalPath(_profile.ProjectPath));
            if ((project.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Write project cannot be a reparse point.");
            for (var directory = project.Directory; directory != null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Write project cannot be under a reparse point.");
        }

        lock (_leases)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_leases.ContainsKey(id))
                return;
            var directory = Path.Combine(_stateRoot, "leases");
            Directory.CreateDirectory(directory);
            try
            {
                _leases.Add(id, OpenLease(Path.Combine(directory, WorkstationProfile.Hash(id.ToUpperInvariant()) + ".lock")));
            }
            catch (IOException error)
            {
                throw new IOException($"TargetBusy: {id}. Close the other write-enabled MCP session before retrying.", error);
            }
        }
    }

    private string RecordPath(Guid id) => Path.Combine(_journal, id.ToString("N") + ".json");
    private string RunningPath(Guid id) => Path.Combine(_journal, id.ToString("N") + ".running");
    private string RecordLeasePath(Guid id) => Path.Combine(_journal, id.ToString("N") + ".record");
    private static FileStream OpenLease(string path) => new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private FileStream OpenRecordLease(Guid id)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return OpenLease(RecordLeasePath(id)); }
            catch (IOException error) when (attempt < 100 && (error.HResult & 0xFFFF) is 32 or 33)
            {
                Thread.Sleep(10);
            }
        }
    }

    private OperationRecord? ReadRecord(Guid id)
    {
        if (!File.Exists(RecordLeasePath(id)) && !File.Exists(RecordPath(id)))
            return null;

        using var lease = OpenRecordLease(id);
        try
        {
            using var input = new FileStream(RecordPath(id), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<OperationRecord>(input, WorkstationProfile.Json);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private void WriteRecord(OperationRecord record)
    {
        using var lease = OpenRecordLease(record.RequestId);
        var destination = RecordPath(record.RequestId);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(record, WorkstationProfile.Json));
        if (File.Exists(destination))
            File.Replace(temporary, destination, null);
        else
            File.Move(temporary, destination);
    }

    private static CallToolResult Failure(string operation, Exception error) =>
        Result(new { operation, error = error.Message }, true);

    private static CallToolResult Result(object content, bool error = false)
    {
        var json = JsonSerializer.SerializeToElement(content, WorkstationProfile.Json);
        return new()
        {
            IsError = error,
            StructuredContent = json,
            Content = [new TextContentBlock { Text = json.GetRawText() }]
        };
    }

    public void Dispose()
    {
        lock (_leases)
        {
            _disposed = true;
            try
            {
                foreach (var worker in _workers.Values)
                    worker.Dispose();
            }
            finally
            {
                foreach (var lease in _leases.Values)
                    lease.Dispose();
                _leases.Clear();
            }
        }
    }

    private sealed record OperationRecord(Guid RequestId, string Operation, string Fingerprint, string Status,
        DateTimeOffset StartedAt, JsonElement? Data, string? Error);
}
