using System.IO;
using System.Text.Json;
using CrimpHeightAnalyzer.Models;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// 配置管理服务
/// </summary>
public class SettingsService
{
    private static readonly string SettingsPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "settings.json");

    /// <summary>
    /// 加载配置
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                settings.MonitorPath ??= string.Empty;
                settings.Controller ??= new();
                settings.Plc ??= new();
                return settings;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载配置失败: {ex.Message}");
        }

        return new AppSettings();
    }

    /// <summary>
    /// 保存配置
    /// </summary>
    public bool Save(AppSettings settings)
    {
        try
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(settings, options);
            File.WriteAllText(SettingsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存配置失败: {ex.Message}");
            return false;
        }
    }
}
