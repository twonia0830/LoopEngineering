using System.Text.Json;
using System.Text.Json.Serialization;
using LoopEngineering.Core.Models;

namespace LoopEngineering.Core.Loop;

/// <summary>Remembers the last used configuration between app restarts. Never stores the token itself.</summary>
public sealed class ConfigStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LoopConfig Load()
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<LoopConfig>(File.ReadAllText(path), Options) ?? new LoopConfig();
        }
        catch (JsonException)
        {
            // fall through to defaults on a corrupt file
        }
        return new LoopConfig();
    }

    public void Save(LoopConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, Options));
    }
}
