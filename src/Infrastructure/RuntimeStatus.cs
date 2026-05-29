using System.Text.Json;

namespace DerivityMeter.Infrastructure;

public class EndpointStatus
{
    public bool Running { get; set; }
    public string? Url { get; set; }
}

public class RuntimeStatusFile
{
    public EndpointStatus Mcp { get; set; } = new();
    public EndpointStatus Otel { get; set; } = new();

    private static readonly string Path =
        System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".derivity", "runtime-meter", "runtime-status.json");

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(Path))
                File.WriteAllText(Path, JsonSerializer.Serialize(new RuntimeStatusFile(),
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
