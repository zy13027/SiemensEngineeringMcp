using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Siemens.Simatic.Simulation.Runtime;

namespace SiemensEngineeringMcp;

internal sealed class SimulationSession(string instanceName) : ISimulationSession
{
    private IInstance? instance;
    private Dictionary<string, STagInfo>? descriptors;
    private bool refreshRequired;

    public object Dispatch(string operation, JsonElement arguments, JsonElement profile, string configuredInstance)
    {
        if (!string.Equals(configuredInstance, instanceName, StringComparison.Ordinal))
            throw new InvalidOperationException("The configured simulator changed. Restart the simulation worker.");

        try
        {
            return operation switch
            {
                "sim_instances" => DescribeInstances(),
                "sim_state" => DescribeState(Attach()),
                "sim_refresh" => Refresh(Attach()),
                "sim_read" => Read(Program.RequiredString(arguments, "tag")),
                "sim_write" => Write(Program.RequiredString(arguments, "tag"), arguments, profile),
                "sim_wait" => Wait(Program.RequiredString(arguments, "tag"), arguments),
                _ => throw new ArgumentException($"Unknown simulation operation '{operation}'.")
            };
        }
        catch (SimulationInitializationException ex)
        {
            Invalidate();
            throw new InvalidOperationException($"PLCSIM Runtime Manager initialisation failed for '{instanceName}': {ex.Message}", ex);
        }
        catch (SimulationRuntimeException ex)
        {
            Invalidate();
            throw new InvalidOperationException(
                $"PLCSIM {operation} failed for '{instanceName}' ({ex.RuntimeErrorCode}): {ex.Message} Refresh symbolic tags before retrying.", ex);
        }
    }

    private object DescribeInstances()
    {
        var configured = SimulationRuntimeManager.RegisteredInstanceInfo
            .Where(entry => string.Equals(entry.Name, instanceName, StringComparison.Ordinal))
            .Select(entry => new { name = entry.Name, id = entry.ID })
            .ToArray();

        return new { configuredInstance = instanceName, runtimeManagerVersion = RuntimeManagerVersion(), instances = configured };
    }

    private static object DescribeState(IInstance target) => new
    {
        instance = target.Name,
        id = target.ID,
        cpuType = target.CPUType.ToString(),
        state = target.OperatingState.ToString(),
        runtimeManagerVersion = RuntimeManagerVersion()
    };

    private static string RuntimeManagerVersion()
    {
        var version = SimulationRuntimeManager.Version;
        return $"{version >> 16}.{version & 0xffff}";
    }

    private IInstance Attach()
    {
        var matching = SimulationRuntimeManager.RegisteredInstanceInfo
            .Where(entry => string.Equals(entry.Name, instanceName, StringComparison.Ordinal))
            .ToArray();

        if (matching.Length != 1)
        {
            Invalidate();
            throw new InvalidOperationException(
                $"Expected one registered PLCSIM instance named exactly '{instanceName}', found {matching.Length}.");
        }

        if (instance is not null && instance.ID == matching[0].ID)
            return instance;

        if (instance is not null)
            Invalidate();

        var target = SimulationRuntimeManager.CreateInterface(instanceName);
        if (!string.Equals(target.Name, instanceName, StringComparison.Ordinal) || target.ID != matching[0].ID)
        {
            target.Dispose();
            throw new InvalidOperationException($"PLCSIM registration changed during attachment to '{instanceName}'.");
        }

        instance = target;
        return target;
    }

    private object Refresh(IInstance target)
    {
        descriptors = null;
        refreshRequired = true;
        target.UpdateTagList(ETagListDetails.IOMCTDB, false);
        var current = new Dictionary<string, STagInfo>(StringComparer.Ordinal);
        foreach (var info in target.TagInfos)
        {
            if (!string.IsNullOrEmpty(info.Name) && !current.TryAdd(info.Name, info))
                throw new InvalidOperationException($"Duplicate symbolic tag '{info.Name}' on '{instanceName}'.");
        }

        descriptors = current;
        refreshRequired = false;
        return new { instance = instanceName, id = target.ID, count = current.Count };
    }

    private STagInfo FindTag(IInstance target, string name)
    {
        if (refreshRequired)
            throw new InvalidOperationException($"Symbolic tags for '{instanceName}' require sim_refresh.");

        if (descriptors is null)
            Refresh(target);

        if (descriptors!.TryGetValue(name, out var info))
            return info;

        throw new ArgumentException($"Symbolic tag '{name}' was not found on '{instanceName}'. Call sim_refresh after a download.");
    }

    private object Read(string tag)
    {
        var target = Attach();
        var info = FindTag(target, tag);
        return new
        {
            instance = instanceName,
            id = target.ID,
            tag,
            dataType = info.DataType.ToString(),
            value = JsonValue(TagValues.Read(target, info)),
            observedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private object Write(string tag, JsonElement arguments, JsonElement profile)
    {
        if (!profile.TryGetProperty("allowSimulationWrite", out var allowed) || allowed.ValueKind != JsonValueKind.True
            || !profile.TryGetProperty("writableTags", out var tags) || tags.ValueKind != JsonValueKind.Array
            || !tags.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && string.Equals(item.GetString(), tag, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Simulated writes to tag '{tag}' on '{instanceName}' are disabled by the profile.");

        var target = Attach();
        if (target.OperatingState != EOperatingState.Run)
            throw new InvalidOperationException($"PLCSIM instance '{instanceName}' must be in RUN for test input writes.");

        var info = FindTag(target, tag);
        var requested = GetValue(arguments, info);
        var before = TagValues.Read(target, info);

        try
        {
            TagValues.Write(target, info, requested);
            var after = TagValues.Read(target, info);
            return new
            {
                instance = instanceName,
                id = target.ID,
                tag,
                dataType = info.DataType.ToString(),
                requested,
                before = JsonValue(before),
                after = JsonValue(after),
                matched = Equals(requested, after),
                observedAtUtc = DateTimeOffset.UtcNow
            };
        }
        catch (SimulationRuntimeException ex)
        {
            Invalidate();
            throw new InvalidOperationException(
                $"Write to '{instanceName}/{tag}' was attempted. Its outcome is unverified ({ex.RuntimeErrorCode}): {ex.Message}", ex);
        }
    }

    private object Wait(string tag, JsonElement arguments)
    {
        if (!arguments.TryGetProperty("timeoutMs", out var configuredTimeout)
            || configuredTimeout.ValueKind != JsonValueKind.Number
            || !configuredTimeout.TryGetInt32(out var timeout) || timeout is < 1 or > 60000)
            throw new ArgumentException("'timeoutMs' must be an integer between 1 and 60000.");

        var target = Attach();
        var info = FindTag(target, tag);
        var expected = GetValue(arguments, info);
        var timer = Stopwatch.StartNew();
        object observed;

        do
        {
            observed = TagValues.Read(target, info);
            if (Equals(expected, observed) && timer.ElapsedMilliseconds <= timeout)
            {
                return new
                {
                    instance = instanceName,
                    id = target.ID,
                    tag,
                    dataType = info.DataType.ToString(),
                    value = JsonValue(observed),
                    matched = true,
                    elapsedMs = timer.ElapsedMilliseconds,
                    observedAtUtc = DateTimeOffset.UtcNow
                };
            }

            var remaining = timeout - timer.ElapsedMilliseconds;
            if (remaining > 0)
                Thread.Sleep((int)Math.Min(50, remaining));
        }
        while (timer.ElapsedMilliseconds < timeout);

        throw new TimeoutException(
            $"Timed out waiting for '{instanceName}/{tag}' ({info.DataType}) after {timeout} ms. "
            + $"Expected {Convert.ToString(expected, CultureInfo.InvariantCulture)}, last observed {Convert.ToString(observed, CultureInfo.InvariantCulture)}.");
    }

    private static object GetValue(JsonElement arguments, STagInfo info)
    {
        if (!arguments.TryGetProperty("value", out var value))
            throw new ArgumentException("A typed 'value' is required.");

        return TagValues.Parse(info, value);
    }

    private static object JsonValue(object value) => value switch
    {
        double number when !double.IsFinite(number) => number.ToString(CultureInfo.InvariantCulture),
        float number when !float.IsFinite(number) => number.ToString(CultureInfo.InvariantCulture),
        _ => value
    };

    private void Invalidate()
    {
        refreshRequired = true;
        descriptors = null;
        Dispose();
    }

    public void Dispose()
    {
        var previous = instance;
        instance = null;
        previous?.Dispose();
    }
}
