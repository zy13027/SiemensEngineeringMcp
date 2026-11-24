using System;
using System.Text.Json;

namespace SiemensEngineeringMcp;

internal static class WorkerProtocol
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static int Run(Func<string, JsonElement, JsonElement, object?> dispatch)
    {
        Console.InputEncoding = new System.Text.UTF8Encoding(false);
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        var output = Console.Out;
        Console.SetOut(Console.Error);
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            string response;
            try
            {
                using var message = JsonDocument.Parse(line);
                var request = message.RootElement;
                var operation = request.GetProperty("operation").GetString()
                    ?? throw new InvalidOperationException("Missing worker operation.");
                var data = dispatch(operation, request.GetProperty("arguments"), request.GetProperty("profile"));
                response = JsonSerializer.Serialize(new { ok = true, data }, Json);
            }
            catch (Exception error)
            {
                var detail = error;
                while (detail.InnerException != null &&
                       (detail is System.Reflection.TargetInvocationException || detail is TypeInitializationException))
                    detail = detail.InnerException;

                response = JsonSerializer.Serialize(new { ok = false, error = detail.Message }, Json);
            }

            output.WriteLine(response);
            output.Flush();
        }

        return 0;
    }
}
