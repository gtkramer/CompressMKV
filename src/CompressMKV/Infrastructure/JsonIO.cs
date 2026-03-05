using System.Text.Json;
using System.Text.Json.Serialization;

namespace CompressMkv;

public static class JsonIO
{
    public static async Task WriteAsync<T>(string path, T obj, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var opt = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        await using var fs = File.Create(path);
        await JsonSerializer.SerializeAsync(fs, obj, opt, ct);
    }
}
