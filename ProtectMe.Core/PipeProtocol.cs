using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ProtectMe.Core;

public static class PipeConstants
{
    public const string PipeName = "ProtectMeWindowsServicePipe";
}

public sealed class PipeRequest
{
    public string Action { get; set; } = string.Empty;
    public string? Password { get; set; }
    public string? NewPassword { get; set; }
    public ProtectConfig? Config { get; set; }
    public WhitelistEntry? WhitelistEntry { get; set; }
    public string? EntryId { get; set; }
    public int? TimerMinutes { get; set; }
}

public sealed class PipeResponse
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public ProtectConfig? Config { get; set; }
    public bool ServiceRunning { get; set; } = true;
}

public static class PipeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<PipeResponse?> SendAsync(PipeRequest request, int timeoutMs = 1500)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeConstants.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeoutMs);

            string payload = JsonSerializer.Serialize(request, JsonOptions) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            await client.WriteAsync(bytes);
            await client.FlushAsync();

            using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
            string? line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) return null;

            return JsonSerializer.Deserialize<PipeResponse>(line, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
