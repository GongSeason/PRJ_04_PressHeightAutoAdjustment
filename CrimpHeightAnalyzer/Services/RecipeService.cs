using System.IO;
using System.Text.Json;
using CrimpHeightAnalyzer.Models;

namespace CrimpHeightAnalyzer.Services;

/// <summary>
/// 配方配置持久化服务
/// </summary>
public class RecipeService
{
    private static readonly string RecipePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "recipe_config.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// 加载配方配置。文件不存在或损坏时返回默认配置。
    /// </summary>
    public RecipeConfig Load()
    {
        try
        {
            if (File.Exists(RecipePath))
            {
                var json = File.ReadAllText(RecipePath);
                return JsonSerializer.Deserialize<RecipeConfig>(json) ?? new RecipeConfig();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载配方配置失败: {ex.Message}");
        }

        return new RecipeConfig();
    }

    /// <summary>
    /// 保存配方配置
    /// </summary>
    public bool Save(RecipeConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(RecipePath, json);
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存配方配置失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 更新指定配方的偏移量并保存
    /// </summary>
    public bool UpdateRecipeDeltas(RecipeConfig config, int programId, double headADelta, double headBDelta)
    {
        var recipe = config.Recipes.FirstOrDefault(r => r.ProgramId == programId);
        if (recipe == null) return false;

        recipe.HeadAManualDelta = headADelta;
        recipe.HeadBManualDelta = headBDelta;
        return Save(config);
    }
}
