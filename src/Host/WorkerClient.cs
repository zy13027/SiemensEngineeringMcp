using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace SiemensEngineeringMcp;

internal sealed class WorkerClient(string name, WorkstationProfile profile) : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1);
    private readonly object _sync = new();
    private Process? _process;
    private bool _disposed;

    internal async Task<JsonElement> CallAsync(string operation, object arguments)
    {
        await _serial.WaitAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            var worker = GetProcess();
            try
            {
                var message = JsonSerializer.Serialize(new { operation, arguments, profile }, WorkstationProfile.Json);
                await worker.StandardInput.WriteLineAsync(message.AsMemory(), deadline.Token);
                await worker.StandardInput.FlushAsync(deadline.Token);
                using var result = JsonDocument.Parse(await worker.StandardOutput.ReadLineAsync(deadline.Token)
                    ?? throw new IOException("Worker closed its output."));
                var reply = result.RootElement;
                if (!reply.GetProperty("ok").GetBoolean())
                    throw new WorkerOperationException(reply.GetProperty("error").GetString() ?? "Worker operation failed.");
                return reply.GetProperty("data").Clone();
            }
            catch (Exception error) when (error is not WorkerOperationException)
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_process, worker))
                        StopProcess();
                }

                throw new IOException($"{name} worker interrupted during {operation}. Inspect the target before retrying. {error.Message}", error);
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private Process GetProcess()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_process != null && !_process.HasExited)
                return _process;

            StopProcess();
            var home = Path.Combine(AppContext.BaseDirectory, "workers", name);
            if (name == "Engineering")
                home = Path.Combine(home, profile.TiaVersion);
            var path = Path.Combine(home, name + ".exe");
            if (!File.Exists(path))
                throw new FileNotFoundException($"{name} worker is unavailable. Build the workstation bundle.", path);

            var config = new ProcessStartInfo(path)
            {
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            _process = Process.Start(config) ?? throw new IOException($"Cannot start {name} worker.");
            return _process;
        }
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        if (process == null)
            return;

        using (process)
        {
            try { process.StandardInput.Close(); }
            catch (IOException) { /* The pipe may already have closed. */ }
            catch (InvalidOperationException) { /* A write may be pending. */ }

            if (!process.WaitForExit(2000))
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
                process.WaitForExit(2000);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            StopProcess();
        }
    }
}

internal sealed class WorkerOperationException(string message) : Exception(message);
