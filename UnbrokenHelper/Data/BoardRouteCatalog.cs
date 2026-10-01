using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace UnbrokenHelper.Data;

public sealed class BoardRouteCatalog
{
    public int SchemaVersion { get; set; }
    public List<BoardRouteBoard> Boards { get; set; } = new();

    public BoardRouteBoard? FindBoard(string key)
        => Boards.FirstOrDefault(board => string.Equals(board.Key, key, System.StringComparison.OrdinalIgnoreCase));

    public BoardRouteDefinition? FindRoute(string boardKey, string routeKey, string mode = "Standard")
    {
        var routes = FindBoard(boardKey)?.Routes
            .Where(route => string.Equals(route.Key, routeKey, System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (routes == null)
        {
            return null;
        }

        // Board geometry is shared across Crucible modes unless a mode-specific route is added later.
        return routes.FirstOrDefault(route =>
                   string.Equals(string.IsNullOrWhiteSpace(route.Mode) ? "Standard" : route.Mode,
                       mode, System.StringComparison.OrdinalIgnoreCase)) ??
               routes.FirstOrDefault(route =>
                   string.Equals(string.IsNullOrWhiteSpace(route.Mode) ? "Standard" : route.Mode,
                       "Standard", System.StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class BoardRouteBoard
{
    public string Key { get; set; } = string.Empty;
    public uint TerritoryId { get; set; }
    public List<BoardRouteDefinition> Routes { get; set; } = new();
    public BoardCollisionData CollisionData { get; set; } = new();
}

public sealed class BoardRouteDefinition
{
    public string Key { get; set; } = string.Empty;
    public string Mode { get; set; } = "Standard";
    public bool Enabled { get; set; }
    public List<BoardRouteNodeData> Nodes { get; set; } = new();
}

public sealed class BoardRouteNodeData
{
    public string Phase { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public int StageNumber { get; set; }
    public string Variant { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public BoardPosition Position { get; set; } = new();
    public string ExpectedAddon { get; set; } = string.Empty;
    public string NextPhase { get; set; } = string.Empty;
    public int RecoveryThreshold { get; set; } = 60;
    public int RecoveryTarget { get; set; } = 90;
    public int FamiliarStageNumber { get; set; }
    public string FamiliarRosterKey { get; set; } = string.Empty;
    public bool DirectResultFlow { get; set; }
    public BoardCombatData Combat { get; set; } = new();
}

public sealed class BoardCombatData
{
    public string DisplayName { get; set; } = string.Empty;
    public List<uint> TargetBaseIds { get; set; } = new();
    public uint PriorityTargetBaseId { get; set; }
    public List<BoardActionTriggerData> ActionTriggers { get; set; } = new();
}

public sealed class BoardActionTriggerData
{
    public uint SourceBaseId { get; set; }
    public uint ActionId { get; set; }
    public string ActionName { get; set; } = string.Empty;
    public string Response { get; set; } = string.Empty;
    public uint ResponseActionId { get; set; }
    public string ResponseActionName { get; set; } = string.Empty;
    public uint ResponseTargetBaseId { get; set; }
}

public sealed class BoardCollisionData
{
    public ushort LayerKey { get; set; }
    public string PcbPathCrc { get; set; } = string.Empty;
    public List<BoardCollisionPosition> Positions { get; set; } = new();
}

public sealed class BoardCollisionPosition
{
    public string InstanceKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string RouteHint { get; set; } = string.Empty;
    public BoardPosition Position { get; set; } = new();
}

public sealed class BoardPosition
{
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }

    public Vector3 ToVector3() => new(X, Y, Z);
}
