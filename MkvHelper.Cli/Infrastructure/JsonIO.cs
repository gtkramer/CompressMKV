using System.Text.Json;
using System.Text.Json.Serialization;

namespace MkvHelper;

public static class JsonIO
{
    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync<T>(string path, T obj, CancellationToken ct)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await using FileStream fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, obj, s_jsonOpts, ct);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using FileStream fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(fs, s_jsonOpts, ct);
    }
}
