using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace UnbrokenHelper.Data;

public sealed class BoardRouteCatalogLoader
{
    private const string DataFileName = "BoardRoutes.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly IPluginLog log;

    public BoardRouteCatalogLoader(IPluginLog log)
    {
        this.log = log;
    }

    public BoardRouteCatalog Load(IDalamudPluginInterface pluginInterface)
    {
        var assemblyDirectory = pluginInterface.AssemblyLocation.DirectoryName;
        if (string.IsNullOrWhiteSpace(assemblyDirectory))
        {
            log.Error("[Data] Could not resolve plugin assembly directory for board route data.");
            return new BoardRouteCatalog();
        }

        var path = Path.Combine(assemblyDirectory, "Data", DataFileName);
        try
        {
            if (!File.Exists(path))
            {
                log.Error($"[Data] Board route data file was not found: {path}");
                return new BoardRouteCatalog();
            }

            var catalog = JsonSerializer.Deserialize<BoardRouteCatalog>(File.ReadAllText(path), SerializerOptions)
                ?? new BoardRouteCatalog();
            Validate(catalog);
            log.Information($"[Data] Board route data loaded schemaVersion={catalog.SchemaVersion} " +
                $"boards={catalog.Boards.Count} routes={catalog.Boards.Sum(board => board.Routes.Count)}");
            return catalog;
        }
        catch (Exception ex)
        {
            log.Error(ex, $"[Data] Failed to load board route data: {path}");
            return new BoardRouteCatalog();
        }
    }

    private void Validate(BoardRouteCatalog catalog)
    {
        if (catalog.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Unsupported board route schema version: {catalog.SchemaVersion}");
        }

        var duplicateBoardKeys = catalog.Boards
            .GroupBy(board => board.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        foreach (var group in duplicateBoardKeys)
        {
            log.Error($"[Data] Invalid board route catalog board key: '{group.Key}'.");
        }

        var duplicateTerritories = catalog.Boards
            .Where(board => board.TerritoryId != 0)
            .GroupBy(board => board.TerritoryId)
            .Where(group => group.Count() > 1);
        foreach (var group in duplicateTerritories)
        {
            log.Error($"[Data] Duplicate board route territory: {group.Key}.");
        }

        foreach (var board in catalog.Boards)
        {
            ValidateBoard(board);
        }
    }

    private void ValidateBoard(BoardRouteBoard board)
    {
        if (string.IsNullOrWhiteSpace(board.Key) || board.TerritoryId == 0)
        {
            log.Error($"[Data] Invalid board route board key='{board.Key}' territory={board.TerritoryId}.");
        }

        var duplicateRoutes = board.Routes
            .GroupBy(route => route.Key, StringComparer.OrdinalIgnoreCase)
            .Where(group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1);
        foreach (var group in duplicateRoutes)
        {
            log.Error($"[Data] Invalid board route route key board='{board.Key}' key='{group.Key}'.");
        }

        foreach (var route in board.Routes)
        {
            ValidateRoute(board, route);
        }

        ValidateCollisionData(board);
    }

    private void ValidateRoute(BoardRouteBoard board, BoardRouteDefinition route)
    {
        var phaseKeys = route.Nodes
            .Select(node => node.Phase)
            .Where(phase => !string.IsNullOrWhiteSpace(phase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (phaseKeys.Count != route.Nodes.Count)
        {
            log.Error($"[Data] Duplicate or empty route node phase board='{board.Key}' route='{route.Key}'.");
        }

        foreach (var node in route.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Phase) || string.IsNullOrWhiteSpace(node.Kind) || string.IsNullOrWhiteSpace(node.Name))
            {
                log.Error($"[Data] Invalid route node board='{board.Key}' route='{route.Key}' phase='{node.Phase}'.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(node.NextPhase) && !phaseKeys.Contains(node.NextPhase))
            {
                log.Error($"[Data] Route transition target is missing board='{board.Key}' route='{route.Key}' " +
                    $"from='{node.Phase}' to='{node.NextPhase}'.");
            }

            if (route.Enabled && string.Equals(node.Kind, "Battle", StringComparison.OrdinalIgnoreCase) &&
                node.Combat.TargetBaseIds.Count == 0)
            {
                log.Error($"[Data] Enabled battle node has no targets board='{board.Key}' route='{route.Key}' phase='{node.Phase}'.");
            }

            ValidatePosition(node.Position, $"board='{board.Key}' route='{route.Key}' phase='{node.Phase}'");
        }
    }

    private void ValidateCollisionData(BoardRouteBoard board)
    {
        foreach (var position in board.CollisionData.Positions)
        {
            if (string.IsNullOrWhiteSpace(position.InstanceKey))
            {
                log.Error($"[Data] Collision position is missing instance key board='{board.Key}'.");
            }

            ValidatePosition(position.Position, $"board='{board.Key}' collision='{position.InstanceKey}'");
        }
    }

    private void ValidatePosition(BoardPosition position, string context)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
        {
            log.Error($"[Data] Invalid route position {context}.");
        }
    }
}
