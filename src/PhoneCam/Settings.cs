using System.Text.Json;

namespace PhoneCam;

sealed class Settings
{
    public string Language { get; set; } = "en";
    public string? CameraId { get; set; }
    public string? Size { get; set; }
    public int Fps { get; set; } = 30;
    public int BitRate { get; set; } = 8_000_000;
    public int Rotation { get; set; }
    public bool Mirror { get; set; }
    public string Fit { get; set; } = "contain";
    public bool PanelCollapsed { get; set; }
    public int PanelWidth { get; set; } = 340;
    /// <summary>"serial/cameraId" of lenses the phone refuses to external capture sessions.</summary>
    public List<string> UnusableCameras { get; set; } = [];

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneCam");

    static string FilePath => Path.Combine(Folder, "settings.json");

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            SaveCore();
        }
        catch
        {
        }
    }

    public bool TrySave(out Exception? error)
    {
        try
        {
            SaveCore();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex;
            return false;
        }
    }

    void SaveCore()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
