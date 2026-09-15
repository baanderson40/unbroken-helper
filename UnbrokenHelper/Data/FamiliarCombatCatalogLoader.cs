using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace UnbrokenHelper.Data;

public sealed class FamiliarCombatCatalogLoader
{
    private const string DataFileName = "FamiliarCombat.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IPluginLog log;

    public FamiliarCombatCatalogLoader(IPluginLog log)
    {
        this.log = log;
    }

    public FamiliarCombatCatalog Load(IDalamudPluginInterface pluginInterface)
    {
        var assemblyDirectory = pluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            log.Error("[Data] Could not resolve plugin assembly directory for familiar combat data.");
            return new FamiliarCombatCatalog();
        }

        var path = Path.Combine(assemblyDirectory, "Data", DataFileName);
        try
        {
            if (!File.Exists(path))
            {
                log.Error($"[Data] Familiar combat data file was not found: {path}");
                return new FamiliarCombatCatalog();
            }

            var catalog = JsonSerializer.Deserialize<FamiliarCombatCatalog>(File.ReadAllText(path), SerializerOptions)
                ?? new FamiliarCombatCatalog();
            Validate(catalog);
            log.Information($"[Data] Familiar combat data loaded schemaVersion={catalog.SchemaVersion} " +
                $"familiars={catalog.Familiars.Count} patch={catalog.Patch}.");
            return catalog;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[Data] Failed to load familiar combat data: {path}");
            return new FamiliarCombatCatalog();
        }
    }

    private void Validate(FamiliarCombatCatalog catalog)
    {
        if (catalog.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported familiar combat schema version: {catalog.SchemaVersion}");
        }

        var duplicateNumbers = catalog.Familiars
            .GroupBy(familiar => familiar.Number)
            .Where(group => group.Key is < 1 or > 50 || group.Count() > 1);
        foreach (var group in duplicateNumbers)
        {
            log.Error($"[Data] Invalid familiar combat catalog number: '{group.Key}'.");
        }

        foreach (var familiar in catalog.Familiars)
        {
            ValidateAction(familiar.TemperedRelease, familiar.Number, "Tempered Release");
            ValidateAction(familiar.Trick, familiar.Number, "Trick");
            ValidateAction(familiar.Borrow, familiar.Number, "Borrow");
        }
    }

    private void ValidateAction(FamiliarActionSummary action, int number, string actionType)
    {
        if (string.IsNullOrWhiteSpace(action.Name) || string.IsNullOrWhiteSpace(action.Description))
        {
            log.Error($"[Data] Familiar No. {number} has incomplete {actionType} data.");
        }
    }
}
