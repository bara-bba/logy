using System.Text.Json;
using System.Text.Json.Serialization;

namespace Logy.Core.Config;

/// <summary>
/// Reads/writes logy's JSON files in one folder, <c>%APPDATA%\logy</c> by default.
/// Writes go to a per-process temp file first and are then moved over the target,
/// so a crash mid-save never leaves a half-written file and two logy processes
/// never share a temp file.
/// </summary>
public sealed class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public JsonStore(string folder)
    {
        Folder = folder;
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "logy");

    public string Folder { get; }

    public string PathFor(string fileName) => Path.Combine(Folder, fileName);

    /// <summary>Loads <paramref name="fileName"/>; returns <paramref name="fallback"/> when missing or unreadable.</summary>
    public T Load<T>(string fileName, Func<T> fallback)
    {
        var path = PathFor(fileName);
        if (!File.Exists(path))
        {
            return fallback();
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? fallback();
        }
        catch (JsonException)
        {
            // A broken settings file must not stop the app from opening; start from defaults.
            return fallback();
        }
        catch (IOException)
        {
            return fallback();
        }
    }

    public void Save<T>(string fileName, T value)
    {
        Directory.CreateDirectory(Folder);
        var path = PathFor(fileName);
        var temp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                // Another logy instance may be moving its own file in the same instant.
                Thread.Sleep(50);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
