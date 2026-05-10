using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompressMkv;

public static class JsonIO
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task WriteAsync<T>(string path, T obj, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, obj, Opts, ct);
    }

    public static async Task<T?> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(fs, Opts, ct);
    }
}
