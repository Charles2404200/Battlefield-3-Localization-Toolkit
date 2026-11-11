using System.Text.Json;

namespace Bf3VietHoaGui;

internal sealed class GuiSettings
{
    public string GameDirectory { get; set; } = "";
    public string ExportDirectory { get; set; } = "";
    public string SheetPath { get; set; } = "";

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BF3VietHoa", "gui-settings.json");

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(SettingsPath)) ?? new GuiSettings();
        }
        catch (Exception) { /* A damaged settings file should not prevent starting the GUI. */ }
        return new GuiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception) { /* Saving preferences is optional. */ }
    }
}
