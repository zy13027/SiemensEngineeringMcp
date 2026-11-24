using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SiemensEngineeringMcp;

internal static class Program
{
    private const string PipeName = "HmiRuntime";
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);
    private const string SiemensSample = @"F:\Program Files\Siemens\Automation\WinCCUnified\OpenPipe\Samples\Javascript\ExpertSyntax\ReadTags.js";

    private static int Main() => WorkerProtocol.Run(Dispatch);

    private static object Dispatch(string operation, JsonElement arguments, JsonElement _) => operation switch
    {
        "runtime_probe" => new { pipe = @"\\.\pipe\HmiRuntime", localSamplePresent = File.Exists(SiemensSample), liveChecked = false },
        "runtime_read" => ReadAsync(RequiredTagName(arguments)).GetAwaiter().GetResult(),
        _ => throw new InvalidOperationException($"Unknown Unified Runtime operation '{operation}'.")
    };

    private static string RequiredTagName(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object ||
            !arguments.TryGetProperty("tagName", out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new ArgumentException("A Unified Runtime tagName is required.");
        var name = value.GetString()!;
        if (name.Length > 512 || name.Any(char.IsControl))
            throw new ArgumentException("The Runtime tag name must be at most 512 characters and contain no control characters.");
        return name;
    }

    private static async Task<object> ReadAsync(string tagName)
    {
        string cookie = Guid.NewGuid().ToString("N");
        byte[] request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Message = "ReadTag",
            Params = new { Tags = new[] { tagName } },
            ClientCookie = cookie
        }) + "\n");

        using var deadline = new CancellationTokenSource(ReadTimeout);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(deadline.Token);
            await pipe.WriteAsync(request, deadline.Token);
            await pipe.FlushAsync(deadline.Token);
            using var response = await ReadResponseAsync(pipe, deadline.Token);
            return ParseTag(response.RootElement, cookie, tagName);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"Unified Open Pipe read for '{tagName}' timed out after {ReadTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static async Task<JsonDocument> ReadResponseAsync(Stream pipe, CancellationToken cancellation)
    {
        using var response = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            int count = await pipe.ReadAsync(buffer.AsMemory(), cancellation);
            if (count == 0)
                throw new IOException("Unified Runtime closed Open Pipe before completing its response.");
            int lineEnd = Array.IndexOf(buffer, (byte)'\n', 0, count);
            int length = lineEnd < 0 ? count : lineEnd;
            if (response.Length + length > MaximumResponseBytes)
                throw new IOException("Unified Runtime Open Pipe response exceeded 64 KiB.");
            response.Write(buffer, 0, length);
            if (lineEnd >= 0)
                return JsonDocument.Parse(response.GetBuffer().AsMemory(0, checked((int)response.Length)));
        }
    }

    private static object ParseTag(JsonElement response, string cookie, string tagName)
    {
        if (response.GetProperty("ClientCookie").GetString() != cookie)
            throw new InvalidDataException("Unified Runtime returned a response for a different request.");
        var message = response.GetProperty("Message").GetString();
        if (message == "ErrorReadTag")
            throw new InvalidOperationException($"Unified Runtime rejected '{tagName}': {ErrorDetails(response)}");
        if (message != "NotifyReadTag")
            throw new InvalidDataException($"Unified Runtime returned unexpected message '{message}'.");

        var tags = response.GetProperty("Params").GetProperty("Tags");
        if (tags.GetArrayLength() != 1 || tags[0].GetProperty("Name").GetString() != tagName)
            throw new InvalidDataException($"Unified Runtime returned a different tag from '{tagName}'.");
        var tag = tags[0];
        if (tag.TryGetProperty("ErrorCode", out var code) && code.ToString() != "0")
            throw new InvalidOperationException($"Unified Runtime failed to read '{tagName}': {ErrorDetails(tag)}");

        var quality = GetValue(tag, "Quality");
        return new
        {
            tagName,
            value = tag.GetProperty("Value").Clone(),
            quality,
            qualityCode = GetValue(tag, "QualityCode"),
            isGood = string.Equals(quality?.ToString(), "Good", StringComparison.OrdinalIgnoreCase),
            runtimeTimestamp = GetValue(tag, "TimeStamp"),
            observedAtUtc = DateTimeOffset.UtcNow
        };
    }

    private static JsonElement? GetValue(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.Clone() : null;

    private static string ErrorDetails(JsonElement reply) =>
        $"{GetValue(reply, "ErrorCode")} {GetValue(reply, "ErrorDescription")}".Trim();
}

