using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Models;

namespace PixelAgents.Tools;

/// <summary>
/// Migration tool to remove frozen systemPrompt from ProjectModule configurations.
/// 
/// Background:
/// When a module was added to a pipeline, the entire AiModule.Configuration (including
/// systemPrompt) was copied to ProjectModule.Configuration. This caused systemPrompt
/// to be "frozen" at that moment, so subsequent updates to the AiModule's systemPrompt
/// would not take effect in existing pipelines.
/// 
/// Solution:
/// This tool removes systemPrompt from all ProjectModule.Configuration fields, ensuring
/// that systemPrompt is always read from the AiModule.Configuration (allowing dynamic updates).
/// 
/// Usage:
/// Run this once after deploying the fix to clean up existing data.
/// </summary>
public class MigrateSystemPrompts
{
    public static async Task RunAsync(ITenantDbContextFactory factory, string[] tenantDbNames)
    {
        Console.WriteLine("Starting systemPrompt migration...");
        Console.WriteLine($"Will process {tenantDbNames.Length} tenant database(s)");
        Console.WriteLine();

        int totalUpdated = 0;

        foreach (var dbName in tenantDbNames)
        {
            Console.WriteLine($"Processing tenant: {dbName}");
            await using var db = factory.Create(dbName);
            
            var projectModules = await db.ProjectModules
                .Where(pm => pm.Configuration != null && pm.Configuration != "")
                .ToListAsync();

            int updatedCount = 0;

            foreach (var pm in projectModules)
            {
                var originalConfig = pm.Configuration;
                var cleanedConfig = RemoveSystemPromptFromConfig(originalConfig);

                if (originalConfig != cleanedConfig)
                {
                    pm.Configuration = cleanedConfig;
                    pm.UpdatedAt = DateTime.UtcNow;
                    updatedCount++;
                }
            }

            if (updatedCount > 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine($"  ✓ Updated {updatedCount} ProjectModule(s)");
            }
            else
            {
                Console.WriteLine($"  - No updates needed");
            }

            totalUpdated += updatedCount;
            Console.WriteLine();
        }

        Console.WriteLine($"Migration completed successfully!");
        Console.WriteLine($"Total ProjectModules updated: {totalUpdated}");
    }

    private static string? RemoveSystemPromptFromConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return configJson;

            var filtered = new Dictionary<string, JsonElement>();
            bool hadSystemPrompt = false;

            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.Equals("systemPrompt", StringComparison.OrdinalIgnoreCase))
                {
                    hadSystemPrompt = true;
                    continue; // Skip systemPrompt
                }

                filtered[prop.Name] = prop.Value.Clone();
            }

            // If no changes, return original
            if (!hadSystemPrompt)
                return configJson;

            // If empty after removing systemPrompt, return null
            if (filtered.Count == 0)
                return null;

            return JsonSerializer.Serialize(filtered);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠ Warning: Failed to parse config JSON: {ex.Message}");
            return configJson; // Return original on parse error
        }
    }
}
