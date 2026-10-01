using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Configuration;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using ECommons;
using ECommons.Automation;
using ECommons.GameFunctions;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine;
using FFXIVClientStructs.FFXIV.Client.LayoutEngine.Layer;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using UnbrokenHelper.Data;
using UnbrokenHelper.Services;
using UnbrokenHelper.Windows;
using Callback = ECommons.Automation.Callback;

namespace UnbrokenHelper;

internal sealed class NotebookFamiliarEntry
{
    internal int UiIndex { get; init; }
    internal int Number { get; init; }
    internal uint PetId { get; init; }
}

internal sealed class BattleNpcActionTrackerEntry
{
    internal ulong ObjectId { get; init; }
    internal uint BaseId { get; init; }
    internal string Name { get; init; } = string.Empty;
    internal byte ActionType { get; init; }
    internal uint ActionId { get; init; }
    internal string ActionName { get; init; } = string.Empty;
    internal string TargetId { get; init; } = string.Empty;
    internal float CurrentCastTime { get; init; }
    internal float TotalCastTime { get; init; }
    internal bool Interruptible { get; init; }
}

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/unbroken";
    private const string BossModPresetName = "Unbroken";
    private const string BossModPresetFileName = "UnbrokenBossModPreset.json";
    private const uint SnarlActionId = 46751;
    private const float StageTransitionArrivalRadius = 0.5f;
    private const float CombatPositionArrivalRadius = 1.0f;
    private static readonly Vector3 Stage4APriorityReturnPosition = new(119.157f, 0f, -419.448f);
    private const int RecoverySessionTimeoutSeconds = 30;
    private const int CombatRecoveryCheckMilliseconds = 500;
    private const int NeedGreedAppearanceGracePeriodMilliseconds = 2000;
    private const int TreasureDataPollMilliseconds = 250;
    private const int TreasureDataMinimumWaitMilliseconds = 750;
    private const int TreasureDataMaximumWaitMilliseconds = 2000;
    private const int TreasureDataStablePollsRequired = 2;
    private const uint SandstillStatusId = 4870;
    private const int SandstillRestartTimeoutSeconds = 30;
    private const int StageDetailModeSelectionMaximumAttempts = 8;
    private const int StageDetailModeSelectionDelayMilliseconds = 300;
    private static readonly string[] CrucibleModeNames =
    {
        "Standard",
        "First Degree",
        "Second Degree",
        "Third Degree",
    };
    private const uint ThiefsKnifeItemId = 243057;
    private const int ItemUseAttemptTimeoutMilliseconds = 2000;
    private const int ItemUseMaximumAttempts = 3;
    private const int ActionTriggerMovementTimeoutSeconds = 15;
    private const int NotebookPageSize = 25;
    private static readonly IReadOnlySet<uint> ItemAttackIds = new HashSet<uint>
    {
        243188, 243189, 243190, 243191, 243192, 243193, 243194, 243199,
    };
    private static readonly IReadOnlySet<uint> ItemBoardPreStageIds = new HashSet<uint>
    {
        243198,
    };
    private static readonly IReadOnlySet<uint> ItemInStagePreBattleIds = new HashSet<uint>
    {
        243160, 243161,
    };
    private static readonly IReadOnlySet<uint> ItemSupportIds = new HashSet<uint>
    {
        243158, 243159, 243162, 243163, 243164, 243165, 243166, 243167,
        243168, 243169, 243170, 243171, 243172, 243173, 243174, 243175,
        243195, 243196, 243197, 243200, 243201,
    };
    private static readonly IReadOnlyDictionary<uint, (int HealPercent, bool IsAsh)> RecoveryItems =
        new Dictionary<uint, (int HealPercent, bool IsAsh)>
        {
            [243136] = (10, false),
            [243137] = (23, false),
            [243138] = (36, false),
            [243139] = (50, false),
            [243140] = (10, true),
            [243141] = (25, true),
            [243142] = (40, true),
        };
    private static readonly IReadOnlyDictionary<uint, int> BeastGearPriority =
        new Dictionary<uint, int>
        {
            // S tier: 1.
            [243042] = 1, // Deepdrowned Axe
            [243041] = 1, // Earthcrushed Axe
            [243038] = 1, // Flame-wreathed Axe
            [243057] = 1, // Thief's Knife
            [243027] = 1, // Hero's Crown
            [243039] = 1, // Icebitten Axe
            [243003] = 1, // Ring of Curing
            [243040] = 1, // Thunderstruck Axe
            [243043] = 1, // Windblown Axe
            [243024] = 1, // Master Shield
            [243025] = 1, // Mystic Veil
            [243047] = 1, // Wind Shield
            [243030] = 1, // Briar Armor

            // A tier: 2.
            [243035] = 2, // Astral Mantle
            [243065] = 2, // Flame Knife
            [243021] = 2, // Genji Armor
            [243019] = 2, // Genji Gloves
            [243011] = 2, // Green Beret
            [243028] = 2, // Mirage Vest
            [243044] = 2, // Ninja Suit
            [243013] = 2, // Ring of Protection
            [243008] = 2, // Staff of the Wise
            [243012] = 2, // Mystic Boots
            [243009] = 2, // Power Armlet
            [243058] = 2, // Thief's Garb
            [243055] = 2, // Beastly Knife
            [243070] = 2, // Spirited Ring
            [243032] = 2, // Umbral Mantle
            [243049] = 2, // Wind Armor
            [243062] = 2, // Thunder Armor
            [243015] = 2, // Earth Shield

            // B tier: 3.
            [243036] = 3, // Astral Band
            [243017] = 3, // Force Shield
            [243031] = 3, // Coward's Knife
            [243056] = 3, // Beast Earring
            [243074] = 3, // Haste Belt
            [243054] = 3, // Beast Mask
            [243004] = 3, // Chemist's Satchel
            [243063] = 3, // Warded Shield
            [243016] = 3, // Water Shield
            [243029] = 3, // Soulreaper's Armor
            [243037] = 3, // Astral Wristlet
            [243020] = 3, // Genji Greatshield
            [243006] = 3, // Steel Armor
            [243051] = 3, // Mythril Armor
            [243034] = 3, // Umbral Wristlet
            [243033] = 3, // Umbral Band
            [243001] = 3, // Belt of Constitution
            [243061] = 3, // Thunder Axe
            [243067] = 3, // Merchant's Cap
            [243068] = 3, // Merchant's Garb
            [243069] = 3, // Merchant's Shoes
            [243060] = 3, // Thief's Boots
            [243045] = 3, // Ninja Gloves
            [243023] = 3, // Angel Robe
            [243052] = 3, // Mythril Gloves
            [243053] = 3, // Mythril Greaves
            [243010] = 3, // Twisted Headband
            [243059] = 3, // Thief's Gloves
            [243066] = 3, // Flame Shield
            [243005] = 3, // Hexed Hat
            [243046] = 3, // Ninja Eyepatch
            [243022] = 3, // Silver Specs
            [243026] = 3, // Black Cowl
            [243064] = 3, // Gold Hairpin
            [243073] = 3, // Empress Hairpin
            [243075] = 3, // Warrior's Buckler
            [243048] = 3, // Wind Mask
            [243018] = 3, // Crimson Ribbon

            // C tier: 4.
            [243071] = 4, // Demonic Helm
            [243072] = 4, // Demonic Armor
            [243050] = 4, // Heavy Axe
            [243007] = 4, // Crown of the Wild

            // D tier: 5.
            [243014] = 5, // Ice Shield
            [243002] = 5, // Ring of Sacrifice
        };

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;

    public WindowSystem WindowSystem { get; } = new("UnbrokenHelper");

    internal uint ClientTerritoryId => ClientState.TerritoryType;
    internal bool IsTerritoryLoaded => ClientState.IsLoggedIn;
    internal bool IsPlayerLoaded => PlayerState.IsLoaded;
    internal int ObjectCount => ObjectTable.Count();
    internal uint PlayerJobId => PlayerState.ClassJob.RowId;
    internal Vector3 PlayerPosition => ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
    internal string TargetName => TargetManager.Target?.Name.ToString() ?? "None";
    internal ulong PlayerObjectId => ObjectTable.LocalPlayer?.GameObjectId ?? 0;
    internal int DebugActionId { get; set; } = 46961;
    internal int DebugActionTypeIndex { get; set; }
    internal IReadOnlyList<BattleNpcActionTrackerEntry> BattleNpcActionTrackerEntries
        => battleNpcActionTracker.Values.ToArray();
    internal DateTime? BattleNpcActionTrackerCapturedAt { get; private set; }
    internal void LogXBMPetRow(int rowId)
    {
        RawRow row;
        try
        {
            var sheet = DataManager.GetExcelSheet<RawRow>(name: "XBMPet");
            if (rowId < 0 || !sheet.TryGetRow((uint)rowId, out row))
            {
                Log.Warning($"[Debug] op=xbmpet-row row={rowId} found=false");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Debug] op=xbmpet-row row={rowId} error=sheet-load-failed");
            return;
        }

        Log.Information($"[Debug] op=xbmpet-row row={rowId} found=true");
        try
        {
            var familiarId = Convert.ToUInt32(row.ReadColumn(4));
            var familiarNumber = familiarId is >= 242001 and <= 242050 ? (int)(familiarId - 242000) : -1;
            var familiarName = familiarNumber > 0 ? FamiliarName(familiarNumber) : "unknown";
            Log.Information($"[Debug] op=xbmpet-familiar row={rowId} familiarId={familiarId} " +
                $"familiarNumber={familiarNumber} familiarName=\"{familiarName}\"");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Debug] op=xbmpet-familiar row={rowId} error={ex.GetType().Name}");
        }

        for (var columnIndex = 0; columnIndex < row.Columns.Count; columnIndex++)
        {
            try
            {
                var column = row.Columns[columnIndex];
                Log.Information($"[Debug] op=xbmpet-column row={rowId} index={columnIndex} type={column.Type} " +
                    $"offset={column.Offset} value={FormatSheetValue(row.ReadColumn(columnIndex))}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Debug] op=xbmpet-column row={rowId} index={columnIndex} error={ex.GetType().Name}");
            }
        }
    }

    internal void LogXBMItemRow(int rowId)
    {
        RawRow row;
        try
        {
            var sheet = DataManager.GetExcelSheet<RawRow>(name: "XBMItem");
            if (rowId < 0 || !sheet.TryGetRow((uint)rowId, out row))
            {
                Log.Warning($"[Debug] op=xbmitem-row row={rowId} found=false");
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Debug] op=xbmitem-row row={rowId} error=sheet-load-failed");
            return;
        }

        Log.Information($"[Debug] op=xbmitem-row row={rowId} found=true");
        LogRawXBMItemColumns(row, (uint)rowId);
    }

    internal void LogXBMItems()
    {
        try
        {
            var sheet = DataManager.GetExcelSheet<RawRow>(name: "XBMItem");
            var dumped = 0;
            foreach (var row in sheet)
            {
                LogRawXBMItemColumns(row, (uint)row.RowId);
                dumped++;
            }

            Log.Information($"[Debug] op=xbmitem-dump-result dumped={dumped}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Debug] op=xbmitem-dump-result error=sheet-load-failed");
        }
    }

    private void LogRawXBMItemColumns(RawRow row, uint rowId)
    {
        for (var columnIndex = 0; columnIndex < row.Columns.Count; columnIndex++)
        {
            try
            {
                var value = row.ReadColumn(columnIndex);
                if (value is string text && string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                Log.Information($"[Debug] op=xbmitem-column row={rowId} index={columnIndex} " +
                    $"type={row.Columns[columnIndex].Type} offset={row.Columns[columnIndex].Offset} value={FormatSheetValue(value)}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Debug] op=xbmitem-column row={rowId} index={columnIndex} error={ex.GetType().Name}");
            }
        }
    }

    private static string FormatSheetValue(object value)
    {
        if (value is string text)
        {
            return $"\"{text.Replace("\"", "\\\"")}\"";
        }
        return value.ToString()?.Replace("\r", "\\r").Replace("\n", "\\n") ?? "null";
    }
    private bool IsRouteDrivenBoard => selectedBoard is 2 or 3 or 4;
    private uint BoardTerritory => selectedBoard switch
    {
        1 => board1Definition?.TerritoryId ?? 1339u,
        2 => board2Definition?.TerritoryId ?? 1340u,
        3 => board3Definition?.TerritoryId ?? 1341u,
        4 => firstMastersBoardDefinition?.TerritoryId ?? 1342u,
        _ => 0u,
    };
    private Vector3 CurrentStageStartPosition => selectedBoard == 1
        ? board1RouteNode?.Position ?? Vector3.Zero
        : CurrentRouteNode?.Position ?? new Vector3(-700.000f, 0f, -8.318f);
    private Vector3 SecondBoardShopPosition => new(-700.003f, 0f, -18.000f);
    private Vector3 Stage9ShopPosition => new(-700.271f, 0f, -74.179f);
    private List<int> ConfiguredFamiliarNumbers => GetMutableFamiliarRoster(selectedBoard);
    private List<int> ConfiguredStageFamiliarNumbers => GetMutableStageFamiliarRoster(
        selectedBoard, CurrentRouteNode?.FamiliarStageNumber ?? 0, CurrentRouteNode?.Variant ?? string.Empty)
        .Where(number => number > 0)
        .ToList();

    private readonly PluginConfiguration configuration;
    private readonly FamiliarCombatCatalog familiarCombatCatalog;
    private readonly BoardRouteBoard? board1Definition;
    private readonly BoardRouteBoard? board2Definition;
    private readonly BoardRouteBoard? board3Definition;
    private readonly BoardRouteBoard? firstMastersBoardDefinition;
    private RouteNode? board1RouteNode;
    private IReadOnlyDictionary<RoutePhase, RouteNode> board2RouteNodes = new Dictionary<RoutePhase, RouteNode>();
    private IReadOnlyDictionary<RoutePhase, RouteNode> board3ARouteNodes = new Dictionary<RoutePhase, RouteNode>();
    private IReadOnlyDictionary<RoutePhase, RouteNode> board3BRouteNodes = new Dictionary<RoutePhase, RouteNode>();
    private IReadOnlyDictionary<RoutePhase, RouteNode> firstMastersRouteNodes = new Dictionary<RoutePhase, RouteNode>();
    private BoardRouteCatalog routeCatalog = new();
    private nint stageDetailModeAddon;
    private int stageDetailModeAttempts;
    private DateTime stageDetailModeLastAction;
    private readonly MainWindow mainWindow;
    private readonly DebugWindow debugWindow;
    private readonly ICallGateSubscriber<bool> vnavIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> vnavPathfindAndMoveTo;
    private readonly ICallGateSubscriber<object> vnavStop;
    private readonly ICallGateSubscriber<string, bool> bossModSetActive;
    private readonly ICallGateSubscriber<bool> bossModClearActive;
    private readonly ICallGateSubscriber<string, bool, bool> bossModCreatePreset;
    private readonly ICallGateSubscriber<string, bool> bossModDeletePreset;
    private readonly ICallGateSubscriber<string> bossModGetActivePreset;
    private readonly ICallGateSubscriber<byte, object> rsrChangeMode;
    private readonly DalamudPluginToggleHelper bossModToggleHelper;
    private string? bossModPreviousActivePreset;
    private bool bossModPreviousActivePresetCaptured;
    private bool bossModPresetSessionStarted;
    private bool bossModPresetCreated;
    private bool bossModPresetActive;
    private Task? bossModRecoveryTask;
    private string? bossModRecoveryPluginName;
    private DateTime bossModRecoveryDeadline = DateTime.MinValue;
    private DateTime bossModRecoveryNextAttempt = DateTime.MinValue;

    private enum RouteNodeKind
    {
        Battle,
        Rest,
        Treasure,
        Shop,
        Random,
    }

    private enum RandomOutcomeKind
    {
        None,
        Battle,
        Treasure,
    }

    private enum Board3RouteVariant
    {
        A,
        B,
    }

    private enum RoutePhase
    {
        None,
        Stage1,
        Stage2,
        Shop2,
        Shop3,
        Stage3B,
        Rest4B,
        Stage4A,
        Stage4B,
        Rest5B,
        Stage5,
        Treasure6,
        Stage6,
        Stage7,
        Treasure8,
        Treasure7A,
        Stage8,
        Shop9,
        Treasure9,
        Rest10B,
        Stage10,
        Shop11,
        Stage12,
        FirstMastersMove1,
        FirstMastersMove2B,
        FirstMastersMove3,
        FirstMastersMove4A,
        FirstMastersMove5,
        FirstMastersMove6B,
        FirstMastersMove7B,
        FirstMastersMove8B,
        FirstMastersMove9,
    }

    private sealed class RouteNode
    {
        internal RoutePhase Phase { get; init; }
        internal RouteNodeKind Kind { get; init; }
        internal int StageNumber { get; init; }
        internal string Variant { get; init; } = string.Empty;
        internal string Name { get; init; } = string.Empty;
        internal Vector3 Position { get; init; }
        internal string ExpectedAddon { get; init; } = string.Empty;
        internal RoutePhase NextPhase { get; init; }
        internal int RecoveryThreshold { get; init; } = 60;
        internal int RecoveryTarget { get; init; } = 90;
        internal int FamiliarStageNumber { get; init; }
        internal string FamiliarRosterKey { get; init; } = string.Empty;
        internal bool DirectResultFlow { get; init; }
        internal uint[] CombatTargetBaseIds { get; init; } = Array.Empty<uint>();
        internal uint PriorityTargetBaseId { get; init; }
        internal ActionTrigger[] ActionTriggers { get; init; } = Array.Empty<ActionTrigger>();

        internal string Key => Variant.Length == 0 ? $"Move{StageNumber}" : $"Move{StageNumber}{Variant}";
    }

    private sealed class ActionTrigger
    {
        internal uint SourceBaseId { get; init; }
        internal uint ActionId { get; init; }
        internal string ActionName { get; init; } = string.Empty;
        internal ActionTriggerResponse Response { get; init; }
        internal uint ResponseActionId { get; init; }
        internal string ResponseActionName { get; init; } = string.Empty;
        internal uint ResponseTargetBaseId { get; init; }
    }

    private enum ActionTriggerResponse
    {
        Snarl,
        MoveToPetPosition,
    }

    private sealed class ActionTriggerMovementSession
    {
        internal (ulong ObjectId, uint ActionId) TriggerKey { get; init; }
        internal Vector3 Destination { get; init; }
        internal DateTime StartedAt { get; init; }
    }

    private RouteNode? CurrentRouteNode
        => selectedBoard == 1
            ? board1RouteNode
            : ActiveRouteNodes.GetValueOrDefault(routePhase);

    private IReadOnlyDictionary<RoutePhase, RouteNode> ActiveRouteNodes
        => selectedBoard switch
        {
            2 => board2RouteNodes,
            3 => selectedBoard3Route == Board3RouteVariant.A ? board3ARouteNodes : board3BRouteNodes,
            4 => firstMastersRouteNodes,
            _ => new Dictionary<RoutePhase, RouteNode>(),
        };
    private RoutePhase InitialRoutePhase => selectedBoard == 4 ? RoutePhase.FirstMastersMove1 : RoutePhase.Stage1;

    private EntryState entryState;
    private RandomOutcomeKind randomOutcome;
    private DateTime entryStateStarted;
    private DateTime nextEntryAction;
    private bool vnavMoveStarted
    {
        get => movement.VnavStarted;
        set => movement.VnavStarted = value;
    }
    private DateTime? movementConditionsClearSince
    {
        get => movement.ConditionsClearSince;
        set => movement.ConditionsClearSince = value;
    }
    private DateTime? laudaReadySince;
    private int selectedBoard = 1;
    private Board3RouteVariant selectedBoard3Route = Board3RouteVariant.B;
    private bool loopEntry;
    private bool stopAfterCurrentLoop;
    private RoutePhase routePhase;
    private RoutePhase recoveryNoUsableItemsRoute;
    private int recoveryNoUsableItemsThreshold;
    private readonly TreasureSession treasure = new();
    private readonly PetPartySession petParty = new();
    private readonly ItemUseSession itemUse = new();
    private readonly HashSet<uint> usedAttackItemIds = new();
    private readonly HashSet<uint> failedItemUseIds = new();
    private readonly HashSet<ulong> snarledSahaginTargets = new();
    private readonly HashSet<(ulong ObjectId, uint ActionId)> handledActionTriggers = new();
    private ActionTriggerMovementSession? actionTriggerMovement;
    private readonly Dictionary<ulong, BattleNpcActionTrackerEntry> battleNpcActionTracker = new();
    private bool stage4APulletWaveObserved;
    private bool stage4APulletReturnStarted;
    private bool preStageItemsHandled;
    private bool sandstillArmed;
    private bool sandstillRestartHandled;
    private PotionDebugState potionDebugState;
    private DateTime potionDebugStateStarted;

    private enum PotionDebugState
    {
        Idle,
        AwaitMainHud,
        AwaitContextMenu,
    }

    private sealed class RecoverySession
    {
        internal EntryState Continuation { get; set; } = EntryState.Idle;
        internal DateTime LastWaitLog { get; set; }
        internal DateTime StartedAt { get; set; }
        internal int Attempt { get; set; }
        internal int SelectedSlot { get; set; } = -1;
        internal uint SelectedItemId { get; set; }
        internal DateTime AttemptStarted { get; set; }
        internal DateTime LastCheckLog { get; set; }
        internal DateTime LastCombatRecoveryCheck { get; set; }
        internal int ThresholdPercent { get; set; } = 60;
        internal int TargetPercent { get; set; } = 90;
        internal bool RsrDisabled { get; set; }
        internal Dictionary<uint, int> ItemAttempts { get; } = new();

        internal bool Active => Continuation != EntryState.Idle;

        internal void Clear()
        {
            Continuation = EntryState.Idle;
            LastWaitLog = DateTime.MinValue;
            StartedAt = DateTime.MinValue;
            Attempt = 0;
            SelectedSlot = -1;
            SelectedItemId = 0;
            AttemptStarted = DateTime.MinValue;
            LastCheckLog = DateTime.MinValue;
            LastCombatRecoveryCheck = DateTime.MinValue;
            ThresholdPercent = 60;
            TargetPercent = 90;
            RsrDisabled = false;
            ItemAttempts.Clear();
        }
    }

    private enum ItemUseTiming
    {
        BoardPreStage,
        InStagePreBattle,
        Combat,
    }

    private sealed class ItemUseSession
    {
        internal bool Running { get; set; }
        internal ItemUseTiming Timing { get; set; }
        internal int Slot { get; set; } = -1;
        internal uint ItemId { get; set; }
        internal string ItemName { get; set; } = string.Empty;
        internal DateTime AttemptStarted { get; set; }
        internal int Attempts { get; set; }
        internal bool HudSelectionDispatched { get; set; }
        internal bool ContextMenuDispatched { get; set; }
        internal bool RsrDisabled { get; set; }

        internal bool Active => Running;

        internal void Clear()
        {
            Running = false;
            Timing = ItemUseTiming.Combat;
            Slot = -1;
            ItemId = 0;
            ItemName = string.Empty;
            AttemptStarted = DateTime.MinValue;
            Attempts = 0;
            HudSelectionDispatched = false;
            ContextMenuDispatched = false;
            RsrDisabled = false;
        }
    }

    private enum MovementResult
    {
        Waiting,
        Moving,
        Arrived,
    }

    private sealed class MovementSession
    {
        internal bool VnavStarted { get; set; }
        internal bool TransitionObserved { get; set; }
        internal bool BoardReady { get; set; }
        internal DateTime? ConditionsClearSince { get; set; }
        internal DateTime? BoardConditionsClearSince { get; set; }

        internal void Clear()
        {
            VnavStarted = false;
            TransitionObserved = false;
            BoardReady = false;
            ConditionsClearSince = null;
            BoardConditionsClearSince = null;
        }
    }

    private readonly MovementSession movement = new();

    private sealed class TreasureSession
    {
        internal int Attempt { get; set; }
        internal DateTime OptionRequestedAt { get; set; }
        internal EntryState SelectionState { get; set; } = EntryState.Idle;
        internal int RequiredSelections { get; set; } = 1;
        internal int CompletedSelections { get; set; }
        internal int SelectedOptionIndex { get; set; } = -1;
        internal uint SelectedItemId { get; set; }
        internal int DataPollCount { get; set; }
        internal int StableDataPolls { get; set; }
        internal DateTime DataFirstObservedAt { get; set; }
        internal string LastDataSignature { get; set; } = string.Empty;
        internal bool PostConfirmationRescanPending { get; set; }
        internal bool PostConfirmationRescanActive { get; set; }
        internal HashSet<int> UsedOptionIndexes { get; } = new();

        internal void Clear()
        {
            Attempt = 0;
            OptionRequestedAt = DateTime.MinValue;
            SelectionState = EntryState.Idle;
            RequiredSelections = 1;
            CompletedSelections = 0;
            SelectedOptionIndex = -1;
            SelectedItemId = 0;
            DataPollCount = 0;
            StableDataPolls = 0;
            DataFirstObservedAt = DateTime.MinValue;
            LastDataSignature = string.Empty;
            PostConfirmationRescanPending = false;
            PostConfirmationRescanActive = false;
            UsedOptionIndexes.Clear();
        }
    }

    private sealed class TreasureDataSnapshot
    {
        internal List<TreasureCardInfo> Cards { get; init; } = new();
        internal List<int> KnifeValueIndexes { get; init; } = new();

        internal bool CardsReady => Cards.Count >= 4 && Cards.All(card =>
            card.OptionIndex >= 0 && card.RowId != 0 && card.ItemId != 0 &&
            !string.IsNullOrWhiteSpace(card.Title));

        internal string Signature =>
            $"knife={string.Join(",", KnifeValueIndexes)};cards=" +
            string.Join("|", Cards.OrderBy(card => card.OptionIndex).Select(card =>
                $"{card.OptionIndex}:{card.RowId}:{card.ItemId}:{card.Acquired}"));
    }

    private sealed class TreasureNodeTextInfo
    {
        internal string Text { get; set; } = string.Empty;
        internal bool Visible { get; set; }
        internal float ScreenX { get; set; }
        internal float ScreenY { get; set; }
    }

    private sealed class TreasureCardInfo
    {
        internal int OptionIndex { get; set; } = -1;
        internal float ScreenX { get; set; }
        internal float ScreenY { get; set; }
        internal string Title { get; set; } = string.Empty;
        internal string Effect { get; set; } = string.Empty;
        internal bool Acquired { get; set; }
        internal bool HasXbmItem { get; set; }
        internal uint RowId { get; set; }
        internal uint ItemId { get; set; }
        internal List<TreasureNodeTextInfo> TextNodes { get; } = new();
    }

    private sealed class XbmItemRecord
    {
        internal int AtkIndex { get; set; }
        internal uint RowId { get; set; }
        internal uint ItemId { get; set; }
        internal string Name { get; set; } = string.Empty;
    }

    private sealed class XbmHudItemRecord
    {
        internal int Slot { get; init; }
        internal uint RowId { get; init; }
        internal uint ItemId { get; init; }
        internal string Name { get; init; } = string.Empty;
        internal bool Enabled { get; init; }
        internal bool HudUsable { get; init; }
    }

    private sealed class PetPartyRecord
    {
        internal int RosterIndex { get; set; }
        internal int FamiliarNumber { get; set; }
        internal bool Selected { get; set; }
        internal uint CurrentHp { get; set; }
        internal uint MaximumHp { get; set; }
        internal string Name { get; set; } = string.Empty;

        internal int HealthPercent => MaximumHp == 0
            ? 0
            : (int)Math.Clamp((ulong)CurrentHp * 100 / MaximumHp, 0, 100);

        internal bool IsEligible(int thresholdPercent)
            => MaximumHp > 0 && CurrentHp > 0 &&
                (ulong)CurrentHp * 100 >= (ulong)MaximumHp * (uint)thresholdPercent;
    }

    private sealed class PetPartySession
    {
        internal int StageToggleIndex { get; set; }
        internal List<int>? StageToggleNumbers { get; set; }
        internal List<int>? StageDesiredNumbers { get; set; }
        internal int StageSelectionRetryCount { get; set; }
        internal int NotebookPage { get; set; } = -1;
        internal int NotebookPhase { get; set; }
        internal List<int> PendingFamiliarNumbers { get; } = new();
        internal HashSet<int> FamiliarRemovalNumbers { get; } = new();
        internal HashSet<int> FamiliarAdditionNumbers { get; } = new();
        internal HashSet<int> NotebookResolvedNumbers { get; } = new();
        internal int NotebookToggleIndex { get; set; }
        internal DateTime NotebookLastAction { get; set; }

        internal void Clear()
        {
            StageToggleIndex = 0;
            StageToggleNumbers = null;
            StageDesiredNumbers = null;
            StageSelectionRetryCount = 0;
            NotebookPage = -1;
            NotebookPhase = 0;
            PendingFamiliarNumbers.Clear();
            FamiliarRemovalNumbers.Clear();
            FamiliarAdditionNumbers.Clear();
            NotebookResolvedNumbers.Clear();
            NotebookToggleIndex = 0;
            NotebookLastAction = DateTime.MinValue;
        }
    }

    private readonly RecoverySession recovery = new();
    private bool knightSetupComplete;
    private bool knightRearMoveStarted;
    private bool boardChallengeConfirmed;
    private bool boardMovementTransitionObserved
    {
        get => movement.TransitionObserved;
        set => movement.TransitionObserved = value;
    }
    private DateTime? boardMovementClearSince
    {
        get => movement.BoardConditionsClearSince;
        set => movement.BoardConditionsClearSince = value;
    }
    private bool boardMovementReady
    {
        get => movement.BoardReady;
        set => movement.BoardReady = value;
    }
    private Vector3 knightRearDestination;

    private enum EntryState
    {
        Idle,
        AwaitLaudaReady,
        TargetLauda,
        AwaitSelectIconString,
        AwaitSelectString,
        AwaitStageList,
        AwaitStageDetail,
        AwaitFirstBoardTerritory,
        MoveToStageStart,
        AwaitPreChallengeRoster,
        AwaitStagePetParty,
        SelectStagePetParty,
        OpenFamiliarNotebook,
        AwaitFamiliarNotebook,
        SelectFamiliarNotebook,
        AwaitFamiliarRosterUpdate,
        AwaitInInstanceStageDetail,
        CommenceInInstanceStage,
        AwaitStageLoaded,
        PrepareCombat,
        AwaitBattlehorn,
        ActivateBossMod,
        PrepareKnight,
        AwaitKnightAction,
        MoveBehindKnight,
        MoveToStage4APriorityReturn,
        Combat,
        AwaitSandstillRestart,
        AwaitContentsBotty,
        ExitContentsBotty,
        AwaitExitConfirmation,
        ConfirmExit,
        AwaitXbmResult,
        ExitXbmResult,
        AwaitNeedGreed,
        RollNeedGreed,
        ConfirmXbmResultExit,
        AwaitCentralShroud,
        AwaitRecoveryMainHud,
        AwaitRecoveryContextMenu,
        AwaitRecoveryHp,
        MoveToStage4BRest,
        AwaitStage4BTent,
        RestStage4B,
        AwaitStage4BConfirmation,
        MoveToRandomOutcome,
        AwaitRandomOutcome,
        MoveToStage6Start,
        MoveToStage10Start,
        AwaitStage6Treasure,
        SelectStage6Treasure,
        AwaitStage6TreasureConfirmation,
        AwaitStage8Treasure,
        MoveToStage8Start,
        SelectStage8Treasure,
        AwaitStage8TreasureConfirmation,
        MoveToSecondBoardShop,
        AwaitBoardExitTransitionStart,
        AwaitBoardExitTransitionReady,
        AwaitSecondBoardShop,
        CloseSecondBoardShop,
        AwaitSecondBoardExitConfirmation,
        ConfirmSecondBoardExit,
        AwaitSecondBoardMainHud,
        ExitSecondBoardMainHud,
        AwaitSecondBoardFinalConfirmation,
        ConfirmSecondBoardFinalExit,
        AwaitInstanceLeave,
        Complete,
        Failed,
    }

    internal string EntryStateText => entryState.ToString();
    internal bool EntryActive => entryState is not (EntryState.Idle or EntryState.Complete or EntryState.Failed);
    internal bool LoopAfterCompletion => loopEntry;
    internal bool StopAfterCurrentLoopPending => stopAfterCurrentLoop;

    internal void RequestStopAfterCurrentLoop()
    {
        if (EntryActive && loopEntry)
        {
            stopAfterCurrentLoop = true;
            Log.Information("[Entry] pending stop requested; automation will stop at Central Shroud.");
        }
    }

    internal void CancelStopAfterCurrentLoop()
    {
        if (EntryActive && loopEntry && stopAfterCurrentLoop)
        {
            stopAfterCurrentLoop = false;
            Log.Information("[Entry] pending stop cancelled; automation will continue looping.");
        }
    }

    internal string EntryTarget => entryState switch
    {
        EntryState.TargetLauda => "Lauda (BaseId 1059316)",
        EntryState.AwaitLaudaReady => "Lauda ready and targetable",
        EntryState.AwaitSelectIconString => "SelectIconString or SelectString",
        EntryState.AwaitSelectString => "SelectString",
        EntryState.AwaitStageList => "XBMStageList",
        EntryState.AwaitStageDetail => "XBMStageDetailList",
        EntryState.AwaitFirstBoardTerritory => $"Territory {BoardTerritory}",
        EntryState.MoveToStageStart => $"VNav {CurrentRouteNode?.Name ?? "move start"}",
        EntryState.AwaitPreChallengeRoster => "XBMPetParty pre-challenge roster",
        EntryState.AwaitStagePetParty => $"XBMPetParty {CurrentRouteNode?.Name ?? "battle"} roster",
        EntryState.SelectStagePetParty => $"XBMPetParty move familiar index {petParty.StageToggleIndex}",
        EntryState.OpenFamiliarNotebook => "XBMPetParty callback 5",
        EntryState.AwaitFamiliarNotebook => "XBMMonsterNotebook",
        EntryState.SelectFamiliarNotebook => "XBMMonsterNotebook reconciliation",
        EntryState.AwaitFamiliarRosterUpdate => "XBMPetParty pre-challenge roster update",
        EntryState.AwaitInInstanceStageDetail => $"XBMStageDetailList {CurrentRouteNode?.Name ?? "battle"}",
        EntryState.CommenceInInstanceStage => "XBMStageDetailList callback 8",
        EntryState.AwaitStageLoaded => "Move conditions clear",
        EntryState.PrepareCombat => "Battlehorn 44881",
        EntryState.AwaitBattlehorn => "Battlehorn resolution",
        EntryState.ActivateBossMod => $"BossMod {BossModPresetName}",
        EntryState.PrepareKnight => "Bone Knight action 46751",
        EntryState.AwaitKnightAction => "Bone Knight action resolution",
        EntryState.MoveBehindKnight => "VNav rear position",
        EntryState.Combat => GetCombatEntryTarget(),
        EntryState.AwaitSandstillRestart => $"Sandstill restart target for {CurrentRouteNode?.Name ?? "move"}",
        EntryState.AwaitContentsBotty => "XBMContentsBooty",
        EntryState.ExitContentsBotty => "XBMContentsBooty callback 1",
        EntryState.AwaitExitConfirmation => "SelectYesno",
        EntryState.ConfirmExit => "SelectYesno callback 0",
        EntryState.AwaitXbmResult => "XBMResult",
        EntryState.ExitXbmResult => "XBMResult callback 0",
        EntryState.AwaitNeedGreed => "NeedGreed",
        EntryState.RollNeedGreed => "NeedGreed callback -2",
        EntryState.ConfirmXbmResultExit => "XBMResult callback -1",
        EntryState.AwaitCentralShroud => "Central Shroud territory 148",
        EntryState.AwaitRecoveryMainHud => "XBMContentsMainHUD recovery item",
        EntryState.AwaitRecoveryContextMenu => "ContextMenu recovery item",
        EntryState.AwaitRecoveryHp => "recovery item consumption",
        EntryState.MoveToStage4APriorityReturn => "VNav Move 4A priority return",
        EntryState.MoveToStage4BRest => $"VNav {CurrentRouteNode?.Name ?? "rest"}",
        EntryState.AwaitStage4BTent => $"XBMPetParty {CurrentRouteNode?.Name ?? "rest"}",
        EntryState.RestStage4B => $"XBMPetParty {CurrentRouteNode?.Name ?? "rest"} callback 3",
        EntryState.AwaitStage4BConfirmation => $"SelectYesno after {CurrentRouteNode?.Name ?? "rest"}",
        EntryState.MoveToRandomOutcome => $"VNav {CurrentRouteNode?.Name ?? "random"} spot",
        EntryState.AwaitRandomOutcome => $"random outcome after {CurrentRouteNode?.Name ?? "random"}",
        EntryState.MoveToStage6Start => $"VNav {CurrentRouteNode?.Name ?? "treasure"} spot",
        EntryState.MoveToStage10Start => $"VNav {CurrentRouteNode?.Name ?? "move"} start",
        EntryState.AwaitStage6Treasure => $"XBMContentsTreasure {CurrentRouteNode?.Name ?? "treasure"}",
        EntryState.SelectStage6Treasure => "XBMContentsTreasure selection",
        EntryState.AwaitStage6TreasureConfirmation => "SelectYesno after Move 6 treasure",
        EntryState.AwaitStage8Treasure => $"XBMContentsTreasure {CurrentRouteNode?.Name ?? "treasure"}",
        EntryState.MoveToStage8Start => $"VNav {CurrentRouteNode?.Name ?? "treasure"} spot",
        EntryState.SelectStage8Treasure => "XBMContentsTreasure selection",
        EntryState.AwaitStage8TreasureConfirmation => "SelectYesno after Move 8 treasure",
        EntryState.MoveToSecondBoardShop => $"VNav {CurrentRouteNode?.Name ?? "shop"}",
        EntryState.AwaitBoardExitTransitionStart => "route exit transition",
        EntryState.AwaitBoardExitTransitionReady => "route map ready",
        EntryState.AwaitSecondBoardShop => $"XBMContentsItemShop {CurrentRouteNode?.Name ?? "shop"}",
        EntryState.CloseSecondBoardShop => $"XBMContentsItemShop {CurrentRouteNode?.Name ?? "shop"} callback 0",
        EntryState.AwaitSecondBoardExitConfirmation => "SelectYesno after shop",
        EntryState.ConfirmSecondBoardExit => "SelectYesno callback 0",
        EntryState.AwaitSecondBoardMainHud => "XBMContentsMainHUD",
        EntryState.ExitSecondBoardMainHud => "XBMContentsMainHUD callback 3",
        EntryState.AwaitSecondBoardFinalConfirmation => "SelectYesno after contents exit",
        EntryState.ConfirmSecondBoardFinalExit => "SelectYesno callback 0",
        EntryState.AwaitInstanceLeave => "Leave board instance",
        _ => "None",
    };

    public Plugin()
    {
        ECommonsMain.Init(PluginInterface, this);
        configuration = PluginInterface.GetPluginConfig() as PluginConfiguration ?? new PluginConfiguration();
        configuration.Normalize();
        familiarCombatCatalog = new FamiliarCombatCatalogLoader(Log).Load(PluginInterface);
        routeCatalog = new BoardRouteCatalogLoader(Log).Load(PluginInterface);
        board1Definition = routeCatalog.FindBoard("board1");
        board2Definition = routeCatalog.FindBoard("board2");
        board3Definition = routeCatalog.FindBoard("board3");
        firstMastersBoardDefinition = routeCatalog.FindBoard("first-masters-board");
        ReloadConfiguredRoutes();
        if (board1RouteNode == null || board2RouteNodes.Count == 0 ||
            board3ARouteNodes.Count == 0 || board3BRouteNodes.Count == 0)
        {
            Log.Error("[Data] Board route catalog did not provide all active Board 1, Board 2 B, and Board 3 A/B routes; automation may be unavailable.");
        }
        vnavIsReady = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        vnavPathfindAndMoveTo = PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        vnavStop = PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        bossModSetActive = PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        bossModClearActive = PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
        bossModCreatePreset = PluginInterface.GetIpcSubscriber<string, bool, bool>("BossMod.Presets.Create");
        bossModDeletePreset = PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.Delete");
        bossModGetActivePreset = PluginInterface.GetIpcSubscriber<string>("BossMod.Presets.GetActive");
        rsrChangeMode = PluginInterface.GetIpcSubscriber<byte, object>("RotationSolverReborn.ChangeOperatingMode");
        bossModToggleHelper = new DalamudPluginToggleHelper(PluginInterface, PlayerState, Log);
        Framework.Update += OnFrameworkUpdate;
        mainWindow = new MainWindow(this);
        debugWindow = new DebugWindow(this);
        WindowSystem.AddWindow(mainWindow);
        WindowSystem.AddWindow(debugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Unbroken Helper debug window."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenMainWindow;

        Log.Information("Unbroken Helper loaded.");
    }

    internal IReadOnlyList<int> GetFamiliarRoster(int board)
        => GetMutableFamiliarRoster(board);

    internal int FamiliarRosterCapacity(int board) => board switch
    {
        1 => 10,
        2 => 12,
        3 => 14,
        4 => 12,
        _ => 0,
    };

    internal bool UnlockedFamiliarDataReady
    {
        get
        {
            var manager = XBMManager.Instance();
            return manager != null && manager->State == XBMManager.DataState.Received;
        }
    }

    internal int UnlockedFamiliarCount
    {
        get
        {
            var manager = XBMManager.Instance();
            return manager == null ? 0 : manager->NumUnlockedPets;
        }
    }

    internal bool IsFamiliarUnlocked(int number)
    {
        if (number is < 1 or > 50)
        {
            return false;
        }

        var manager = XBMManager.Instance();
        return manager != null && manager->State == XBMManager.DataState.Received &&
            manager->IsPetUnlocked((uint)number);
    }

    internal bool TryGetOpenMonsterNotebook(out IReadOnlyList<NotebookFamiliarEntry> entries)
    {
        var addon = GetReadyAddon("XBMMonsterNotebook");
        if (addon == null)
        {
            entries = Array.Empty<NotebookFamiliarEntry>();
            return false;
        }

        entries = ReadNotebookFamiliarEntries(addon);
        return true;
    }

    internal int PetHealthThresholdPercent => configuration.PetHealthThresholdPercent;

    internal void SetPetHealthThresholdPercent(int percent)
    {
        var normalized = Math.Clamp(percent, 1, 100);
        if (configuration.PetHealthThresholdPercent == normalized)
        {
            return;
        }

        configuration.PetHealthThresholdPercent = normalized;
        SaveConfiguration();
    }

    internal IReadOnlyList<int> GetStageFamiliarRoster(int board, int stage, string variant = "")
        => GetMutableStageFamiliarRoster(board, stage, variant);

    internal void AddStageFamiliar(int board, int stage, int number, string variant = "")
    {
        var roster = GetMutableStageRoster(board, stage, variant);
        var boardRoster = GetMutableFamiliarRoster(board);
        var slot = roster.FindIndex(assignedNumber => assignedNumber == 0);
        if (!boardRoster.Contains(number) || roster.Contains(number) || slot < 0)
        {
            return;
        }

        roster[slot] = number;
        SaveConfiguration();
    }

    internal void SetStageFamiliarSlot(int board, int stage, int slot, int number, string variant = "")
    {
        var roster = GetMutableStageRoster(board, stage, variant);
        var boardRoster = GetMutableFamiliarRoster(board);
        if (slot is < 0 or >= 3 || (number != 0 && !boardRoster.Contains(number)) ||
            (number != 0 && roster.Where((_, index) => index != slot).Contains(number)))
        {
            return;
        }

        while (roster.Count < 3)
        {
            roster.Add(0);
        }

        if (slot >= roster.Count)
        {
            return;
        }

        if (roster[slot] != number)
        {
            roster[slot] = number;
            SaveConfiguration();
        }
    }

    internal void RemoveStageFamiliar(int board, int stage, int number, string variant = "")
    {
        var roster = GetMutableStageRoster(board, stage, variant);
        var slot = roster.IndexOf(number);
        if (slot >= 0)
        {
            roster[slot] = 0;
            SaveConfiguration();
        }
    }

    private List<int> GetMutableStageRoster(int board, int stage, string variant = "")
        => GetMutableStageFamiliarRoster(board, stage, variant);

    private List<int> GetMutableFamiliarRoster(int board)
        => board switch
        {
            1 => configuration.Board1FamiliarNumbers,
            2 => configuration.Board2FamiliarNumbers,
            3 => configuration.Board3FamiliarNumbers,
            4 => configuration.FirstMastersBoardFamiliarNumbers,
            _ => new List<int>(),
        };

    private List<int> GetMutableStageFamiliarRoster(int board, int stage, string variant = "")
        => board switch
        {
            1 => configuration.Board1StageFamiliarNumbers,
            2 => stage switch
            {
                3 => configuration.Board2Stage3BFamiliarNumbers,
                5 => configuration.Board2Stage5FamiliarNumbers,
                7 => configuration.Board2Stage7FamiliarNumbers,
                10 => configuration.Board2Stage10FamiliarNumbers,
                _ => configuration.Board2Stage1FamiliarNumbers,
            },
            3 => stage switch
            {
                2 => configuration.Board3Stage2FamiliarNumbers,
                4 when string.Equals(variant, "A", StringComparison.OrdinalIgnoreCase) => configuration.Board3Stage4AFamiliarNumbers,
                4 => configuration.Board3Stage4BFamiliarNumbers,
                6 => configuration.Board3Stage6FamiliarNumbers,
                8 => configuration.Board3Stage8FamiliarNumbers,
                12 => configuration.Board3Stage12FamiliarNumbers,
                _ => configuration.Board3Stage1FamiliarNumbers,
            },
            4 => stage switch
            {
                2 => configuration.FirstMastersMove2BFamiliarNumbers,
                5 => configuration.FirstMastersMove5FamiliarNumbers,
                7 => configuration.FirstMastersMove7BFamiliarNumbers,
                9 => configuration.FirstMastersMove9FamiliarNumbers,
                _ => configuration.FirstMastersMove1FamiliarNumbers,
            },
            _ => new List<int>(),
        };

    internal string FamiliarName(int number) => number switch
    {
        1 => "Cu Sith", 2 => "Squirrel", 3 => "Sheep", 4 => "Pugil", 5 => "Opo-opo",
        6 => "Dodo", 7 => "Coblyn", 8 => "Diremite", 9 => "Megalocrab", 10 => "Wespe",
        11 => "Vulture", 12 => "Mandragora", 13 => "Geshunpest", 14 => "Puk", 15 => "Snipper",
        16 => "Mantis", 17 => "Slime", 18 => "Dullahan", 19 => "Bat", 20 => "Flying Trap",
        21 => "Ziz", 22 => "Cactuar", 23 => "Golem", 24 => "Apkallu", 25 => "Adamantoise",
        26 => "Buffalo", 27 => "Uragnite", 28 => "Worm", 29 => "Spriggan", 30 => "Goobbue",
        31 => "Gigantoad", 32 => "Colibri", 33 => "Coeurl", 34 => "Raptor", 35 => "Drake",
        36 => "Treant", 37 => "Antling", 38 => "Fell Chimera", 39 => "Morbol", 40 => "Ghost",
        41 => "Salamander", 42 => "Cobra", 43 => "Hydra", 44 => "Damselfly", 45 => "Rotting Goobbue",
        46 => "Zu", 47 => "Ice Golem", 48 => "Scorpion", 49 => "Rafflesia", 50 => "Behemoth",
        _ => $"Familiar No. {number}"
    };

    internal FamiliarCombatEntry? GetFamiliarCombatInfo(int number)
        => familiarCombatCatalog.Find(number);

    internal void AddFamiliarToRoster(int board, int number)
    {
        var roster = GetMutableFamiliarRoster(board);
        if (number is < 1 or > 50 || roster.Contains(number) || roster.Count >= FamiliarRosterCapacity(board))
        {
            return;
        }

        roster.Add(number);
        SaveConfiguration();
    }

    internal void RemoveFamiliarFromRoster(int board, int number)
    {
        var roster = GetMutableFamiliarRoster(board);
        var changed = roster.Remove(number);
        foreach (var stageRoster in GetStageRosters(board))
        {
            for (var slot = 0; slot < stageRoster.Count; slot++)
            {
                if (stageRoster[slot] == number)
                {
                    stageRoster[slot] = 0;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            SaveConfiguration();
        }
    }

    internal void ClearFamiliarRoster(int board)
    {
        var changed = GetMutableFamiliarRoster(board).Count > 0;
        GetMutableFamiliarRoster(board).Clear();
        foreach (var stageRoster in GetStageRosters(board))
        {
            for (var slot = 0; slot < stageRoster.Count; slot++)
            {
                if (stageRoster[slot] != 0)
                {
                    stageRoster[slot] = 0;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            SaveConfiguration();
        }
    }

    private IEnumerable<List<int>> GetStageRosters(int board)
        => board switch
        {
            1 => new[] { configuration.Board1StageFamiliarNumbers },
            2 => new[]
            {
                configuration.Board2Stage1FamiliarNumbers,
                configuration.Board2Stage3BFamiliarNumbers,
                configuration.Board2Stage5FamiliarNumbers,
                configuration.Board2Stage7FamiliarNumbers,
                configuration.Board2Stage10FamiliarNumbers,
            },
            3 => new[]
            {
                configuration.Board3Stage1FamiliarNumbers,
                configuration.Board3Stage2FamiliarNumbers,
                configuration.Board3Stage4AFamiliarNumbers,
                configuration.Board3Stage4BFamiliarNumbers,
                configuration.Board3Stage6FamiliarNumbers,
                configuration.Board3Stage8FamiliarNumbers,
                configuration.Board3Stage12FamiliarNumbers,
            },
            4 => new[]
            {
                configuration.FirstMastersMove1FamiliarNumbers,
                configuration.FirstMastersMove2BFamiliarNumbers,
                configuration.FirstMastersMove5FamiliarNumbers,
                configuration.FirstMastersMove7BFamiliarNumbers,
                configuration.FirstMastersMove9FamiliarNumbers,
            },
            _ => Array.Empty<List<int>>(),
        };

    internal bool IsBoardConfigurationComplete(int board, bool board3RouteA)
    {
        IEnumerable<RouteNode> battleNodes = board switch
        {
            1 when board1RouteNode != null => new[] { board1RouteNode },
            2 => board2RouteNodes.Values,
            3 => (board3RouteA ? board3ARouteNodes : board3BRouteNodes).Values,
            4 => firstMastersRouteNodes.Values,
            _ => Array.Empty<RouteNode>(),
        };

        var boardRoster = GetMutableFamiliarRoster(board);
        var battleNodeList = battleNodes.Where(node => node.Kind == RouteNodeKind.Battle ||
            (node.Kind == RouteNodeKind.Random && node.FamiliarStageNumber > 0)).ToList();
        return boardRoster.Count > 0 && battleNodeList.Count > 0 && battleNodeList
            .All(node =>
            {
                var stageRoster = GetMutableStageFamiliarRoster(board, node.FamiliarStageNumber, node.Variant);
                return stageRoster.Count == 3 && stageRoster.All(number => number > 0 && boardRoster.Contains(number));
            });
    }

    internal Vector2 GetMainWindowTabSize(int tab)
        => tab switch
        {
            0 => new Vector2(configuration.MainWindowRunWidth, configuration.MainWindowRunHeight),
            1 => new Vector2(configuration.MainWindowBoard1Width, configuration.MainWindowBoard1Height),
            2 => new Vector2(configuration.MainWindowBoard2Width, configuration.MainWindowBoard2Height),
            3 => new Vector2(configuration.MainWindowBoard3Width, configuration.MainWindowBoard3Height),
            4 => new Vector2(configuration.MainWindowFirstMastersWidth, configuration.MainWindowFirstMastersHeight),
            _ => new Vector2(820f, 680f),
        };

    internal void SaveMainWindowTabSize(int tab, Vector2 size)
    {
        var width = Math.Clamp(size.X, 200f, 4000f);
        var height = Math.Clamp(size.Y, 200f, 2400f);
        switch (tab)
        {
            case 0:
                configuration.MainWindowRunWidth = width;
                configuration.MainWindowRunHeight = height;
                break;
            case 1:
                configuration.MainWindowBoard1Width = width;
                configuration.MainWindowBoard1Height = height;
                break;
            case 2:
                configuration.MainWindowBoard2Width = width;
                configuration.MainWindowBoard2Height = height;
                break;
            case 3:
                configuration.MainWindowBoard3Width = width;
                configuration.MainWindowBoard3Height = height;
                break;
            case 4:
                configuration.MainWindowFirstMastersWidth = width;
                configuration.MainWindowFirstMastersHeight = height;
                break;
            default:
                return;
        }

        SaveConfiguration();
    }

    private void SaveConfiguration() => PluginInterface.SavePluginConfig(configuration);

    private static IReadOnlyDictionary<RoutePhase, RouteNode> BuildRoute(BoardRouteDefinition? route)
    {
        var nodes = new Dictionary<RoutePhase, RouteNode>();
        if (route == null || !route.Enabled)
        {
            return nodes;
        }

        foreach (var nodeData in route.Nodes)
        {
            var node = BuildRouteNode(nodeData);
            if (node == null || node.Phase == RoutePhase.None)
            {
                continue;
            }

            nodes[node.Phase] = node;
        }

        return nodes;
    }

    private static RouteNode? BuildRouteNode(BoardRouteNodeData? nodeData)
    {
        if (nodeData == null ||
            !Enum.TryParse<RouteNodeKind>(nodeData.Kind, ignoreCase: true, out var kind) ||
            !Enum.TryParse<RoutePhase>(nodeData.Phase, ignoreCase: true, out var phase) ||
            phase == RoutePhase.None)
        {
            return null;
        }

        var nextPhase = string.IsNullOrWhiteSpace(nodeData.NextPhase)
            ? RoutePhase.None
            : Enum.TryParse<RoutePhase>(nodeData.NextPhase, ignoreCase: true, out var parsedNextPhase)
                ? parsedNextPhase
                : RoutePhase.None;

        return new RouteNode
        {
            Phase = phase,
            Kind = kind,
            StageNumber = nodeData.StageNumber,
            Variant = nodeData.Variant,
            Name = nodeData.Name,
            Position = nodeData.Position.ToVector3(),
            ExpectedAddon = nodeData.ExpectedAddon,
            NextPhase = nextPhase,
            RecoveryThreshold = nodeData.RecoveryThreshold,
            RecoveryTarget = nodeData.RecoveryTarget,
            FamiliarStageNumber = nodeData.FamiliarStageNumber,
            FamiliarRosterKey = nodeData.FamiliarRosterKey,
            DirectResultFlow = nodeData.DirectResultFlow,
            CombatTargetBaseIds = nodeData.Combat.TargetBaseIds.ToArray(),
            PriorityTargetBaseId = nodeData.Combat.PriorityTargetBaseId,
            ActionTriggers = nodeData.Combat.ActionTriggers
                .Where(trigger => Enum.TryParse<ActionTriggerResponse>(trigger.Response, true, out _))
                .Select(trigger => new ActionTrigger
                {
                    SourceBaseId = trigger.SourceBaseId,
                    ActionId = trigger.ActionId,
                    ActionName = trigger.ActionName,
                    Response = Enum.Parse<ActionTriggerResponse>(trigger.Response, true),
                    ResponseActionId = trigger.ResponseActionId,
                    ResponseActionName = trigger.ResponseActionName,
                    ResponseTargetBaseId = trigger.ResponseTargetBaseId,
                })
                .ToArray(),
        };
    }

    internal int CrucibleModeIndex => configuration.CrucibleModeIndex;

    internal string CrucibleModeName => GetCrucibleModeName(configuration.CrucibleModeIndex);

    internal static string CrucibleModeComboItems => string.Join("\0", CrucibleModeNames) + "\0";

    internal void SetCrucibleModeIndex(int index)
    {
        if (EntryActive)
        {
            return;
        }

        var normalized = Math.Clamp(index, 0, CrucibleModeNames.Length - 1);
        if (configuration.CrucibleModeIndex == normalized)
        {
            return;
        }

        configuration.CrucibleModeIndex = normalized;
        ReloadConfiguredRoutes();
        SaveConfiguration();
        Log.Information($"[Config] Crucible mode changed mode={CrucibleModeName}.");
    }

    internal bool IsCrucibleModeAvailable(int board, bool board3RouteA)
        => board switch
        {
            1 => board1RouteNode != null,
            2 => board2RouteNodes.Count > 0,
            3 => (board3RouteA ? board3ARouteNodes : board3BRouteNodes).Count > 0,
            4 => firstMastersRouteNodes.Count > 0,
            _ => false,
        };

    private static string GetCrucibleModeName(int index)
        => CrucibleModeNames[Math.Clamp(index, 0, CrucibleModeNames.Length - 1)];

    private void ReloadConfiguredRoutes()
    {
        var mode = CrucibleModeName;
        board1RouteNode = BuildRouteNode(routeCatalog.FindRoute("board1", "board1-default", mode)?.Nodes.FirstOrDefault());
        board2RouteNodes = BuildRoute(routeCatalog.FindRoute("board2", "board2-b", mode));
        board3ARouteNodes = BuildRoute(routeCatalog.FindRoute("board3", "board3-a", mode));
        board3BRouteNodes = BuildRoute(routeCatalog.FindRoute("board3", "board3-b", mode));
        firstMastersRouteNodes = BuildRoute(routeCatalog.FindRoute("first-masters-board", "first-masters-default", mode));
        Log.Information($"[Data] configured routes mode={mode} board1={board1RouteNode != null} " +
            $"board2={board2RouteNodes.Count} board3A={board3ARouteNodes.Count} " +
            $"board3B={board3BRouteNodes.Count} firstMasters={firstMastersRouteNodes.Count}.");
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        var wasIdle = entryState == EntryState.Idle && potionDebugState == PotionDebugState.Idle;
        StopEntry("plugin-disposed");
        if (wasIdle)
        {
            ResetEntryRuntime("plugin-disposed");
        }
        ReleaseBossModPreset("plugin-disposed");
        ECommonsMain.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenMainWindow;
        CommandManager.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        debugWindow.Dispose();
    }

    internal void StartFirstBoardEntry(bool loop = true) => StartBoardEntry(1, loop);

    internal void StartSecondBoardEntry(bool loop = true) => StartBoardEntry(2, loop);

    internal void StartThirdBoardAEntry(bool loop = true) => StartBoardEntry(3, loop, Board3RouteVariant.A);

    internal void StartThirdBoardBEntry(bool loop = true) => StartBoardEntry(3, loop, Board3RouteVariant.B);

    internal void StartFirstMastersBoardEntry(bool loop = true) => StartBoardEntry(4, loop);

    private void StartBoardEntry(int board, bool loop, Board3RouteVariant board3Route = Board3RouteVariant.B)
    {
        if (EntryActive)
        {
            return;
        }

        selectedBoard = board;
        selectedBoard3Route = board == 3 ? board3Route : Board3RouteVariant.B;
        if (!IsCrucibleModeAvailable(board, board3Route == Board3RouteVariant.A))
        {
            Log.Warning($"[Entry] No route data is configured for board={board} mode={CrucibleModeName}; " +
                "entry automation was not started.");
            return;
        }

        if (board == 4 && !IsBoardConfigurationComplete(4, false))
        {
            Log.Warning("[Entry] First Master's Board route or familiar configuration is unavailable; entry automation was not started.");
            return;
        }
        if (board == 3 && !HasBoard3FamiliarConfiguration())
        {
            return;
        }

        stopAfterCurrentLoop = false;
        ReleaseBossModPreset("new entry");
        ResetEntryRuntime("new entry");
        entryState = EntryState.AwaitLaudaReady;
        loopEntry = loop;
        routePhase = RoutePhase.None;
        entryStateStarted = DateTime.UtcNow;
        nextEntryAction = DateTime.MinValue;
        var route = selectedBoard == 3 ? $" route=3{selectedBoard3Route}" : string.Empty;
        Log.Information($"[Entry] started board={selectedBoard}{route} mode={CrucibleModeName} entry automation.");
    }

    private bool HasBoard3FamiliarConfiguration()
    {
        if (ActiveRouteNodes.Count == 0)
        {
            Log.Warning("[Entry] Board 3 route is unavailable; entry automation was not started.");
            return false;
        }

        var boardRoster = configuration.Board3FamiliarNumbers;
        if (boardRoster.Count == 0)
        {
            Log.Warning("[Entry] Board 3 familiar roster is empty; configure the roster before starting entry automation.");
            return false;
        }

        foreach (var node in ActiveRouteNodes.Values.Where(node => node.Kind == RouteNodeKind.Battle))
        {
            var stageRoster = GetMutableStageFamiliarRoster(3, node.FamiliarStageNumber, node.Variant);
            if (stageRoster.Count != 3 || stageRoster.Any(number => !boardRoster.Contains(number)))
            {
                Log.Warning($"[Entry] Board 3 familiar roster is incomplete move={node.StageNumber}{node.Variant} " +
                    $"configured={string.Join(",", stageRoster)} boardRoster={string.Join(",", boardRoster)}; " +
                    "configure three valid move familiars before starting entry automation.");
                return false;
            }
        }

        return true;
    }

    private void ResetCombatProviders()
    {
        try
        {
            rsrChangeMode.InvokeAction(0);
        }
        catch (Exception ex)
        {
            Log.Debug($"[Combat] RSR reset unavailable: {ex.Message}");
        }

        if (bossModPresetActive)
        {
            try
            {
                bossModClearActive.InvokeFunc();
                bossModPresetActive = false;
            }
            catch (Exception ex)
            {
                Log.Debug($"[Combat] BossMod reset unavailable: {ex.Message}");
            }
        }
    }

    private bool EnsureBossModPreset()
    {
        if (bossModPresetCreated)
        {
            return true;
        }

        bossModPresetSessionStarted = true;
        try
        {
            if (!bossModPreviousActivePresetCaptured)
            {
                bossModPreviousActivePreset = bossModGetActivePreset.InvokeFunc();
                if (string.Equals(bossModPreviousActivePreset, BossModPresetName, StringComparison.OrdinalIgnoreCase))
                {
                    bossModPreviousActivePreset = null;
                }

                bossModPreviousActivePresetCaptured = true;
            }

            var assemblyDirectory = PluginInterface.AssemblyLocation.DirectoryName;
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                Log.Error("[Combat] Could not resolve plugin assembly directory for BossMod preset.");
                return false;
            }

            var path = Path.Combine(assemblyDirectory, "Data", BossModPresetFileName);
            if (!File.Exists(path))
            {
                Log.Error($"[Combat] BossMod preset file was not found: {path}");
                return false;
            }

            if (!bossModCreatePreset.InvokeFunc(File.ReadAllText(path), true))
            {
                Log.Error($"[Combat] BossMod rejected preset creation name={BossModPresetName}.");
                return false;
            }

            bossModPresetCreated = true;
            Log.Information($"[Combat] BossMod preset created or overwritten name={BossModPresetName}.");
            return true;
        }
        catch (IpcNotReadyError)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Combat] Failed to create BossMod preset name={BossModPresetName}.");
            return false;
        }
    }

    private bool ProcessBossModRecovery()
    {
        if (bossModRecoveryTask == null)
        {
            return false;
        }

        if (!bossModRecoveryTask.IsCompleted)
        {
            if (DateTime.UtcNow < bossModRecoveryDeadline)
            {
                return true;
            }

            var recoveryPlugin = bossModRecoveryPluginName ?? "unknown";
            bossModRecoveryTask = null;
            bossModRecoveryDeadline = DateTime.MinValue;
            FailEntry($"bossmod-recovery-timeout:{recoveryPlugin}");
            return true;
        }

        var completedTask = bossModRecoveryTask;
        bossModRecoveryTask = null;
        bossModRecoveryDeadline = DateTime.MinValue;
        if (completedTask.IsFaulted || completedTask.IsCanceled)
        {
            var reason = completedTask.Exception?.GetBaseException().Message ??
                (completedTask.IsCanceled ? "canceled" : "unknown");
            FailEntry($"bossmod-recovery-failed:{reason}");
            return true;
        }

        Log.Information($"[Combat] BossMod recovery cycle completed plugin={bossModRecoveryPluginName}; retrying IPC.");
        bossModRecoveryPluginName = null;
        return false;
    }

    private bool TryStartBossModRecovery()
    {
        if (bossModRecoveryTask != null)
        {
            return true;
        }

        if (DateTime.UtcNow < bossModRecoveryNextAttempt)
        {
            return false;
        }

        if (!bossModToggleHelper.TryFindLoadedBossMod(out var pluginName, out var findFailure) || pluginName == null)
        {
            Log.Warning($"[Combat] BossMod IPC is not ready and recovery could not start: {findFailure ?? "unknown"}.");
            return false;
        }

        if (!bossModToggleHelper.TryCycle(pluginName, out var operationTask, out var cycleFailure) || operationTask == null)
        {
            Log.Warning($"[Combat] BossMod IPC recovery cycle could not start plugin={pluginName}: {cycleFailure ?? "unknown"}.");
            bossModRecoveryNextAttempt = DateTime.UtcNow.AddSeconds(30);
            return false;
        }

        bossModRecoveryPluginName = pluginName;
        bossModRecoveryTask = operationTask;
        bossModRecoveryDeadline = DateTime.UtcNow.AddSeconds(20);
        bossModRecoveryNextAttempt = DateTime.UtcNow.AddSeconds(30);
        Log.Warning($"[Combat] BossMod IPC was not ready; cycling plugin={pluginName} to force IPC registration.");
        return true;
    }

    private void ReleaseBossModPreset(string reason)
    {
        if (!bossModPresetSessionStarted)
        {
            return;
        }

        if (bossModPresetActive)
        {
            try
            {
                bossModClearActive.InvokeFunc();
            }
            catch (Exception ex)
            {
                Log.Debug($"[Combat] BossMod active preset clear unavailable: {ex.Message}");
            }

            bossModPresetActive = false;
        }

        if (bossModPreviousActivePresetCaptured)
        {
            try
            {
                var restored = bossModPreviousActivePreset == null
                    ? bossModClearActive.InvokeFunc()
                    : bossModSetActive.InvokeFunc(bossModPreviousActivePreset);
                if (!restored)
                {
                    Log.Warning("[Combat] BossMod previous active preset could not be restored.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Combat] Failed to restore BossMod active preset list.");
            }
        }

        if (bossModPresetCreated)
        {
            try
            {
                if (!bossModDeletePreset.InvokeFunc(BossModPresetName))
                {
                    Log.Warning($"[Combat] BossMod preset could not be deleted name={BossModPresetName}.");
                }
                else
                {
                    Log.Information($"[Combat] BossMod preset deleted name={BossModPresetName} reason={reason}.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, $"[Combat] Failed to delete BossMod preset name={BossModPresetName}.");
            }
        }

        bossModPreviousActivePreset = null;
        bossModPreviousActivePresetCaptured = false;
        bossModPresetSessionStarted = false;
        bossModPresetCreated = false;
    }

    private void ResetStageCombatProviders(string reason)
    {
        ResetCombatProviders();
        if (TargetManager.Target != null)
        {
            TargetManager.Target = null;
        }

        Log.Information($"[Combat] move providers reset reason=\"{EscapeLog(reason)}\" target=cleared.");
    }

    private void ResetEntryRuntime(string reason)
    {
        if (potionDebugState != PotionDebugState.Idle)
        {
            potionDebugState = PotionDebugState.Idle;
            potionDebugStateStarted = DateTime.MinValue;
            Log.Information($"[Debug] potion-use cancelled reason=\"{EscapeLog(reason)}\".");
        }

        CancelItemUse(reason);
        CancelRecovery(reason);
        ClearActionTriggerRuntime();
        ResetStageDetailModeSelection();
        TryStopVnav();
        movement.Clear();
        treasure.Clear();
        randomOutcome = RandomOutcomeKind.None;
        recovery.Clear();
        usedAttackItemIds.Clear();
            failedItemUseIds.Clear();
        snarledSahaginTargets.Clear();
        ResetCombatProviders();

        routePhase = RoutePhase.None;
        recoveryNoUsableItemsRoute = RoutePhase.None;
        recoveryNoUsableItemsThreshold = 0;
        petParty.Clear();
        knightSetupComplete = false;
        knightRearMoveStarted = false;
        knightRearDestination = Vector3.Zero;
        stage4APulletWaveObserved = false;
        stage4APulletReturnStarted = false;
        preStageItemsHandled = false;
        sandstillArmed = false;
        sandstillRestartHandled = false;
        boardChallengeConfirmed = false;
        laudaReadySince = null;
        Log.Information($"[Entry] runtime reset reason=\"{EscapeLog(reason)}\".");
    }

    private void EndEntry(EntryState nextState, string reason)
    {
        var previousState = entryState;
        ResetEntryRuntime(reason);
        ReleaseBossModPreset(reason);
        stopAfterCurrentLoop = false;
        loopEntry = false;
        entryState = nextState;
        Log.Information($"[Entry] terminal state={nextState} previousState={previousState} reason=\"{EscapeLog(reason)}\".");
    }

    internal void StopEntry(string reason = "user")
    {
        if (entryState == EntryState.Idle && potionDebugState == PotionDebugState.Idle)
        {
            return;
        }

        EndEntry(EntryState.Idle, reason);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        ProcessPotionDebug();

        if (!EntryActive)
        {
            return;
        }

        if (itemUse.Active)
        {
            ProcessItemUse();
            return;
        }

        if ((entryState is EntryState.AwaitRecoveryMainHud or EntryState.AwaitRecoveryContextMenu or EntryState.AwaitRecoveryHp) &&
            recovery.Continuation == EntryState.Combat && !HasLivingCombatTarget())
        {
            CancelRecovery("combat encounter cleared");
            AdvanceEntry(EntryState.Combat, 0);
            Log.Information("[Recovery] combat encounter cleared while recovery was pending; returning to combat exit handling.");
            return;
        }

        if (recovery.Active && DateTime.UtcNow - recovery.StartedAt >= TimeSpan.FromSeconds(RecoverySessionTimeoutSeconds))
        {
            var continuation = recovery.Continuation;
            CancelRecovery("recovery session timeout");
            AdvanceEntry(continuation, 0);
            Log.Warning($"[Recovery] recovery session timeout after {RecoverySessionTimeoutSeconds}s; " +
                $"returning to {continuation}.");
            return;
        }

        var stateTimeout = entryState is EntryState.AwaitFirstBoardTerritory or EntryState.MoveToStageStart or EntryState.AwaitStageLoaded or EntryState.MoveToStage4APriorityReturn or EntryState.MoveToStage4BRest or EntryState.MoveToRandomOutcome or EntryState.AwaitRandomOutcome or EntryState.MoveToStage6Start or EntryState.MoveToStage8Start or EntryState.MoveToStage10Start or EntryState.MoveToSecondBoardShop or EntryState.AwaitBoardExitTransitionStart or EntryState.AwaitBoardExitTransitionReady
            ? TimeSpan.FromSeconds(120)
            : entryState == EntryState.Combat
                ? TimeSpan.FromHours(8)
            : TimeSpan.FromSeconds(30);
        if (DateTime.UtcNow - entryStateStarted > stateTimeout)
        {
            var failedState = entryState;
            var failedTarget = EntryTarget;
            EndEntry(EntryState.Failed, "timeout");
            Log.Warning($"[Entry] failed state={failedState} target={failedTarget} reason=timeout.");
            return;
        }

        if (TryHandleMidStageResult())
        {
            return;
        }

        if (entryState == EntryState.Combat && IsRouteDrivenBoard &&
            DateTime.UtcNow - recovery.LastCombatRecoveryCheck >=
                TimeSpan.FromMilliseconds(CombatRecoveryCheckMilliseconds))
        {
            recovery.LastCombatRecoveryCheck = DateTime.UtcNow;
            var recoveryThreshold = CurrentRouteNode?.RecoveryThreshold ?? 60;
            var recoveryTarget = CurrentRouteNode?.RecoveryTarget ?? 90;
            if (HasLivingCombatTarget() && IsPlayerBelowPotionThreshold(recoveryThreshold) &&
                TryBeginRecovery(EntryState.Combat, recoveryThreshold, recoveryTarget))
            {
                return;
            }
        }

        if (DateTime.UtcNow < nextEntryAction)
        {
            return;
        }

        switch (entryState)
        {
            case EntryState.AwaitLaudaReady:
                ProcessLaudaReady();
                break;
            case EntryState.TargetLauda:
                ProcessTargetLauda();
                break;
            case EntryState.AwaitSelectIconString:
                ProcessSelectIconString();
                break;
            case EntryState.AwaitSelectString:
                ProcessSelectString();
                break;
            case EntryState.AwaitStageList:
                ProcessStageList();
                break;
            case EntryState.AwaitStageDetail:
                ProcessStageDetail();
                break;
            case EntryState.AwaitFirstBoardTerritory:
                if (TryConfirmBoardChallenge())
                {
                    break;
                }

        if (ClientTerritoryId == BoardTerritory)
        {
            boardMovementTransitionObserved = false;
            boardMovementClearSince = null;
            boardMovementReady = false;
            vnavMoveStarted = false;
            AdvanceEntry(EntryState.MoveToStageStart);
        }
                break;
            case EntryState.MoveToStageStart:
                ProcessMoveToStageStart();
                break;
            case EntryState.AwaitPreChallengeRoster:
                ProcessPreChallengeRosterAvailability();
                break;
            case EntryState.AwaitStagePetParty:
                ProcessStagePetPartyAvailability();
                break;
            case EntryState.SelectStagePetParty:
                ProcessStagePetPartySelection();
                break;
            case EntryState.OpenFamiliarNotebook:
                ProcessOpenFamiliarNotebook();
                break;
            case EntryState.AwaitFamiliarNotebook:
                ProcessFamiliarNotebookAvailability();
                break;
            case EntryState.SelectFamiliarNotebook:
                ProcessFamiliarNotebookSelection();
                break;
            case EntryState.AwaitFamiliarRosterUpdate:
                ProcessFamiliarRosterUpdate();
                break;
            case EntryState.AwaitInInstanceStageDetail:
                ProcessInInstanceStageDetailAvailability();
                break;
            case EntryState.CommenceInInstanceStage:
                ProcessInInstanceCommence();
                break;
            case EntryState.AwaitStageLoaded:
                ProcessStageLoaded();
                break;
            case EntryState.ActivateBossMod:
                ProcessActivateBossMod();
                break;
            case EntryState.PrepareKnight:
                ProcessPrepareKnight();
                break;
            case EntryState.AwaitKnightAction:
                ProcessAwaitKnightAction();
                break;
            case EntryState.MoveBehindKnight:
                ProcessMoveBehindKnight();
                break;
            case EntryState.Combat:
                ProcessCombat();
                break;
            case EntryState.AwaitSandstillRestart:
                ProcessSandstillRestart();
                break;
            case EntryState.AwaitContentsBotty:
                ProcessContentsBottyAvailability();
                break;
            case EntryState.ExitContentsBotty:
                ProcessContentsBottyExit();
                break;
            case EntryState.AwaitExitConfirmation:
                ProcessExitConfirmationAvailability();
                break;
            case EntryState.ConfirmExit:
                ProcessExitConfirmation();
                break;
            case EntryState.AwaitXbmResult:
                ProcessXbmResultAvailability();
                break;
            case EntryState.ExitXbmResult:
                ProcessXbmResultInitialCallback();
                break;
            case EntryState.AwaitNeedGreed:
                ProcessNeedGreedAvailability();
                break;
            case EntryState.RollNeedGreed:
                ProcessNeedGreedRoll();
                break;
            case EntryState.ConfirmXbmResultExit:
                ProcessXbmResultExitCallback();
                break;
            case EntryState.AwaitCentralShroud:
                ProcessCentralShroudReturn();
                break;
            case EntryState.AwaitRecoveryMainHud:
                ProcessRecoveryMainHud();
                break;
            case EntryState.AwaitRecoveryContextMenu:
                ProcessRecoveryContextMenu();
                break;
            case EntryState.AwaitRecoveryHp:
                ProcessRecoveryHp();
                break;
            case EntryState.MoveToStage4APriorityReturn:
                ProcessMoveToStage4APriorityReturn();
                break;
            case EntryState.MoveToStage4BRest:
                ProcessMoveToStage4BRest();
                break;
            case EntryState.AwaitStage4BTent:
                ProcessStage4BTentAvailability();
                break;
            case EntryState.RestStage4B:
                ProcessStage4BRest();
                break;
            case EntryState.AwaitStage4BConfirmation:
                ProcessStage4BConfirmation();
                break;
            case EntryState.MoveToRandomOutcome:
                ProcessMoveToRandomOutcome();
                break;
            case EntryState.AwaitRandomOutcome:
                ProcessRandomOutcomeAvailability();
                break;
            case EntryState.MoveToStage6Start:
                ProcessMoveToStage6Start();
                break;
            case EntryState.MoveToStage10Start:
                ProcessMoveToStage10Start();
                break;
            case EntryState.AwaitStage6Treasure:
                ProcessStage6TreasureAvailability();
                break;
            case EntryState.SelectStage6Treasure:
                ProcessStage6TreasureSelection();
                break;
            case EntryState.AwaitStage6TreasureConfirmation:
                ProcessStage6TreasureConfirmation();
                break;
            case EntryState.AwaitStage8Treasure:
                ProcessStage8TreasureAvailability();
                break;
            case EntryState.MoveToStage8Start:
                ProcessMoveToStage8Start();
                break;
            case EntryState.SelectStage8Treasure:
                ProcessStage8TreasureSelection();
                break;
            case EntryState.AwaitStage8TreasureConfirmation:
                ProcessStage8TreasureConfirmation();
                break;
            case EntryState.MoveToSecondBoardShop:
                ProcessMoveToSecondBoardShop();
                break;
            case EntryState.AwaitBoardExitTransitionStart:
                ProcessBoardExitTransitionStart();
                break;
            case EntryState.AwaitBoardExitTransitionReady:
                ProcessBoardExitTransitionReady();
                break;
            case EntryState.AwaitSecondBoardShop:
                ProcessSecondBoardShopAvailability();
                break;
            case EntryState.CloseSecondBoardShop:
                ProcessCloseSecondBoardShop();
                break;
            case EntryState.AwaitSecondBoardExitConfirmation:
                ProcessSecondBoardExitConfirmationAvailability();
                break;
            case EntryState.ConfirmSecondBoardExit:
                ProcessSecondBoardExitConfirmation();
                break;
            case EntryState.AwaitSecondBoardMainHud:
                ProcessSecondBoardMainHudAvailability();
                break;
            case EntryState.ExitSecondBoardMainHud:
                ProcessSecondBoardMainHudExit();
                break;
            case EntryState.AwaitSecondBoardFinalConfirmation:
                ProcessSecondBoardFinalConfirmationAvailability();
                break;
            case EntryState.ConfirmSecondBoardFinalExit:
                ProcessSecondBoardFinalConfirmation();
                break;
            case EntryState.AwaitInstanceLeave:
                ProcessInstanceLeave();
                break;
        }
    }

    private bool TryHandleMidStageResult()
    {
        if (entryState is not (EntryState.Combat or EntryState.AwaitSandstillRestart or EntryState.AwaitRecoveryMainHud or
            EntryState.AwaitRecoveryContextMenu or EntryState.AwaitRecoveryHp) ||
            GetReadyAddon("XBMResult") == null)
        {
            return false;
        }

        var failedState = entryState;
        var recoveryWasActive = recovery.Active;
        if (recoveryWasActive)
        {
            CancelRecovery("mid-move XBMResult");
        }

        ResetStageCombatProviders("mid-move XBMResult");
        AdvanceEntry(EntryState.AwaitXbmResult);
        Log.Warning($"[Failure] XBMResult detected mid-move state={failedState}; starting result exit flow.");
        return true;
    }

    internal void UseDebugPotion()
    {
        if (EntryActive)
        {
            Log.Warning("[Debug] potion-use blocked: entry automation is active.");
            return;
        }

        if (potionDebugState != PotionDebugState.Idle)
        {
            return;
        }

        potionDebugState = PotionDebugState.AwaitMainHud;
        potionDebugStateStarted = DateTime.UtcNow;
        Log.Information("[Debug] potion-use started actionId=46961; waiting for XBMContentsMainHUD.");
        ProcessPotionDebug();
    }

    private void ProcessPotionDebug()
    {
        if (potionDebugState == PotionDebugState.Idle)
        {
            return;
        }

        if (DateTime.UtcNow - potionDebugStateStarted > TimeSpan.FromSeconds(10))
        {
            Log.Warning($"[Debug] potion-use failed state={potionDebugState} reason=timeout.");
            potionDebugState = PotionDebugState.Idle;
            return;
        }

        if (potionDebugState == PotionDebugState.AwaitMainHud)
        {
            var addon = GetReadyAddon("XBMContentsMainHUD");
            if (addon == null)
            {
                return;
            }

            if (!TryFireCallback(addon, "XBMContentsMainHUD", "debug-potion-select", 6, 0,
                    new AtkValue { Type = AtkValueType.Undefined }))
            {
                potionDebugState = PotionDebugState.Idle;
                return;
            }
            potionDebugState = PotionDebugState.AwaitContextMenu;
            potionDebugStateStarted = DateTime.UtcNow;
            Log.Information("[Debug] potion-use opened ContextMenu callback=6,0,undefined.");
            return;
        }

        var contextMenu = GetReadyAddon("ContextMenu");
        if (contextMenu == null)
        {
            return;
        }

        if (!TryGetContextMenuUseIndex(contextMenu, out var potionUseIndex) ||
            !TryFireCallback(contextMenu, "ContextMenu", "debug-potion-use", 0, potionUseIndex, (uint)0,
                new AtkValue { Type = AtkValueType.Undefined },
                new AtkValue { Type = AtkValueType.Undefined }))
        {
            potionDebugState = PotionDebugState.Idle;
            return;
        }
        potionDebugState = PotionDebugState.Idle;
        Log.Information($"[Debug] potion-use selected ContextMenu Use entry index={potionUseIndex}.");
    }

    private void ProcessLaudaReady()
    {
        if (ClientTerritoryId != 148)
        {
            laudaReadySince = null;
            return;
        }

        var lauda = FindLauda();
        if (lauda == null)
        {
            laudaReadySince = null;
            return;
        }

        laudaReadySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - laudaReadySince.Value < TimeSpan.FromSeconds(1))
        {
            return;
        }

        AdvanceEntry(EntryState.TargetLauda);
    }

    private void ProcessTargetLauda()
    {
        var lauda = FindLauda();
        if (lauda == null || !lauda.IsTargetable)
        {
            return;
        }

        if (TargetManager.Target?.GameObjectId != lauda.GameObjectId)
        {
            TargetManager.Target = lauda;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(250);
            return;
        }

        var targetSystem = TargetSystem.Instance();
        var laudaStruct = lauda.Struct();
        if (targetSystem == null || laudaStruct == null)
        {
            FailEntry("lauda-interaction-native-unavailable");
            return;
        }

        targetSystem->InteractWithObject(laudaStruct, false);
        AdvanceEntry(EntryState.AwaitSelectIconString);
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? FindLauda()
    {
        var sheet = DataManager.GetExcelSheet<ENpcResident>();
        return ObjectTable.FirstOrDefault(gameObject =>
        {
            if (gameObject.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc ||
                !gameObject.IsValid() || !gameObject.IsTargetable ||
                !sheet.TryGetRow(gameObject.BaseId, out var resident))
            {
                return false;
            }

            if (gameObject.BaseId == 1059316)
            {
                return true;
            }

            return string.Equals(resident.Singular.ToString(), "Lauda", StringComparison.OrdinalIgnoreCase) &&
                (resident.Title.ToString().Contains("Crucible", StringComparison.OrdinalIgnoreCase) ||
                 resident.Title.ToString().Contains("Unbroken", StringComparison.OrdinalIgnoreCase));
        });
    }

    private void ProcessSelectIconString()
    {
        if (GetReadyAddon("SelectString") != null)
        {
            Log.Information("[Entry] optional SelectIconString was not shown; continuing with SelectString.");
            AdvanceEntry(EntryState.AwaitSelectString, 0);
            return;
        }

        var addon = GetReadyAddon("SelectIconString");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "SelectIconString", "select-icon-string", 1))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitSelectString);
    }

    private void ProcessSelectString()
    {
        var addon = GetReadyAddon("SelectString");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "SelectString", "select-string", 0))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitStageList);
    }

    private void ProcessStageList()
    {
        var addon = GetReadyAddon("XBMStageList");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMStageList", "select-board", 1, selectedBoard))
        {
            return;
        }
        if (IsRouteDrivenBoard)
        {
            if (!SetRoute(InitialRoutePhase, $"Board {selectedBoard} move list selected"))
            {
                return;
            }
        }
        AdvanceEntry(EntryState.AwaitPreChallengeRoster);
    }

    private void ProcessStageDetail()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Battle))
        {
            return;
        }

        if (!RequireExpectedRouteAddon("XBMStageDetailList"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMStageDetailList");
        if (addon == null)
        {
            return;
        }

        if (!EnsureStageDetailMode(addon))
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMStageDetailList", "commence-stage", 8))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitFirstBoardTerritory);
    }

    private bool TryConfirmBoardChallenge()
    {
        if (boardChallengeConfirmed)
        {
            return false;
        }

        var addon = GetReadyAddon("SelectYesno");
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount == 0)
        {
            return false;
        }

        if (!TryGetSelectYesnoPrompt(addon, out var prompt))
        {
            return false;
        }
        if (!prompt.Contains("Proceed to challenge this board?", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryFireCallback(addon, "SelectYesno", "confirm-board-challenge", 0))
        {
            return false;
        }
        boardChallengeConfirmed = true;
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
        Log.Information("[Entry] confirmed low-rank board challenge prompt.");
        return true;
    }

    private void ProcessMoveToStageStart()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Battle))
        {
            return;
        }

        if (ClientTerritoryId != BoardTerritory)
        {
            FailEntry("territory-changed");
            return;
        }

        var destination = CurrentStageStartPosition;
        if (!boardMovementReady)
        {
            if (HasEntryTransitionCondition())
            {
                boardMovementClearSince = null;
                return;
            }

            boardMovementClearSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - boardMovementClearSince.Value < TimeSpan.FromSeconds(2))
            {
                return;
            }

            boardMovementReady = true;
            boardMovementClearSince = null;
        }

        if (boardMovementTransitionObserved)
        {
            boardMovementTransitionObserved = false;
            boardMovementClearSince = null;
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
            Log.Information("[Entry] move-start event transition observed; continuing to move familiar selection.");
            return;
        }

        if (HasEntryTransitionCondition())
        {
            boardMovementTransitionObserved = true;
            boardMovementClearSince = null;
            StopMovement();
            return;
        }

        if (GetReadyAddon("XBMPetParty") != null)
        {
            StopMovement();
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
            return;
        }

        if (TryMoveToDestination(destination, "move start", "vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            boardMovementReady = true;
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
        }
    }

    private void ProcessPreChallengeRosterAvailability()
    {
        if (!RequireExpectedRouteAddon("XBMPetParty"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        var current = ReadCurrentFamiliarNumbers(addon);
        var desired = ConfiguredFamiliarNumbers.ToHashSet();

        var toRemove = current.Except(desired);
        var toAdd = desired.Except(current);
        petParty.FamiliarRemovalNumbers.Clear();
        petParty.FamiliarRemovalNumbers.UnionWith(toRemove);
        petParty.FamiliarAdditionNumbers.Clear();
        petParty.FamiliarAdditionNumbers.UnionWith(toAdd);
        petParty.PendingFamiliarNumbers.Clear();
        petParty.PendingFamiliarNumbers.AddRange(toRemove
            .Order()
            .Concat(toAdd.Order()));
        Log.Information($"[Entry] familiar roster current={string.Join(",", current.Order())} " +
            $"desired={string.Join(",", desired.Order())} remove={string.Join(",", toRemove.Order())} " +
            $"add={string.Join(",", toAdd.Order())}.");
        if (petParty.PendingFamiliarNumbers.Count > 0)
        {
            AdvanceEntry(EntryState.OpenFamiliarNotebook);
            return;
        }

        AdvanceEntry(EntryState.AwaitStageDetail);
    }

    private void ProcessStagePetPartyAvailability()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Battle))
        {
            return;
        }

        if (!RequireExpectedRouteAddon("XBMPetParty"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        var records = ReadPetPartyRecords(addon);
        if (records.Count == 0)
        {
            Log.Debug("[Entry] in-board familiar roster is not populated yet; waiting before move selection.");
            return;
        }

        if (records.Any(record => record.MaximumHp == 0))
        {
            Log.Debug("[Entry] in-board familiar HP data is not populated yet; waiting before move selection.");
            return;
        }

        var configured = ConfiguredStageFamiliarNumbers.Distinct().ToList();
        if (configured.Count != 3)
        {
            FailEntry($"move-familiar-roster-requires-3:{configured.Count}");
            return;
        }

        var desired = ResolveHealthyStageFamiliarNumbers(records, configured);
        if (desired == null)
        {
            return;
        }

        petParty.StageToggleIndex = 0;
        petParty.StageToggleNumbers = null;
        petParty.StageDesiredNumbers = desired;
        petParty.StageSelectionRetryCount = 0;
        AdvanceEntry(EntryState.SelectStagePetParty);
    }

    private void ProcessOpenFamiliarNotebook()
    {
        if (!RequireExpectedRouteAddon("XBMPetParty"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMPetParty", "open-familiar-notebook", 5))
        {
            return;
        }
        petParty.NotebookPage = -1;
        petParty.NotebookPhase = 0;
        petParty.NotebookToggleIndex = 0;
        petParty.NotebookLastAction = DateTime.MinValue;
        petParty.NotebookResolvedNumbers.Clear();
        AdvanceEntry(EntryState.AwaitFamiliarNotebook);
        Log.Information($"[Entry] opening familiar notebook for reconciliation pending={string.Join(",", petParty.PendingFamiliarNumbers)}.");
    }

    private void ProcessFamiliarNotebookAvailability()
    {
        if (GetReadyAddon("XBMMonsterNotebook") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.SelectFamiliarNotebook);
    }

    private void ProcessFamiliarNotebookSelection()
    {
        var addon = GetReadyAddon("XBMMonsterNotebook");
        if (addon == null)
        {
            return;
        }

        if (petParty.NotebookPage < 0)
        {
            if (!TryFireCallback(addon, "XBMMonsterNotebook", "notebook-page-1", 3, (uint)0))
            {
                return;
            }
            petParty.NotebookPage = 0;
            petParty.NotebookToggleIndex = 0;
            petParty.NotebookLastAction = DateTime.UtcNow;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
            Log.Information("[Entry] notebook switched to page 1.");
            return;
        }

        var available = ReadNotebookFamiliarEntries(addon);
        if (available.Count == 0)
        {
            Log.Debug("[Entry] familiar notebook is open but its entries are not populated yet; waiting.");
            return;
        }

        var phaseNumbers = (petParty.NotebookPhase switch
        {
            0 or 1 => petParty.FamiliarRemovalNumbers.Order(),
            _ => petParty.FamiliarAdditionNumbers.Order(),
        }).ToList();
        Log.Information($"[Entry] notebook page={petParty.NotebookPage + 1} phase={petParty.NotebookPhase} " +
            $"atkValuesCount={addon->AtkValuesCount} availableCount={available.Count} " +
            $"requested={string.Join(",", petParty.PendingFamiliarNumbers)} " +
            $"availableNumbers={string.Join(",", available.Select(entry => $"{entry.Number}:{entry.PetId}:ui={entry.UiIndex}"))}.");

        if (DateTime.UtcNow - petParty.NotebookLastAction < TimeSpan.FromMilliseconds(500))
        {
            return;
        }

        if (petParty.NotebookToggleIndex < phaseNumbers.Count)
        {
            var number = phaseNumbers[petParty.NotebookToggleIndex++];
            var entry = available.FirstOrDefault(candidate => candidate.Number == number);
            if (entry != null)
            {
                if (!TryFireCallback(addon, "XBMMonsterNotebook", $"select-familiar-{number}", 7, (uint)entry.UiIndex))
                {
                    return;
                }

                petParty.NotebookResolvedNumbers.Add(number);
                petParty.NotebookLastAction = DateTime.UtcNow;
                Log.Information($"[Entry] familiar No. {number} selected from notebook uiIndex={entry.UiIndex}; waiting for roster refresh.");
                nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
            }

            return;
        }

        if (petParty.NotebookPhase == 0)
        {
            if (!TryFireCallback(addon, "XBMMonsterNotebook", "notebook-page-2-removals", 3, 1u))
            {
                return;
            }
            petParty.NotebookPage = 1;
            petParty.NotebookPhase++;
            petParty.NotebookToggleIndex = 0;
            petParty.NotebookLastAction = DateTime.UtcNow;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
            Log.Information("[Entry] notebook removal scan page 1 complete; switched to page 2.");
            return;
        }

        if (petParty.NotebookPhase == 1)
        {
            var unresolvedRemovals = petParty.FamiliarRemovalNumbers
                .Where(number => !petParty.NotebookResolvedNumbers.Contains(number))
                .Order()
                .ToList();
            if (unresolvedRemovals.Count > 0)
            {
                FailEntry($"familiar-not-found:{string.Join(",", unresolvedRemovals)}");
                return;
            }

            if (!TryFireCallback(addon, "XBMMonsterNotebook", "notebook-page-1-additions", 3, 0u))
            {
                return;
            }
            petParty.NotebookPage = 0;
            petParty.NotebookPhase++;
            petParty.NotebookToggleIndex = 0;
            petParty.NotebookLastAction = DateTime.UtcNow;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
            Log.Information("[Entry] notebook removal scan complete; switched to page 1 for additions.");
            return;
        }

        if (petParty.NotebookPhase == 2)
        {
            if (!TryFireCallback(addon, "XBMMonsterNotebook", "notebook-page-2-additions", 3, 1u))
            {
                return;
            }
            petParty.NotebookPage = 1;
            petParty.NotebookPhase++;
            petParty.NotebookToggleIndex = 0;
            petParty.NotebookLastAction = DateTime.UtcNow;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
            Log.Information("[Entry] notebook addition scan page 1 complete; switched to page 2.");
            return;
        }

        var unresolvedAdditions = petParty.FamiliarAdditionNumbers
            .Where(number => !petParty.NotebookResolvedNumbers.Contains(number))
            .Order()
            .ToList();
        if (unresolvedAdditions.Count > 0)
        {
            FailEntry($"familiar-not-found:{string.Join(",", unresolvedAdditions)}");
            return;
        }

        if (!TryFireCallback(addon, "XBMMonsterNotebook", "close-familiar-notebook", 0))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitFamiliarRosterUpdate);
        Log.Information("[Entry] all missing familiars selected; closing familiar notebook.");
    }

    private void ProcessFamiliarRosterUpdate()
    {
        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        var current = ReadCurrentFamiliarNumbers(addon);
        var desired = ConfiguredFamiliarNumbers.ToHashSet();
        if (!current.SetEquals(desired))
        {
            Log.Warning($"[Entry] familiar roster mismatch after notebook close current={string.Join(",", current.Order())} " +
                $"desired={string.Join(",", desired.Order())}.");
            return;
        }

        AdvanceEntry(EntryState.AwaitStageDetail);
    }

    private List<PetPartyRecord> ReadPetPartyRecords(AtkUnitBase* addon)
    {
        var result = new List<PetPartyRecord>();
        var count = Math.Clamp(ReadAtkInt(addon, 5), 0, FamiliarRosterCapacity(selectedBoard));
        const int recordStart = 6;
        const int recordStride = 77;
        for (var index = 0; index < count; index++)
        {
            var offset = recordStart + index * recordStride;
            if (offset + 1 >= addon->AtkValuesCount)
            {
                break;
            }

            var id = ReadAtkUInt(addon, offset + 1);
            if (id is >= 242001 and <= 242050)
            {
                result.Add(new PetPartyRecord
                {
                    RosterIndex = index,
                    FamiliarNumber = (int)(id - 242000),
                    Selected = ReadAtkBool(addon, offset + 2),
                    Name = ReadAtkString(addon, offset + 3),
                    CurrentHp = ReadAtkUInt(addon, offset + 5),
                    MaximumHp = ReadAtkUInt(addon, offset + 6),
                });
            }
        }

        return result;
    }

    private HashSet<int> ReadCurrentFamiliarNumbers(AtkUnitBase* addon)
        => ReadPetPartyRecords(addon).Select(record => record.FamiliarNumber).ToHashSet();

    private List<int>? ResolveHealthyStageFamiliarNumbers(
        List<PetPartyRecord> records,
        List<int> configured)
    {
        var threshold = PetHealthThresholdPercent;
        var configuredSet = configured.ToHashSet();
        var boardRoster = ConfiguredFamiliarNumbers.ToHashSet();
        var byNumber = records.ToDictionary(record => record.FamiliarNumber);
        var fallbackCandidates = records
            .Where(record => boardRoster.Contains(record.FamiliarNumber) &&
                !configuredSet.Contains(record.FamiliarNumber) && record.IsEligible(threshold))
            .ToList();
        var desired = new List<int>(configured.Count);
        var used = new HashSet<int>();

        foreach (var familiarNumber in configured)
        {
            if (byNumber.TryGetValue(familiarNumber, out var record) && record.IsEligible(threshold))
            {
                desired.Add(familiarNumber);
                used.Add(familiarNumber);
                continue;
            }

            var reason = byNumber.TryGetValue(familiarNumber, out record)
                ? $"hp={record.CurrentHp}/{record.MaximumHp} hpPercent={record.HealthPercent}"
                : "reason=not-in-live-roster";
            Log.Warning($"[Entry] move pet rejected number={familiarNumber} " +
                $"name=\"{EscapeLog(record?.Name ?? FamiliarName(familiarNumber))}\" threshold={threshold} {reason}.");

            var replacement = fallbackCandidates.FirstOrDefault(candidate => !used.Contains(candidate.FamiliarNumber));
            if (replacement == null)
            {
                Log.Error($"[Entry] move pet replacement unavailable rejected={familiarNumber} " +
                    $"threshold={threshold} effective={string.Join(",", desired)}.");
                FailEntry($"move-familiar-no-healthy-replacement:{familiarNumber}");
                return null;
            }

            desired.Add(replacement.FamiliarNumber);
            used.Add(replacement.FamiliarNumber);
            Log.Information($"[Entry] move pet replacement rejected={familiarNumber} " +
                $"replacement={replacement.FamiliarNumber} name=\"{EscapeLog(replacement.Name)}\" " +
                $"hp={replacement.CurrentHp}/{replacement.MaximumHp} hpPercent={replacement.HealthPercent} " +
                $"threshold={threshold}.");
        }

        Log.Information($"[Entry] move pet selection configured={string.Join(",", configured)} " +
            $"effective={string.Join(",", desired)} threshold={threshold}.");
        return desired;
    }

    private static List<NotebookFamiliarEntry> ReadNotebookFamiliarEntries(AtkUnitBase* addon)
    {
        const int recordStart = 22;
        const int recordStride = 8;
        var result = new List<NotebookFamiliarEntry>();
        var recordCount = Math.Min(NotebookPageSize, Math.Max(0, (addon->AtkValuesCount - recordStart) / recordStride));
        for (var index = 0; index < recordCount; index++)
        {
            var offset = recordStart + index * recordStride;
            var number = ReadAtkUInt(addon, offset + 2);
            var petId = ReadAtkUInt(addon, offset + 6);
            if (number is >= 1 and <= 50 && petId >= 242001 && petId <= 242050)
            {
                result.Add(new NotebookFamiliarEntry
                {
                    UiIndex = index,
                    Number = (int)number,
                    PetId = petId,
                });
            }
        }

        return result;
    }

    private void ProcessStagePetPartySelection()
    {
        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        var desired = petParty.StageDesiredNumbers?.ToHashSet();
        if (desired == null)
        {
            FailEntry("move-familiar-selection-not-prepared");
            return;
        }

        if (desired.Count != 3)
        {
            FailEntry($"move-familiar-roster-requires-3:{desired.Count}");
            return;
        }

        var current = ReadStageSelectedFamiliarNumbers(addon);
        petParty.StageToggleNumbers ??= current.Except(desired).Concat(desired.Except(current)).ToList();
        var toToggle = petParty.StageToggleNumbers;
        if (petParty.StageToggleIndex >= toToggle.Count)
        {
            if (!current.SetEquals(desired))
            {
                petParty.StageSelectionRetryCount++;
                Log.Warning($"[Entry] move familiar verification mismatch retry={petParty.StageSelectionRetryCount}/3 " +
                    $"current={string.Join(",", current.Order())} desired={string.Join(",", desired.Order())} " +
                    $"queued={string.Join(",", toToggle)}.");
                if (petParty.StageSelectionRetryCount >= 3)
                {
                    FailEntry($"move-familiar-verification-failed:current={string.Join(",", current.Order())}:desired={string.Join(",", desired.Order())}");
                    return;
                }

                petParty.StageToggleNumbers = null;
                petParty.StageToggleIndex = 0;
                AdvanceEntry(EntryState.SelectStagePetParty);
                return;
            }

            petParty.StageSelectionRetryCount = 0;
            AdvanceEntry(EntryState.AwaitInInstanceStageDetail);
            nextEntryAction = DateTime.UtcNow.AddSeconds(1);
            return;
        }

        var familiarNumber = toToggle[petParty.StageToggleIndex];
        var rosterIndex = FindPetPartyRosterIndex(addon, familiarNumber);
        if (rosterIndex < 0)
        {
            FailEntry($"move-familiar-not-in-board-roster:{familiarNumber}");
            return;
        }

        if (!TryFireCallback(addon, "XBMPetParty", $"toggle-stage-familiar-{familiarNumber}", 1, (uint)rosterIndex))
        {
            return;
        }
        Log.Information($"[Entry] move familiar toggle number={familiarNumber} rosterIndex={rosterIndex}.");
        petParty.StageToggleIndex++;
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
        AdvanceEntry(EntryState.SelectStagePetParty);
    }

    private HashSet<int> ReadStageSelectedFamiliarNumbers(AtkUnitBase* addon)
        => ReadPetPartyRecords(addon)
            .Where(record => record.Selected)
            .Select(record => record.FamiliarNumber)
            .ToHashSet();

    private static AtkComponentList* GetPetPartyList(AtkUnitBase* addon)
    {
        var node = GetPetPartyListNode(addon);
        if (node == null)
        {
            return null;
        }

        var component = ((AtkComponentNode*)node)->Component;
        return component != null && component->GetComponentType() == ComponentType.List
            ? (AtkComponentList*)component
            : null;
    }

    private static AtkResNode* GetPetPartyListNode(AtkUnitBase* addon)
    {
        const int listNodeIndex = 13;
        if (addon->UldManager.NodeList == null || addon->UldManager.NodeListCount <= listNodeIndex)
        {
            return null;
        }

        var node = addon->UldManager.NodeList[listNodeIndex];
        return node != null && node->NodeId == 11 && node->GetNodeType() == NodeType.Component ? node : null;
    }

    private static AtkResNode* FindRendererNode(AtkComponentListItemRenderer* renderer, uint nodeId)
    {
        if (renderer == null || renderer->UldManager.NodeList == null)
        {
            return null;
        }

        var count = Math.Min(renderer->UldManager.NodeListCount, (ushort)1000);
        for (var index = 0; index < count; index++)
        {
            var node = renderer->UldManager.NodeList[index];
            if (node == null)
            {
                continue;
            }

            if (node->NodeId == nodeId)
            {
                return node;
            }

            if (node->NodeId == 2 && node->GetNodeType() == NodeType.Component)
            {
                var component = ((AtkComponentNode*)node)->Component;
                var nested = component == null ? null : FindNodeById(&component->UldManager, nodeId, new HashSet<nint>());
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private int FindPetPartyRosterIndex(AtkUnitBase* addon, int familiarNumber)
    {
        return ReadPetPartyRecords(addon)
            .FirstOrDefault(record => record.FamiliarNumber == familiarNumber)?.RosterIndex ?? -1;
    }

    private static uint ReadPetPartyFamiliarId(AtkUnitBase* addon, int index)
    {
        const int recordStart = 6;
        const int recordStride = 77;
        return ReadAtkUInt(addon, recordStart + index * recordStride + 1);
    }

    private void ProcessInInstanceStageDetailAvailability()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Battle))
        {
            return;
        }

        if (!RequireExpectedRouteAddon("XBMStageDetailList"))
        {
            return;
        }

        if (GetReadyAddon("XBMStageDetailList") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.CommenceInInstanceStage);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void ProcessInInstanceCommence()
    {
        if (!RequireExpectedRouteAddon("XBMStageDetailList"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMStageDetailList");
        if (addon == null)
        {
            return;
        }

        if (!EnsureStageDetailMode(addon))
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMStageDetailList", "commence-stage-in-instance", 8))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitStageLoaded);
        movementConditionsClearSince = null;
        Log.Information($"[Entry] board={selectedBoard} move={CurrentRouteNode?.Name ?? "unknown"} commenced; waiting for move load.");
    }

    private bool EnsureStageDetailMode(AtkUnitBase* addon)
    {
        if (!TryReadStageDetailMode(addon, out var currentModeIndex))
        {
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(StageDetailModeSelectionDelayMilliseconds);
            return false;
        }

        var targetModeIndex = configuration.CrucibleModeIndex;
        if (currentModeIndex == targetModeIndex)
        {
            if (stageDetailModeAttempts > 0)
            {
                Log.Information($"[Entry] stage mode confirmed current={GetCrucibleModeName(currentModeIndex)} " +
                    $"target={CrucibleModeName} attempts={stageDetailModeAttempts}.");
            }

            ResetStageDetailModeSelection();
            return true;
        }

        var addonAddress = (nint)addon;
        if (stageDetailModeAddon != addonAddress)
        {
            stageDetailModeAddon = addonAddress;
            stageDetailModeAttempts = 0;
            stageDetailModeLastAction = DateTime.MinValue;
        }

        if (stageDetailModeAttempts >= StageDetailModeSelectionMaximumAttempts)
        {
            FailEntry($"stage-mode-selection-timeout:current={GetCrucibleModeName(currentModeIndex)}:" +
                $"target={CrucibleModeName}");
            return false;
        }

        if (DateTime.UtcNow - stageDetailModeLastAction <
            TimeSpan.FromMilliseconds(StageDetailModeSelectionDelayMilliseconds))
        {
            nextEntryAction = stageDetailModeLastAction.AddMilliseconds(StageDetailModeSelectionDelayMilliseconds);
            return false;
        }

        var callback = targetModeIndex > currentModeIndex ? 5 : 4;
        if (!TryFireCallback(addon, "XBMStageDetailList", $"select-crucible-mode-{callback}", callback))
        {
            return false;
        }
        stageDetailModeAttempts++;
        stageDetailModeLastAction = DateTime.UtcNow;
        nextEntryAction = stageDetailModeLastAction.AddMilliseconds(StageDetailModeSelectionDelayMilliseconds);
        Log.Information($"[Entry] stage mode change current={GetCrucibleModeName(currentModeIndex)} " +
            $"target={CrucibleModeName} direction={(callback == 5 ? "next" : "previous")} " +
            $"callback={callback} attempt={stageDetailModeAttempts}/{StageDetailModeSelectionMaximumAttempts}.");
        return false;
    }

    private void ResetStageDetailModeSelection()
    {
        stageDetailModeAddon = 0;
        stageDetailModeAttempts = 0;
        stageDetailModeLastAction = DateTime.MinValue;
    }

    private void ProcessStageLoaded()
    {
        if (HasEntryTransitionCondition())
        {
            movementConditionsClearSince = null;
            return;
        }

        movementConditionsClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - movementConditionsClearSince.Value < TimeSpan.FromSeconds(1))
        {
            return;
        }

        if (TryBeginItemUse(ItemUseTiming.InStagePreBattle))
        {
            Log.Information($"[Entry] move loaded; using in-move pre-battle items before {CurrentRouteNode?.Name ?? "battle"}.");
            return;
        }

        AdvanceEntry(EntryState.ActivateBossMod);
    }

    private void ProcessPrepareKnight()
    {
        var knight = GetKnightTarget();
        if (knight == null)
        {
            AdvanceEntry(EntryState.Combat);
            return;
        }

        SetCombatTarget(knight);
        if (!CanUseAction(SnarlActionId, knight.GameObjectId))
        {
            return;
        }

        var used = UseAction(SnarlActionId, knight.GameObjectId);
        Log.Information($"[Combat] Bone Knight rear action action={SnarlActionId} target={knight.GameObjectId:X} " +
            $"baseId={knight.BaseId} dispatch={used}.");
        if (used)
        {
            AdvanceEntry(EntryState.AwaitKnightAction);
            nextEntryAction = DateTime.UtcNow.AddSeconds(1);
        }
    }

    private void ProcessAwaitKnightAction()
    {
        knightRearMoveStarted = false;
        movementConditionsClearSince = null;
        AdvanceEntry(EntryState.MoveBehindKnight);
    }

    private void ProcessMoveBehindKnight()
    {
        var knight = GetKnightTarget();
        if (knight == null)
        {
            StopMovement();
            knightRearMoveStarted = false;
            knightSetupComplete = true;
            AdvanceEntry(EntryState.Combat);
            return;
        }

        SetCombatTarget(knight);
        if (knightRearMoveStarted)
        {
            if (TryMoveToDestination(knightRearDestination, "Bone Knight rear position",
                    "knight-rear-vnav-unavailable", CombatPositionArrivalRadius) == MovementResult.Arrived)
            {
                knightRearMoveStarted = false;
                knightSetupComplete = true;
                AdvanceEntry(EntryState.Combat);
            }

            return;
        }

        if (HasEntryTransitionCondition())
        {
            movementConditionsClearSince = null;
            return;
        }

        movementConditionsClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - movementConditionsClearSince.Value < TimeSpan.FromSeconds(1))
        {
            return;
        }

        var forward = new Vector3(MathF.Sin(knight.Rotation), 0f, MathF.Cos(knight.Rotation));
        knightRearDestination = knight.Position - (forward * 3f);
        var result = TryMoveToDestination(knightRearDestination, "Bone Knight rear position",
            "knight-rear-vnav-unavailable", CombatPositionArrivalRadius);
        if (result == MovementResult.Moving)
        {
            knightRearMoveStarted = true;
        }
        else if (result == MovementResult.Arrived)
        {
            knightSetupComplete = true;
            AdvanceEntry(EntryState.Combat);
        }
    }

    private void ProcessActivateBossMod()
    {
        if (ProcessBossModRecovery())
        {
            return;
        }

        var target = GetCombatTarget();
        if (target == null)
        {
            return;
        }

        SetCombatTarget(target);
        try
        {
            if (!EnsureBossModPreset())
            {
                FailEntry("bossmod-preset-creation-failed");
                return;
            }

            if (!bossModSetActive.InvokeFunc(BossModPresetName))
            {
                FailEntry("bossmod-preset-rejected");
                return;
            }

            bossModPresetActive = true;
            Log.Information($"[Combat] BossMod preset activated name={BossModPresetName}.");
            rsrChangeMode.InvokeAction(3);
            Log.Information("[Combat] RotationSolverReborn mode activated mode=Manual.");
            AdvanceEntry(EntryState.Combat);
        }
        catch (IpcNotReadyError ex)
        {
            if (TryStartBossModRecovery())
            {
                return;
            }

            FailEntry($"bossmod-ipc-unavailable:{ex.Message}");
        }
        catch (Exception ex)
        {
            FailEntry($"bossmod-unavailable:{ex.Message}");
        }
    }

    private void ProcessCombat()
    {
        UpdateSandstillStatus();
        if (ProcessActionTriggers())
        {
            return;
        }

        if (HasEntryTransitionCondition())
        {
            return;
        }

        if (selectedBoard == 3 && routePhase == RoutePhase.Stage4A)
        {
            if (HasLivingStage4APriorityPiece())
            {
                stage4APulletWaveObserved = true;
                stage4APulletReturnStarted = false;
            }
            else if (stage4APulletWaveObserved && !stage4APulletReturnStarted)
            {
                stage4APulletReturnStarted = true;
                StopMovement();
                AdvanceEntry(EntryState.MoveToStage4APriorityReturn);
                Log.Information("[Entry] Move 4A Pullet wave cleared; returning to priority position.");
                return;
            }
        }

        var target = GetCombatTarget();
        if (target == null)
        {
            if (HasLivingCombatTarget())
            {
                // Encounter targets can remain alive but temporarily untargetable.
                return;
            }

            if (sandstillArmed && !sandstillRestartHandled)
            {
                sandstillRestartHandled = true;
                TargetManager.Target = null;
                AdvanceEntry(EntryState.AwaitSandstillRestart, 0);
                Log.Information($"[Combat] Sandstill target reset detected move={CurrentRouteNode?.Key ?? "unknown"}; " +
                    "waiting for the move target to respawn.");
                return;
            }

            if (CurrentRouteNode?.DirectResultFlow == true)
            {
                ResetStageCombatProviders("move targets cleared");
                AdvanceEntry(EntryState.AwaitXbmResult);
                Log.Information("[Combat] Move 10 targets cleared; waiting for XBMResult.");
                return;
            }

            ResetStageCombatProviders("move targets cleared");
            Log.Information("[Combat] move targets cleared; beginning move exit.");
            AdvanceEntry(EntryState.AwaitContentsBotty);
            return;
        }

        if (selectedBoard == 1 && target.BaseId == 19334 && !knightSetupComplete)
        {
            AdvanceEntry(EntryState.PrepareKnight);
            return;
        }

        SetCombatTarget(target);
        if (TryUseCombatItems())
        {
            return;
        }

        if (selectedBoard == 3 && routePhase == RoutePhase.Stage2 && target.BaseId == 19605 &&
            !snarledSahaginTargets.Contains(target.GameObjectId) && TryUseCombatAction(SnarlActionId, target))
        {
            snarledSahaginTargets.Add(target.GameObjectId);
            Log.Information($"[Combat] Sahagin Piece Snarl action={SnarlActionId} target={target.GameObjectId:X} " +
                $"baseId={target.BaseId} dispatch=True.");
        }

        nextEntryAction = DateTime.UtcNow.AddMilliseconds(250);
    }

    private bool ProcessActionTriggers()
    {
        var node = CurrentRouteNode;
        if (!IsCurrentBattleRouteNode() || node == null || node.ActionTriggers.Length == 0)
        {
            return false;
        }

        return ProcessActionTriggersForNode(node);
    }

    private bool ProcessActionTriggersForNode(RouteNode node)
    {
        var activeCasts = new List<(IGameObject Source, uint ActionId)>();
        var activeCastKeys = new HashSet<(ulong ObjectId, uint ActionId)>();

        foreach (var gameObject in ObjectTable)
        {
            if (gameObject.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
                !gameObject.IsValid() || gameObject is not ICharacter character ||
                IsBattleNpcPet(gameObject) || character.Address == IntPtr.Zero)
            {
                continue;
            }

            var castInfo = ((Character*)character.Address)->GetCastInfo();
            if (castInfo == null || !castInfo->IsCasting || castInfo->ActionId == 0)
            {
                continue;
            }

            var key = (gameObject.GameObjectId, castInfo->ActionId);
            activeCasts.Add((gameObject, castInfo->ActionId));
            activeCastKeys.Add(key);
        }

        handledActionTriggers.RemoveWhere(key => !activeCastKeys.Contains(key));

        var movementSession = actionTriggerMovement;
        if (movementSession != null)
        {
            if (DateTime.UtcNow - movementSession.StartedAt >=
                TimeSpan.FromSeconds(ActionTriggerMovementTimeoutSeconds))
            {
                var timedOutKey = movementSession.TriggerKey;
                StopMovement();
                actionTriggerMovement = null;
                handledActionTriggers.Add(timedOutKey);
                Log.Warning($"[Combat] action-trigger response timed out response=MoveToPetPosition " +
                    $"sourceObjectId={timedOutKey.ObjectId:X} actionId={timedOutKey.ActionId}.");
                return true;
            }

            var movementResult = TryMoveToDestination(movementSession.Destination,
                "action-trigger familiar position", "action-trigger-vnav-unavailable", CombatPositionArrivalRadius);
            if (movementResult == MovementResult.Arrived)
            {
                var completedKey = movementSession.TriggerKey;
                actionTriggerMovement = null;
                handledActionTriggers.Add(completedKey);
                Log.Information($"[Combat] action-trigger response completed response=MoveToPetPosition " +
                    $"sourceObjectId={completedKey.ObjectId:X} actionId={completedKey.ActionId}.");
            }

            return true;
        }

        if (node.ActionTriggers.Length != 0)
        {
            foreach (var cast in activeCasts)
            {
                var trigger = node.ActionTriggers.FirstOrDefault(candidate =>
                    candidate.ActionId == cast.ActionId &&
                    (candidate.SourceBaseId == 0 || candidate.SourceBaseId == cast.Source.BaseId));
                if (trigger == null)
                {
                    continue;
                }

                var key = (cast.Source.GameObjectId, cast.ActionId);
                if (handledActionTriggers.Contains(key))
                {
                    continue;
                }

                var actionName = string.IsNullOrWhiteSpace(trigger.ActionName)
                    ? GetActionName(trigger.ActionId)
                    : trigger.ActionName;

                if (trigger.Response == ActionTriggerResponse.MoveToPetPosition)
                {
                    var familiar = GetOwnedFamiliar();
                    if (familiar == null)
                    {
                        return true;
                    }

                    actionTriggerMovement = new ActionTriggerMovementSession
                    {
                        TriggerKey = key,
                        Destination = familiar.Position,
                        StartedAt = DateTime.UtcNow,
                    };
                    Log.Information($"[Combat] action-trigger matched move={node.Key} source=" +
                        $"\"{EscapeLog(cast.Source.Name.ToString())}\" sourceBaseId={cast.Source.BaseId} " +
                        $"sourceObjectId={cast.Source.GameObjectId:X} actionId={cast.ActionId} " +
                        $"actionName=\"{EscapeLog(actionName)}\" response=MoveToPetPosition " +
                        $"familiarObjectId={familiar.GameObjectId:X} destination={FormatVector(familiar.Position)}.");
                    return true;
                }

                if (trigger.Response == ActionTriggerResponse.Snarl)
                {
                    var target = GetCombatTargetByBaseId(trigger.ResponseTargetBaseId);
                    if (target == null)
                    {
                        return true;
                    }

                    SetCombatTarget(target);
                    if (!TryUseCombatAction(trigger.ResponseActionId, target))
                    {
                        return true;
                    }

                    handledActionTriggers.Add(key);
                    Log.Information($"[Combat] action-trigger matched move={node.Key} source=" +
                        $"\"{EscapeLog(cast.Source.Name.ToString())}\" sourceBaseId={cast.Source.BaseId} " +
                        $"sourceObjectId={cast.Source.GameObjectId:X} actionId={cast.ActionId} " +
                        $"actionName=\"{EscapeLog(actionName)}\" response=Snarl " +
                        $"responseActionId={trigger.ResponseActionId} responseTargetObjectId={target.GameObjectId:X} " +
                        $"responseTargetBaseId={target.BaseId} dispatched=True.");
                    return true;
                }
            }
        }

        return false;
    }

    private void ProcessSandstillRestart()
    {
        if (DateTime.UtcNow - entryStateStarted >= TimeSpan.FromSeconds(SandstillRestartTimeoutSeconds))
        {
            FailEntry("sandstill-restart-target-timeout");
            return;
        }

        if (GetReadyAddon("XBMContentsBooty") != null)
        {
            sandstillArmed = false;
            ResetStageCombatProviders("Sandstill target loss resolved as move completion");
            AdvanceEntry(EntryState.AwaitContentsBotty, 0);
            Log.Information("[Combat] Sandstill target loss resolved by XBMContentsBooty; continuing move exit.");
            return;
        }

        if (HasEntryTransitionCondition())
        {
            return;
        }

        var target = GetCombatTarget();
        if (target == null)
        {
            return;
        }

        TargetManager.Target = null;
        ReassertCombatProvidersAfterSandstill();
        SetCombatTarget(target);
        sandstillArmed = false;
        AdvanceEntry(EntryState.Combat, 250);
        Log.Information($"[Combat] Sandstill restart target reacquired name=\"{EscapeLog(target.Name.ToString())}\" " +
            $"baseId={target.BaseId} objectId={target.GameObjectId:X}; resuming move combat.");
    }

    private void UpdateSandstillStatus()
    {
        if (sandstillArmed || sandstillRestartHandled || !HasPlayerStatus(SandstillStatusId))
        {
            return;
        }

        sandstillArmed = true;
        Log.Information($"[Combat] Sandstill armed statusId={SandstillStatusId} move={CurrentRouteNode?.Key ?? "unknown"}.");
    }

    private static bool HasPlayerStatus(uint statusId)
    {
        var player = ObjectTable.LocalPlayer;
        if (player is not ICharacter character || character.Address == IntPtr.Zero)
        {
            return false;
        }

        var nativeCharacter = (Character*)character.Address;
        var statusManager = nativeCharacter->GetStatusManager();
        return statusManager != null && statusManager->HasStatus(statusId);
    }

    private void ReassertCombatProvidersAfterSandstill()
    {
        if (bossModPresetCreated)
        {
            try
            {
                bossModPresetActive = bossModSetActive.InvokeFunc(BossModPresetName);
                if (!bossModPresetActive)
                {
                    Log.Warning($"[Combat] BossMod preset reassertion was rejected after Sandstill restart name={BossModPresetName}.");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[Combat] BossMod preset reassertion failed after Sandstill restart error={ex.GetType().Name}.");
            }
        }

        try
        {
            rsrChangeMode.InvokeAction(3);
            Log.Information("[Combat] RotationSolverReborn mode reasserted mode=Manual after Sandstill restart.");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Combat] RSR reassertion failed after Sandstill restart error={ex.GetType().Name}.");
        }
    }

    private void ProcessContentsBottyAvailability()
    {
        if (GetReadyAddon("XBMContentsBooty") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.ExitContentsBotty);
    }

    private void ProcessContentsBottyExit()
    {
        var addon = GetReadyAddon("XBMContentsBooty");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMContentsBooty", "close-contents-booty", 1))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitExitConfirmation);
    }

    private void ProcessExitConfirmationAvailability()
    {
        if (GetReadyAddon("SelectYesno") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.ConfirmExit);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void ProcessExitConfirmation()
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            return;
        }

        if (!TryGetSelectYesnoPrompt(addon, out var prompt))
        {
            return;
        }

        if (string.Equals(prompt, "Proceed to challenge this board?", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[Entry] rejected board-exit callback while challenge prompt is still open.");
            return;
        }

        if (!TryFireCallback(addon, "SelectYesno", "confirm-stage-exit", 0))
        {
            return;
        }
        if (IsRouteDrivenBoard && IsCurrentBattleRouteNode())
        {
            var completedNode = CurrentRouteNode;
            if (completedNode == null)
            {
                FailEntry("route-battle-node-missing");
                return;
            }
            var nextNode = ActiveRouteNodes.GetValueOrDefault(completedNode.NextPhase);
            if (nextNode == null || !SetNextRouteNode($"{completedNode.Name} complete"))
            {
                return;
            }

            var player = ObjectTable.LocalPlayer as ICharacter;
            var hp = player == null ? "unavailable" : $"{player.CurrentHp}/{player.MaxHp}";
            if (nextNode.Kind == RouteNodeKind.Rest)
            {
                Log.Information($"[Recovery] phase=post-move route={completedNode.Key} hp={hp} decision=defer-to-{nextNode.Key}-rest.");
                boardMovementTransitionObserved = false;
                boardMovementClearSince = null;
                boardMovementReady = false;
                vnavMoveStarted = false;
                movementConditionsClearSince = null;
                AdvanceEntry(EntryState.MoveToStage4BRest);
                Log.Information($"[Entry] {completedNode.Name} complete; moving to {nextNode.Name}.");
                return;
            }

            vnavMoveStarted = false;
            movementConditionsClearSince = null;
            if (TryBeginRecovery(EntryState.AwaitBoardExitTransitionStart))
            {
                Log.Information($"[Entry] {completedNode.Name} complete; checking recovery before {nextNode.Name}.");
                return;
            }

            AdvanceEntry(EntryState.AwaitBoardExitTransitionStart);
            Log.Information($"[Entry] {completedNode.Name} complete; waiting for transition to {nextNode.Name}.");
        }
        else
        {
            AdvanceEntry(EntryState.AwaitSecondBoardMainHud);
            Log.Information("[Entry] post-move results confirmed; waiting for contents exit.");
        }
    }

    private bool IsPlayerBelowPotionThreshold(int thresholdPercent)
    {
        var player = ObjectTable.LocalPlayer;
        if (player is not ICharacter character || character.MaxHp <= 0)
        {
            return false;
        }

        return (long)character.CurrentHp * 100 <= (long)character.MaxHp * thresholdPercent;
    }

    private bool TryBeginItemUse(ItemUseTiming timing)
    {
        if (itemUse.Active || recovery.Active)
        {
            return false;
        }

        var addon = GetReadyAddon("XBMContentsMainHUD");
        var candidate = addon == null ? null : FindNextItemUseCandidate(addon, timing);
        if (candidate == null)
        {
            return false;
        }

        itemUse.Clear();
        if (!TryDisableRsrForItemUse())
        {
            return false;
        }

        itemUse.Running = true;
        itemUse.Timing = timing;
        itemUse.Slot = candidate.Slot;
        itemUse.ItemId = candidate.ItemId;
        itemUse.ItemName = candidate.Name;
        itemUse.AttemptStarted = DateTime.UtcNow;
        Log.Information($"[Items] queue itemId={candidate.ItemId} slot={candidate.Slot} name=\"{EscapeLog(candidate.Name)}\" " +
            $"timing={timing} reportedHudUsable={candidate.HudUsable}.");
        return true;
    }

    private XbmHudItemRecord? FindNextItemUseCandidate(AtkUnitBase* addon, ItemUseTiming timing)
    {
        var combatTarget = timing == ItemUseTiming.Combat ? GetCombatTarget() : null;
        return ReadXbmHudItemRecords(addon)
            .Where(item => item.ItemId != 0 && item.Enabled &&
                !failedItemUseIds.Contains(item.ItemId) &&
                IsItemUseCandidate(item.ItemId, timing, combatTarget) &&
                (!ItemAttackIds.Contains(item.ItemId) || !usedAttackItemIds.Contains(item.ItemId)))
            .OrderBy(item => ItemAttackIds.Contains(item.ItemId) ? 1 : 0)
            .ThenBy(item => item.Slot)
            .FirstOrDefault();
    }

    private bool IsItemUseCandidate(uint itemId, ItemUseTiming timing,
        Dalamud.Game.ClientState.Objects.Types.IGameObject? combatTarget)
    {
        if (timing == ItemUseTiming.BoardPreStage)
        {
            return ItemBoardPreStageIds.Contains(itemId);
        }

        if (timing == ItemUseTiming.InStagePreBattle)
        {
            return ItemInStagePreBattleIds.Contains(itemId);
        }

        if (ItemSupportIds.Contains(itemId))
        {
            return true;
        }

        return ItemAttackIds.Contains(itemId) && combatTarget != null;
    }

    private void ProcessItemUse()
    {
        if (!itemUse.Active)
        {
            return;
        }

        if (itemUse.Timing == ItemUseTiming.Combat && GetReadyAddon("XBMResult") != null)
        {
            CancelItemUse("combat result appeared");
            ResetStageCombatProviders("combat result appeared during item use");
            AdvanceEntry(EntryState.AwaitXbmResult, 0);
            return;
        }

        var hud = GetReadyAddon("XBMContentsMainHUD");
        if (hud == null)
        {
            if (DateTime.UtcNow - itemUse.AttemptStarted >= TimeSpan.FromMilliseconds(ItemUseAttemptTimeoutMilliseconds))
            {
                RetryItemUse("HUD unavailable");
            }

            return;
        }

        var current = ReadXbmHudItem(hud, itemUse.Slot);
        if (current == null || current.ItemId != itemUse.ItemId)
        {
            var usedItemId = itemUse.ItemId;
            var usedItemName = itemUse.ItemName;
            if (ItemAttackIds.Contains(usedItemId))
            {
                usedAttackItemIds.Add(usedItemId);
            }

            Log.Information($"[Items] consumed itemId={usedItemId} slot={itemUse.Slot} name=\"{EscapeLog(usedItemName)}\" " +
                $"timing={itemUse.Timing}.");
            StartNextItemUseAttempt();
            return;
        }

        if (itemUse.Timing == ItemUseTiming.Combat && ItemAttackIds.Contains(itemUse.ItemId))
        {
            var target = GetCombatTarget();
            if (target == null)
            {
                RetryItemUse("combat target unavailable");
                return;
            }

            if (TargetManager.Target?.GameObjectId != target.GameObjectId)
            {
                SetCombatTarget(target);
                return;
            }
        }

        if (!itemUse.HudSelectionDispatched)
        {
            if (!TryFireCallback(hud, "XBMContentsMainHUD", $"select-item-{itemUse.ItemId}", 6, (uint)itemUse.Slot,
                    new AtkValue { Type = AtkValueType.Undefined }))
            {
                RetryItemUse("HUD callback rejected");
                return;
            }
            itemUse.HudSelectionDispatched = true;
            itemUse.AttemptStarted = DateTime.UtcNow;
            Log.Information($"[Items] itemId={itemUse.ItemId} slot={itemUse.Slot} HUD selection dispatched.");
            return;
        }

        if (!itemUse.ContextMenuDispatched)
        {
            var contextMenu = GetReadyAddon("ContextMenu");
            if (contextMenu == null)
            {
                if (DateTime.UtcNow - itemUse.AttemptStarted >= TimeSpan.FromMilliseconds(ItemUseAttemptTimeoutMilliseconds))
                {
                    RetryItemUse("ContextMenu unavailable");
                }

                return;
            }

            if (!TryGetContextMenuUseIndex(contextMenu, out var useIndex) ||
                !TryFireCallback(contextMenu, "ContextMenu", $"use-item-{itemUse.ItemId}", 0, useIndex, (uint)0,
                    new AtkValue { Type = AtkValueType.Undefined },
                    new AtkValue { Type = AtkValueType.Undefined }))
            {
                RetryItemUse("ContextMenu callback rejected");
                return;
            }
            itemUse.ContextMenuDispatched = true;
            itemUse.AttemptStarted = DateTime.UtcNow;
            Log.Information($"[Items] itemId={itemUse.ItemId} ContextMenu use callback dispatched.");
            return;
        }

        if (DateTime.UtcNow - itemUse.AttemptStarted >= TimeSpan.FromMilliseconds(ItemUseAttemptTimeoutMilliseconds))
        {
            RetryItemUse("item slot unchanged");
        }
    }

    private void StartNextItemUseAttempt()
    {
        var timing = itemUse.Timing;
        itemUse.Slot = -1;
        itemUse.ItemId = 0;
        itemUse.ItemName = string.Empty;
        itemUse.AttemptStarted = DateTime.MinValue;
        itemUse.Attempts = 0;
        itemUse.HudSelectionDispatched = false;
        itemUse.ContextMenuDispatched = false;

        var addon = GetReadyAddon("XBMContentsMainHUD");
        var candidate = addon == null ? null : FindNextItemUseCandidate(addon, timing);
        if (candidate == null)
        {
            FinishItemUse();
            return;
        }

        itemUse.Slot = candidate.Slot;
        itemUse.ItemId = candidate.ItemId;
        itemUse.ItemName = candidate.Name;
        itemUse.AttemptStarted = DateTime.UtcNow;
        Log.Information($"[Items] queue next itemId={candidate.ItemId} slot={candidate.Slot} " +
            $"name=\"{EscapeLog(candidate.Name)}\" timing={timing} reportedHudUsable={candidate.HudUsable}.");
    }

    private void RetryItemUse(string reason)
    {
        var itemId = itemUse.ItemId;
        itemUse.Attempts++;
        if (itemUse.Attempts >= ItemUseMaximumAttempts)
        {
            failedItemUseIds.Add(itemId);
            Log.Warning($"[Items] itemId={itemUse.ItemId} slot={itemUse.Slot} attempt={itemUse.Attempts}/{ItemUseMaximumAttempts} " +
                $"reason=\"{EscapeLog(reason)}\"; suppressing item for current route.");
            StartNextItemUseAttempt();
            return;
        }

        Log.Information($"[Items] itemId={itemUse.ItemId} slot={itemUse.Slot} attempt={itemUse.Attempts}/{ItemUseMaximumAttempts} " +
            $"reason=\"{EscapeLog(reason)}\"; retrying.");
        itemUse.HudSelectionDispatched = false;
        itemUse.ContextMenuDispatched = false;
        itemUse.AttemptStarted = DateTime.UtcNow;
    }

    private void FinishItemUse()
    {
        EnableRsrForItemUse();
        itemUse.Clear();
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(250);
    }

    private void CancelItemUse(string reason)
    {
        if (!itemUse.Active)
        {
            return;
        }

        EnableRsrForItemUse();
        Log.Information($"[Items] cancelled reason=\"{EscapeLog(reason)}\".");
        itemUse.Clear();
    }

    private bool TryDisableRsrForItemUse()
    {
        try
        {
            rsrChangeMode.InvokeAction(0);
            itemUse.RsrDisabled = true;
            Log.Information("[Items] RSR disabled before item use.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"[Items] RSR disable failed error={ex.GetType().Name}.");
            return false;
        }
    }

    private void EnableRsrForItemUse()
    {
        if (!itemUse.RsrDisabled)
        {
            return;
        }

        try
        {
            rsrChangeMode.InvokeAction(3);
            Log.Information("[Items] RSR re-enabled mode=Manual.");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Items] RSR re-enable failed error={ex.GetType().Name}.");
        }
    }

    private bool TryUseCombatItems()
        => TryBeginItemUse(ItemUseTiming.Combat);

    private bool TryBeginRecovery(EntryState continuation, int? thresholdOverride = null, int? targetOverride = null)
    {
        if (IsRouteDrivenBoard && routePhase != RoutePhase.None &&
            recoveryNoUsableItemsRoute == routePhase &&
            recoveryNoUsableItemsThreshold == (thresholdOverride ?? 60))
        {
            return false;
        }

        var now = DateTime.UtcNow;
        var isCombatCheck = continuation == EntryState.Combat;
        var logCheck = !isCombatCheck || now - recovery.LastCheckLog >= TimeSpan.FromSeconds(5);
        if (logCheck)
        {
            recovery.LastCheckLog = now;
        }

        var player = ObjectTable.LocalPlayer as ICharacter;
        if (player is not { MaxHp: > 0 })
        {
            if (logCheck)
            {
                Log.Information($"[Recovery] phase={(isCombatCheck ? "combat" : "post-move")} decision=player-unavailable.");
            }
            return false;
        }

        var hpPercent = (int)((long)player.CurrentHp * 100 / player.MaxHp);
        var thresholdPercent = thresholdOverride ?? 60;
        var targetPercent = targetOverride ?? 90;
        var next = thresholdPercent == 10 ? "Move 4B rest" : "next route";
        if (hpPercent > thresholdPercent)
        {
            if (logCheck)
            {
                Log.Information($"[Recovery] phase={(isCombatCheck ? "combat" : "post-move")} hp={player.CurrentHp}/{player.MaxHp} " +
                    $"percent={hpPercent} threshold={thresholdPercent} next=\"{next}\" decision=skip-above-threshold.");
            }
            return false;
        }

        if (logCheck)
        {
            Log.Information($"[Recovery] phase={(isCombatCheck ? "combat" : "post-move")} hp={player.CurrentHp}/{player.MaxHp} " +
                $"percent={hpPercent} threshold={thresholdPercent} next=\"{next}\" decision=inspect-content.");
        }
        recovery.Continuation = continuation;
        recovery.StartedAt = now;
        recovery.Attempt = 0;
        recovery.ThresholdPercent = thresholdPercent;
        recovery.TargetPercent = targetPercent;
        AdvanceEntry(EntryState.AwaitRecoveryMainHud, 0);
        return true;
    }

    private void ProcessRecoveryMainHud()
    {
        var addon = GetReadyAddon("XBMContentsMainHUD");
        if (addon == null)
        {
            if (DateTime.UtcNow - recovery.LastWaitLog >= TimeSpan.FromSeconds(2))
            {
                recovery.LastWaitLog = DateTime.UtcNow;
                Log.Information("[Recovery] waiting for XBMContentsMainHUD.");
            }
            return;
        }

        if (!TryGetHealingContentSlot(addon, out var slot, out var itemId, out var healPercent, out var itemName, out var usable))
        {
            if (IsRouteDrivenBoard)
            {
                recoveryNoUsableItemsRoute = routePhase;
                recoveryNoUsableItemsThreshold = recovery.ThresholdPercent;
            }

            Log.Information("[Recovery] decision=no-recovery-items; continuing without item use.");
            var continuation = recovery.Continuation;
            CompleteRecovery(continuation, "no usable recovery item");
            return;
        }

        if (recovery.Attempt == 0)
        {
            if (!recovery.RsrDisabled && !TryDisableRsr())
            {
                CompleteRecovery(recovery.Continuation, "RSR disable failed");
                return;
            }
        }

        recovery.Attempt++;
        recovery.SelectedSlot = slot;
        recovery.SelectedItemId = itemId;
        recovery.ItemAttempts[itemId] = recovery.ItemAttempts.TryGetValue(itemId, out var attempts)
            ? attempts + 1
            : 1;
        recovery.AttemptStarted = DateTime.UtcNow;
        if (!TryFireCallback(addon, "XBMContentsMainHUD", $"select-recovery-item-{itemId}", 6, (uint)slot,
                new AtkValue { Type = AtkValueType.Undefined }))
        {
            FailRecoveryAttempt("HUD callback rejected");
            return;
        }
        AdvanceEntry(EntryState.AwaitRecoveryContextMenu, 0);
        Log.Information($"[Recovery] attempt={recovery.Attempt} content-menu requested slot={slot} itemId={itemId} " +
            $"name=\"{EscapeLog(itemName)}\" healPercent={healPercent} hudUsable={usable} " +
            $"decision=attempt-anyway itemAttempt={recovery.ItemAttempts[itemId]}/{ItemUseMaximumAttempts}.");
    }

    private void ProcessRecoveryContextMenu()
    {
        var addon = GetReadyAddon("ContextMenu");
        if (addon == null)
        {
            if (DateTime.UtcNow - recovery.LastWaitLog >= TimeSpan.FromSeconds(2))
            {
                recovery.LastWaitLog = DateTime.UtcNow;
                Log.Information("[Recovery] waiting for ContextMenu after content selection.");
            }
            if (DateTime.UtcNow - recovery.AttemptStarted >= TimeSpan.FromSeconds(2))
            {
                FailRecoveryAttempt("ContextMenu unavailable");
            }
            return;
        }

        Log.Information($"[Recovery] context-menu available=true atkValuesCount={addon->AtkValuesCount}.");

        if (!TryGetContextMenuUseIndex(addon, out var useIndex) ||
            !TryFireCallback(addon, "ContextMenu", $"use-recovery-item-{recovery.SelectedItemId}", 0, useIndex, (uint)0,
                new AtkValue { Type = AtkValueType.Undefined },
                new AtkValue { Type = AtkValueType.Undefined }))
        {
            FailRecoveryAttempt("ContextMenu callback rejected");
            return;
        }
        AdvanceEntry(EntryState.AwaitRecoveryHp, 0);
        Log.Information($"[Recovery] attempt={recovery.Attempt} context-menu use callback dispatched; polling item slot.");
    }

    private void ProcessRecoveryHp()
    {
        var addon = GetReadyAddon("XBMContentsMainHUD");
        var current = addon == null ? null : ReadXbmHudItem(addon, recovery.SelectedSlot);
        if (addon != null && (current == null || current.ItemId != recovery.SelectedItemId))
        {
            Log.Information($"[Recovery] attempt={recovery.Attempt} item consumed slot={recovery.SelectedSlot} " +
                $"itemId={recovery.SelectedItemId}; success.");
            CompleteRecovery(recovery.Continuation, "item consumed");
            return;
        }

        if (DateTime.UtcNow - recovery.AttemptStarted < TimeSpan.FromSeconds(2))
        {
            return;
        }

        FailRecoveryAttempt("item slot unchanged");
    }

    private void FailRecoveryAttempt(string reason)
    {
        var attempts = recovery.ItemAttempts.TryGetValue(recovery.SelectedItemId, out var count) ? count : recovery.Attempt;
        if (attempts >= ItemUseMaximumAttempts)
        {
            Log.Warning($"[Recovery] attempt={recovery.Attempt} slot={recovery.SelectedSlot} itemId={recovery.SelectedItemId} " +
                $"itemAttempt={attempts}/{ItemUseMaximumAttempts} reason=\"{EscapeLog(reason)}\"; item exhausted.");
        }
        else
        {
            Log.Information($"[Recovery] attempt={recovery.Attempt} slot={recovery.SelectedSlot} itemId={recovery.SelectedItemId} " +
                $"itemAttempt={attempts}/{ItemUseMaximumAttempts} reason=\"{EscapeLog(reason)}\"; retrying.");
        }

        AdvanceEntry(EntryState.AwaitRecoveryMainHud, 0);
    }

    private bool TryDisableRsr()
    {
        try
        {
            rsrChangeMode.InvokeAction(0);
            recovery.RsrDisabled = true;
            Log.Information("[Recovery] RSR disabled before item use.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning($"[Recovery] RSR disable failed error={ex.GetType().Name}.");
            return false;
        }
    }

    private void EnableRsr()
    {
        if (!recovery.RsrDisabled)
        {
            return;
        }

        try
        {
            rsrChangeMode.InvokeAction(3);
            Log.Information("[Recovery] RSR re-enabled mode=Manual.");
        }
        catch (Exception ex)
        {
            Log.Warning($"[Recovery] RSR re-enable failed error={ex.GetType().Name}.");
        }
    }

    private void CompleteRecovery(EntryState continuation, string reason)
    {
        EnableRsr();
        Log.Information($"[Recovery] complete reason=\"{EscapeLog(reason)}\" continuation={continuation}.");
        recovery.Clear();
        AdvanceEntry(continuation, 0);
    }

    private void CancelRecovery(string reason)
    {
        if (!recovery.Active)
        {
            return;
        }

        EnableRsr();
        Log.Information($"[Recovery] cancelled reason=\"{EscapeLog(reason)}\".");
        recovery.Clear();
    }

    private static List<XbmHudItemRecord> ReadXbmHudItemRecords(AtkUnitBase* addon)
    {
        var result = new List<XbmHudItemRecord>();
        if (addon == null || addon->AtkValues == null)
        {
            return result;
        }

        const int firstSlotValue = 11;
        const int valuesPerSlot = 5;
        var slotCount = Math.Min(10, Math.Max(0, (addon->AtkValuesCount - firstSlotValue) / valuesPerSlot));
        for (var slot = 0; slot < slotCount; slot++)
        {
            var offset = firstSlotValue + slot * valuesPerSlot;
            result.Add(new XbmHudItemRecord
            {
                Slot = slot,
                ItemId = ReadAtkUInt(addon, offset),
                RowId = ReadAtkUInt(addon, offset + 1),
                Name = ReadAtkString(addon, offset + 2),
                Enabled = ReadAtkBool(addon, offset + 3),
                HudUsable = ReadAtkBool(addon, offset + 4),
            });
        }

        return result;
    }

    private static XbmHudItemRecord? ReadXbmHudItem(AtkUnitBase* addon, int slot)
        => ReadXbmHudItemRecords(addon).FirstOrDefault(item => item.Slot == slot);

    private bool TryGetHealingContentSlot(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon,
        out int slot, out uint itemId, out int healPercent, out string itemName, out bool usable)
    {
        slot = -1;
        itemId = 0;
        healPercent = 0;
        itemName = string.Empty;
        usable = false;
        var player = ObjectTable.LocalPlayer as ICharacter;
        if (player == null || player.MaxHp <= 0)
        {
            return false;
        }

        var currentPercent = (int)((long)player.CurrentHp * 100 / player.MaxHp);
        var targetPercent = recovery.TargetPercent;
        var requiredHeal = Math.Max(1, targetPercent - currentPercent);
        var candidates = new List<(int Slot, uint ItemId, int Heal, string Name, bool Ash, bool Usable)>();

        for (var contentSlot = 0; contentSlot < 10; contentSlot++)
        {
            var offset = 11 + contentSlot * 5;
            var liveItemId = ReadAtkUInt(addon, offset);
            var rowId = ReadAtkUInt(addon, offset + 1);
            var name = ReadAtkString(addon, offset + 2);
            var enabled = ReadAtkBool(addon, offset + 3);
            var hudUsable = ReadAtkBool(addon, offset + 4);
            var knownRecovery = RecoveryItems.TryGetValue(liveItemId, out var recovery);
            Log.Information($"[Recovery] slot={contentSlot} itemId={liveItemId} rowId={rowId} name=\"{EscapeLog(name)}\" " +
                $"enabled={enabled} usable={hudUsable} knownRecovery={knownRecovery} " +
                $"healPercent={(knownRecovery ? recovery.HealPercent : 0)}.");
            if (liveItemId == 0 || !enabled || !knownRecovery)
            {
                continue;
            }

            candidates.Add((contentSlot, liveItemId, recovery.HealPercent, name, recovery.IsAsh, hudUsable));
        }

        var availableCandidates = candidates
            .Where(candidate => !recovery.ItemAttempts.TryGetValue(candidate.ItemId, out var attempts) ||
                attempts < ItemUseMaximumAttempts)
            .ToList();

        var selected = availableCandidates
            .Where(candidate => candidate.Heal >= requiredHeal)
            .OrderBy(candidate => candidate.Heal)
            .ThenByDescending(candidate => candidate.Ash)
            .FirstOrDefault();
        if (selected.ItemId == 0)
        {
            selected = availableCandidates.OrderByDescending(candidate => candidate.Heal).ThenByDescending(candidate => candidate.Ash).FirstOrDefault();
        }

        if (selected.ItemId == 0)
        {
            return false;
        }

        slot = selected.Slot;
        itemId = selected.ItemId;
        healPercent = selected.Heal;
        itemName = selected.Name;
        usable = selected.Usable;
        return true;
    }

    private void ProcessMoveToStage4APriorityReturn()
    {
        if (selectedBoard != 3 || routePhase != RoutePhase.Stage4A)
        {
            stage4APulletWaveObserved = false;
            stage4APulletReturnStarted = false;
            AdvanceEntry(EntryState.Combat, 0);
            return;
        }

        if (HasEntryTransitionCondition())
        {
            return;
        }

        if (HasLivingStage4APriorityPiece())
        {
            StopMovement();
            stage4APulletReturnStarted = false;
            AdvanceEntry(EntryState.Combat, 0);
            Log.Information("[Entry] Move 4A Pullet wave appeared during return; resuming combat.");
            return;
        }

        if (TryMoveToDestination(Stage4APriorityReturnPosition, "Move 4A priority return",
                "stage4a-priority-return-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            stage4APulletWaveObserved = false;
            stage4APulletReturnStarted = false;
            AdvanceEntry(EntryState.Combat);
            Log.Information($"[Entry] Move 4A priority return reached destination={FormatVector(Stage4APriorityReturnPosition)}.");
        }
    }

    private void ProcessMoveToStage4BRest()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Rest))
        {
            return;
        }

        if (ClientTerritoryId != BoardTerritory)
        {
            FailEntry("territory-changed");
            return;
        }

        if (GetReadyAddon("XBMPetParty") != null)
        {
            StopMovement();
            AdvanceEntry(EntryState.AwaitStage4BTent);
            return;
        }

        if (HasEntryTransitionCondition())
        {
            boardMovementClearSince = null;
            return;
        }

        boardMovementClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - boardMovementClearSince.Value < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var destination = CurrentRouteNode?.Position ?? new Vector3(-694.744f, 0f, -33.627f);
        if (TryMoveToDestination(destination, "Move 4B rest", "stage4b-rest-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            AdvanceEntry(EntryState.AwaitStage4BTent);
        }
    }

    private void ProcessStage4BTentAvailability()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Rest))
        {
            return;
        }

        if (GetReadyAddon("XBMPetParty") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.RestStage4B);
    }

    private void ProcessStage4BRest()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Rest))
        {
            return;
        }

        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMPetParty", "open-rest", 3))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitStage4BConfirmation);
        Log.Information("[Entry] Move 4B rest dispatched callback=3; waiting for confirmation.");
    }

    private void ProcessStage4BConfirmation()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Rest))
        {
            return;
        }

        var addon = GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            return;
        }

        if (!TryGetSelectYesnoPrompt(addon, out var prompt))
        {
            return;
        }

        if (prompt.Contains("Proceed to challenge this board?", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[Entry] rejected rest confirmation while challenge prompt is open.");
            return;
        }

        if (!TryFireCallback(addon, "SelectYesno", "confirm-rest", 0))
        {
            return;
        }
        if (!SetNextRouteNode("Move 4B rest confirmed"))
        {
            return;
        }
        boardMovementTransitionObserved = false;
        boardMovementClearSince = null;
        boardMovementReady = false;
        vnavMoveStarted = false;
        movementConditionsClearSince = null;
        AdvanceEntry(EntryState.AwaitBoardExitTransitionStart);
        Log.Information("[Entry] Move 4B rest confirmed; waiting for transition to Move 5.");
    }

    private void ProcessMoveToRandomOutcome()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Random))
        {
            return;
        }

        if (ClientTerritoryId != BoardTerritory)
        {
            FailEntry("territory-changed");
            return;
        }

        if (TryBeginRandomOutcome())
        {
            return;
        }

        if (HasEntryTransitionCondition())
        {
            movementConditionsClearSince = null;
            return;
        }

        movementConditionsClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - movementConditionsClearSince.Value < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var destination = CurrentRouteNode?.Position ?? Vector3.Zero;
        if (TryMoveToDestination(destination, CurrentRouteNode?.Name ?? "random outcome",
                "random-outcome-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            AdvanceEntry(EntryState.AwaitRandomOutcome);
        }
    }

    private void ProcessRandomOutcomeAvailability()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Random))
        {
            return;
        }

        TryBeginRandomOutcome();
    }

    private bool TryBeginRandomOutcome()
    {
        var randomNode = CurrentRouteNode;
        if (randomNode?.Kind != RouteNodeKind.Random)
        {
            return false;
        }

        if (GetReadyAddon("XBMContentsTreasure") != null)
        {
            randomOutcome = RandomOutcomeKind.Treasure;
            StopMovement();
            AdvanceEntry(EntryState.AwaitStage6Treasure);
            Log.Information($"[Entry] {randomNode.Name} random outcome=treasure addon=XBMContentsTreasure.");
            return true;
        }

        if (GetReadyAddon("XBMPetParty") != null)
        {
            randomOutcome = RandomOutcomeKind.Battle;
            StopMovement();
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
            Log.Information($"[Entry] {randomNode.Name} random outcome=battle addon=XBMPetParty " +
                $"targets={string.Join(",", randomNode.CombatTargetBaseIds)} priority={randomNode.PriorityTargetBaseId}.");
            return true;
        }

        return false;
    }

    private void ProcessMoveToStage6Start()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Treasure))
        {
            return;
        }

        if (ClientTerritoryId != BoardTerritory)
        {
            FailEntry("territory-changed");
            return;
        }

        if (GetReadyAddon("XBMContentsTreasure") != null)
        {
            StopMovement();
            AdvanceEntry(EntryState.AwaitStage6Treasure);
            return;
        }

        if (HasEntryTransitionCondition())
        {
            movementConditionsClearSince = null;
            return;
        }

        movementConditionsClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - movementConditionsClearSince.Value < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var destination = CurrentRouteNode?.Position ?? new Vector3(-700.365f, 0f, -50.835f);
        if (TryMoveToDestination(destination, "Move 6 treasure", "stage6-treasure-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            AdvanceEntry(EntryState.AwaitStage6Treasure);
        }
    }

    private void ProcessMoveToStage10Start()
    {
        if (ClientTerritoryId != BoardTerritory)
        {
            FailEntry("territory-changed");
            return;
        }

        if (GetReadyAddon("XBMPetParty") != null)
        {
            StopMovement();
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
            return;
        }

        if (HasEntryTransitionCondition())
        {
            movementConditionsClearSince = null;
            return;
        }

        movementConditionsClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - movementConditionsClearSince.Value < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var destination = CurrentRouteNode?.Position ?? new Vector3(-699.314f, 0f, -83.571f);
        if (TryMoveToDestination(destination, "Move 10", "stage10-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
        }
    }

    private void ProcessStage6TreasureAvailability()
        => ProcessTreasureAvailability(EntryState.SelectStage6Treasure);

    private void ProcessStage6TreasureSelection()
        => ProcessTreasureSelection(EntryState.AwaitStage6TreasureConfirmation);

    private void ProcessStage6TreasureConfirmation()
        => ProcessTreasureConfirmation();

    private void ProcessTreasureAvailability(EntryState selectionState)
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Treasure))
        {
            return;
        }

        if (!RequireExpectedRouteAddon("XBMContentsTreasure"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMContentsTreasure");
        if (addon == null)
        {
            return;
        }

        if (treasure.SelectionState != selectionState)
        {
            treasure.Clear();
            treasure.SelectionState = selectionState;
            treasure.DataFirstObservedAt = DateTime.UtcNow;
            Log.Information($"[Entry] {TreasureRouteName()} treasure opened; polling item data before selection.");
        }

        var snapshot = ReadTreasureDataSnapshot(addon);
        var dataStable = PollTreasureData(snapshot, out var dataTimedOut);
        if (!snapshot.CardsReady)
        {
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(TreasureDataPollMilliseconds);
            return;
        }

        var hasKnife = snapshot.KnifeValueIndexes.Count > 0;
        if (!dataStable && !dataTimedOut)
        {
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(TreasureDataPollMilliseconds);
            return;
        }

        if (!hasKnife && !dataTimedOut)
        {
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(TreasureDataPollMilliseconds);
            return;
        }

        treasure.RequiredSelections = hasKnife ? 2 : 1;
        treasure.PostConfirmationRescanPending = !hasKnife;
        Log.Information($"[Entry] {TreasureRouteName()} treasure data stabilized " +
            $"polls={treasure.DataPollCount} stablePolls={treasure.StableDataPolls} " +
            $"knifeDetected={hasKnife} knifeIndexes={string.Join(",", snapshot.KnifeValueIndexes)} " +
            $"requiredSelections={treasure.RequiredSelections} provisional={!hasKnife}.");
        AdvanceEntry(selectionState);
    }

    private void ProcessTreasureSelection(EntryState confirmationState)
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Treasure))
        {
            return;
        }

        if (!RequireExpectedRouteAddon("XBMContentsTreasure"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMContentsTreasure");
        if (addon == null)
        {
            return;
        }

        if (GetReadyAddon("SelectYesno") != null)
        {
            return;
        }

        var cards = ResolveTreasureCards(addon);
        if (!AreTreasureCardsReady(cards))
        {
            Log.Debug($"[Entry] {TreasureRouteName()} treasure cards are not populated yet; polling before selection.");
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(TreasureDataPollMilliseconds);
            return;
        }

        LogTreasureChoices(cards);
        var selected = SelectTreasureCard(cards, treasure.UsedOptionIndexes);
        var optionIndex = selected?.OptionIndex ?? 0;
        if (optionIndex < 0)
        {
            optionIndex = 0;
        }

        if (!TryFireCallback(addon, "XBMContentsTreasure", $"select-treasure-{optionIndex}", 2, optionIndex))
        {
            return;
        }
        treasure.UsedOptionIndexes.Add(optionIndex);
        treasure.SelectedOptionIndex = optionIndex;
        treasure.SelectedItemId = selected?.ItemId ?? 0;
        treasure.Attempt = 1;
        treasure.OptionRequestedAt = DateTime.UtcNow;
        AdvanceEntry(confirmationState);
        Log.Information($"[Entry] {TreasureRouteName()} selection={treasure.CompletedSelections + 1}/{treasure.RequiredSelections} " +
            $"option={optionIndex} itemId={treasure.SelectedItemId} name=\"{EscapeLog(selected?.Title ?? "unknown")}\" " +
            $"reason={GetTreasureSelectionReason(selected)} gearTier={GetTreasureGearTier(selected)}; " +
            "waiting up to 2 seconds for confirmation.");
    }

    private void ProcessTreasureConfirmation()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Treasure))
        {
            return;
        }

        if (treasure.PostConfirmationRescanActive)
        {
            ProcessTreasurePostConfirmationRescan();
            return;
        }

        var selectYesno = GetReadyAddon("SelectYesno");
        if (selectYesno != null)
        {
            if (!TryGetSelectYesnoPrompt(selectYesno, out var prompt))
            {
                return;
            }

            if (prompt.Contains("Proceed to challenge this board?", StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("[Entry] rejected treasure confirmation while challenge prompt is open.");
                return;
            }

            if (!TryFireCallback(selectYesno, "SelectYesno", "confirm-treasure", 0))
            {
                return;
            }
            treasure.CompletedSelections++;
            if (treasure.CompletedSelections < treasure.RequiredSelections)
            {
                treasure.Attempt = 0;
                treasure.OptionRequestedAt = DateTime.MinValue;
                AdvanceEntry(treasure.SelectionState, 500);
                Log.Information($"[Entry] {TreasureRouteName()} selection {treasure.CompletedSelections}/{treasure.RequiredSelections} confirmed; waiting for next treasure selection.");
            }
            else if (treasure.PostConfirmationRescanPending)
            {
                BeginTreasurePostConfirmationRescan();
            }
            else
            {
                CompleteTreasure();
            }

            return;
        }

        var treasureAddon = GetReadyAddon("XBMContentsTreasure");
        if (treasureAddon == null)
        {
            return;
        }

        var elapsed = DateTime.UtcNow - treasure.OptionRequestedAt;
        if (treasure.Attempt == 1 && elapsed >= TimeSpan.FromSeconds(2))
        {
            var previousOption = treasure.SelectedOptionIndex;
            if (!TryFireCallback(treasureAddon, "XBMContentsTreasure", "select-treasure-fallback-1", 2, 1))
            {
                return;
            }
            treasure.UsedOptionIndexes.Add(1);
            treasure.SelectedOptionIndex = 1;
            treasure.SelectedItemId = ResolveTreasureCards(treasureAddon)
                .FirstOrDefault(card => card.OptionIndex == 1)?.ItemId ?? 0;
            treasure.Attempt = 2;
            treasure.OptionRequestedAt = DateTime.UtcNow;
            Log.Information($"[Entry] {TreasureRouteName()} option {previousOption} produced no confirmation; " +
                "selected fallback option 1.");
            return;
        }

        if (treasure.Attempt == 2 && elapsed >= TimeSpan.FromSeconds(2))
        {
            FailEntry($"{TreasureRouteName().ToLowerInvariant()}-confirmation-timeout");
        }
    }

    private void BeginTreasurePostConfirmationRescan()
    {
        treasure.PostConfirmationRescanPending = false;
        treasure.PostConfirmationRescanActive = true;
        treasure.DataPollCount = 0;
        treasure.StableDataPolls = 0;
        treasure.DataFirstObservedAt = DateTime.UtcNow;
        treasure.LastDataSignature = string.Empty;
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(TreasureDataPollMilliseconds);
        Log.Information($"[Entry] {TreasureRouteName()} selection {treasure.CompletedSelections}/{treasure.RequiredSelections} " +
            "confirmed; rescanning gear and item data for a late Thief's Knife.");
    }

    private void ProcessTreasurePostConfirmationRescan()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Treasure))
        {
            return;
        }

        var addon = GetReadyAddon("XBMContentsTreasure");
        if (addon == null)
        {
            treasure.PostConfirmationRescanActive = false;
            Log.Information($"[Entry] {TreasureRouteName()} treasure closed during fallback rescan; completing one-selection treasure.");
            CompleteTreasure();
            return;
        }

        var snapshot = ReadTreasureDataSnapshot(addon);
        var dataStable = PollTreasureData(snapshot, out var dataTimedOut);
        if ((dataStable || dataTimedOut) && snapshot.KnifeValueIndexes.Count > 0)
        {
            treasure.RequiredSelections = 2;
            treasure.PostConfirmationRescanActive = false;
            Log.Information($"[Entry] {TreasureRouteName()} late Thief's Knife detected " +
                $"knifeIndexes={string.Join(",", snapshot.KnifeValueIndexes)}; continuing with second selection.");
            AdvanceEntry(treasure.SelectionState, 500);
            return;
        }

        if (!dataTimedOut)
        {
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(TreasureDataPollMilliseconds);
            return;
        }

        treasure.PostConfirmationRescanActive = false;
        Log.Information($"[Entry] {TreasureRouteName()} fallback rescan complete " +
            $"knifeDetected={snapshot.KnifeValueIndexes.Count > 0}; completing one-selection treasure.");
        CompleteTreasure();
    }

    private string TreasureRouteName()
        => IsCurrentTreasureRouteNode()
            ? CurrentRouteNode?.Name ?? "Unexpected treasure"
            : "Unexpected treasure";

    private void CompleteTreasure()
    {
        var route = routePhase;
        var currentNode = CurrentRouteNode;
        var nextNode = currentNode == null ? null : ActiveRouteNodes.GetValueOrDefault(currentNode.NextPhase);
        treasure.Clear();
        ResetMovement();
        if (currentNode == null || !IsCurrentTreasureRouteNode() || nextNode == null ||
            !SetNextRouteNode($"{currentNode.Name} complete"))
        {
            FailEntry($"unexpected-treasure-route:{route}");
            return;
        }

        switch (nextNode.Kind)
        {
            case RouteNodeKind.Battle:
                AdvanceEntry(EntryState.MoveToStageStart);
                Log.Information($"[Entry] {currentNode.Name} confirmed; moving to {nextNode.Name}.");
                break;
            case RouteNodeKind.Shop:
                AdvanceEntry(EntryState.MoveToSecondBoardShop);
                Log.Information($"[Entry] {currentNode.Name} confirmed; moving to {nextNode.Name}.");
                break;
            case RouteNodeKind.Rest:
                AdvanceEntry(EntryState.MoveToStage4BRest);
                Log.Information($"[Entry] {currentNode.Name} confirmed; moving to {nextNode.Name}.");
                break;
            default:
                FailEntry($"treasure-next-node-invalid:{nextNode.Key}:{nextNode.Kind}");
                break;
        }
    }

    private void ProcessMoveToStage8Start()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Treasure))
        {
            return;
        }

        if (ClientTerritoryId != BoardTerritory)
        {
            FailEntry("territory-changed");
            return;
        }

        if (GetReadyAddon("XBMContentsTreasure") != null)
        {
            StopMovement();
            AdvanceEntry(EntryState.AwaitStage8Treasure);
            return;
        }

        if (HasEntryTransitionCondition())
        {
            movementConditionsClearSince = null;
            return;
        }

        movementConditionsClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - movementConditionsClearSince.Value < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var destination = CurrentRouteNode?.Position ?? new Vector3(-695.762f, 0f, -66.826f);
        if (TryMoveToDestination(destination, "Move 8 treasure", "stage8-treasure-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            AdvanceEntry(EntryState.AwaitStage8Treasure);
        }
    }

    private void ProcessStage8TreasureAvailability()
        => ProcessTreasureAvailability(EntryState.SelectStage8Treasure);

    private void ProcessStage8TreasureSelection()
        => ProcessTreasureSelection(EntryState.AwaitStage8TreasureConfirmation);

    private void ProcessStage8TreasureConfirmation()
        => ProcessTreasureConfirmation();

    private void ProcessBoardExitTransitionStart()
    {
        if (!HasEntryTransitionCondition())
        {
            return;
        }

        movementConditionsClearSince = null;
        preStageItemsHandled = false;
        AdvanceEntry(EntryState.AwaitBoardExitTransitionReady);
        Log.Information("[Entry] Second Board exit transition observed; waiting for map readiness.");
    }

    private void ProcessBoardExitTransitionReady()
    {
        if (HasEntryTransitionCondition())
        {
            movementConditionsClearSince = null;
            return;
        }

        movementConditionsClearSince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - movementConditionsClearSince.Value < TimeSpan.FromSeconds(2))
        {
            return;
        }

        var node = CurrentRouteNode;
        switch (node?.Kind)
        {
            case RouteNodeKind.Battle:
                if (!preStageItemsHandled)
                {
                    preStageItemsHandled = true;
                    if (TryBeginItemUse(ItemUseTiming.BoardPreStage))
                    {
                        Log.Information($"[Entry] transition ready; using pre-move items before {node.Name}.");
                        return;
                    }

                    Log.Information($"[Entry] transition ready; no pre-move items available for {node.Name}.");
                }

                boardMovementTransitionObserved = false;
                boardMovementClearSince = null;
                boardMovementReady = false;
                vnavMoveStarted = false;
                if (node.DirectResultFlow)
                {
                    AdvanceEntry(EntryState.AwaitStageLoaded);
                    Log.Information($"[Entry] pre-move items complete; waiting for {node.Name} map to settle.");
                    break;
                }

                AdvanceEntry(EntryState.MoveToStageStart);
                Log.Information($"[Entry] pre-move items complete; moving to {node.Name}.");
                break;
            case RouteNodeKind.Treasure:
                boardMovementTransitionObserved = false;
                boardMovementClearSince = null;
                boardMovementReady = false;
                vnavMoveStarted = false;
                AdvanceEntry(EntryState.MoveToStage6Start);
                Log.Information($"[Entry] transition ready; moving to {node.Name} spot.");
                break;
            case RouteNodeKind.Shop:
                boardMovementTransitionObserved = false;
                boardMovementClearSince = null;
                boardMovementReady = false;
                vnavMoveStarted = false;
                AdvanceEntry(EntryState.MoveToSecondBoardShop);
                Log.Information($"[Entry] transition ready; moving to {node.Name}.");
                break;
            case RouteNodeKind.Random:
                boardMovementTransitionObserved = false;
                boardMovementClearSince = null;
                boardMovementReady = false;
                vnavMoveStarted = false;
                AdvanceEntry(EntryState.MoveToRandomOutcome);
                Log.Information($"[Entry] transition ready; moving to {node.Name}.");
                break;
            default:
                FailEntry($"route-node-transition-handler-missing:{node?.Key ?? "unknown"}");
                break;
        }
    }

    private void ProcessMoveToSecondBoardShop()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Shop))
        {
            return;
        }

        if (ClientTerritoryId != BoardTerritory)
        {
            return;
        }

        if (GetReadyAddon("XBMContentsItemShop") != null)
        {
            StopMovement();
            AdvanceEntry(EntryState.AwaitSecondBoardShop);
            return;
        }

        var destination = CurrentRouteNode?.Position ?? SecondBoardShopPosition;
        if (!boardMovementReady)
        {
            if (HasEntryTransitionCondition())
            {
                boardMovementClearSince = null;
                return;
            }

            boardMovementClearSince ??= DateTime.UtcNow;
            if (DateTime.UtcNow - boardMovementClearSince.Value < TimeSpan.FromSeconds(2))
            {
                return;
            }

            boardMovementReady = true;
        }

        if (HasEntryTransitionCondition())
        {
            boardMovementTransitionObserved = true;
            boardMovementClearSince = null;
            StopMovement();
            return;
        }

        if (boardMovementTransitionObserved)
        {
            AdvanceEntry(EntryState.AwaitSecondBoardShop);
            return;
        }

        if (TryMoveToDestination(destination,
                CurrentRouteNode?.Name ?? "second-board shop",
                "second-board-shop-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            boardMovementReady = true;
            AdvanceEntry(EntryState.AwaitSecondBoardShop);
        }
    }

    private void ProcessSecondBoardShopAvailability()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Shop))
        {
            return;
        }

        if (!RequireExpectedRouteAddon("XBMContentsItemShop"))
        {
            return;
        }

        if (GetReadyAddon("XBMContentsItemShop") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.CloseSecondBoardShop);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void ProcessCloseSecondBoardShop()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Shop))
        {
            return;
        }

        if (!RequireExpectedRouteAddon("XBMContentsItemShop"))
        {
            return;
        }

        var addon = GetReadyAddon("XBMContentsItemShop");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMContentsItemShop", "close-item-shop", 0))
        {
            return;
        }
        vnavMoveStarted = false;
        AdvanceEntry(EntryState.AwaitSecondBoardExitConfirmation);
        Log.Information("[Entry] Second Board item shop closed; waiting for exit confirmation.");
    }

    private void ProcessSecondBoardExitConfirmationAvailability()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Shop))
        {
            return;
        }

        if (GetReadyAddon("SelectYesno") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.ConfirmSecondBoardExit);
    }

    private void ProcessSecondBoardExitConfirmation()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Shop))
        {
            return;
        }

        var addon = GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            return;
        }

        var currentNode = CurrentRouteNode;
        var nextNode = currentNode == null ? null : ActiveRouteNodes.GetValueOrDefault(currentNode.NextPhase);
        if (currentNode?.Kind != RouteNodeKind.Shop || nextNode == null)
        {
            FailEntry($"shop-next-node-invalid:{currentNode?.Key ?? "unknown"}");
            return;
        }

        if (!TryGetSelectYesnoPrompt(addon, out var prompt))
        {
            return;
        }

        if (prompt.Contains("Proceed to challenge this board?", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[Entry] rejected shop exit while challenge prompt is open.");
            return;
        }

        if (!TryFireCallback(addon, "SelectYesno", "confirm-shop-exit", 0))
        {
            return;
        }
        if (!SetNextRouteNode($"{currentNode.Name} confirmed"))
        {
            return;
        }
        boardMovementTransitionObserved = false;
        boardMovementClearSince = null;
        boardMovementReady = false;
        vnavMoveStarted = false;
        movementConditionsClearSince = null;

        if (nextNode.DirectResultFlow)
        {
            AdvanceEntry(EntryState.MoveToStage10Start);
            Log.Information($"[Entry] {currentNode.Name} confirmed; moving to {nextNode.Name} start.");
            return;
        }

        switch (nextNode.Kind)
        {
            case RouteNodeKind.Battle:
                if (TryBeginRecovery(EntryState.MoveToStageStart, 60, 90))
                {
                    Log.Information($"[Entry] {currentNode.Name} confirmed; checking recovery items before {nextNode.Name}.");
                    return;
                }

                AdvanceEntry(EntryState.MoveToStageStart);
                break;
            case RouteNodeKind.Rest:
                AdvanceEntry(EntryState.MoveToStage4BRest);
                break;
            case RouteNodeKind.Treasure:
                AdvanceEntry(EntryState.MoveToStage6Start);
                break;
            case RouteNodeKind.Random:
                AdvanceEntry(EntryState.MoveToRandomOutcome);
                break;
            default:
                FailEntry($"shop-next-node-invalid:{nextNode.Key}:{nextNode.Kind}");
                return;
        }

        Log.Information($"[Entry] {currentNode.Name} confirmed; moving to {nextNode.Name}.");
    }

    private void ProcessSecondBoardMainHudAvailability()
    {
        if (GetReadyAddon("XBMContentsMainHUD") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.ExitSecondBoardMainHud);
    }

    private void ProcessSecondBoardMainHudExit()
    {
        var addon = GetReadyAddon("XBMContentsMainHUD");
        if (addon == null)
        {
            return;
        }

        ResetCombatProviders();
        if (!TryFireCallback(addon, "XBMContentsMainHUD", "close-second-board-hud", 3))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitSecondBoardFinalConfirmation);
        Log.Information("[Entry] requested Second Board contents exit; waiting for final confirmation.");
    }

    private void ProcessSecondBoardFinalConfirmationAvailability()
    {
        if (GetReadyAddon("SelectYesno") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.ConfirmSecondBoardFinalExit);
    }

    private void ProcessSecondBoardFinalConfirmation()
    {
        var addon = GetReadyAddon("SelectYesno");
        if (addon == null)
        {
            return;
        }

        if (!TryGetSelectYesnoPrompt(addon, out var prompt))
        {
            return;
        }

        if (prompt.Contains("Proceed to challenge this board?", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("[Entry] rejected final exit while challenge prompt is open.");
            return;
        }

        if (!TryFireCallback(addon, "SelectYesno", "confirm-final-exit", 0))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitXbmResult);
        Log.Information("[Entry] confirmed final Second Board exit prompt; waiting for XBMResult.");
    }

    private void ProcessInstanceLeave()
    {
        try
        {
            if (!EventFramework.CanLeaveCurrentContent())
            {
                return;
            }

            EventFramework.LeaveCurrentContent(true);
            AdvanceEntry(EntryState.AwaitXbmResult);
            Log.Information("[Entry] board instance leave dispatched; waiting for XBMResult.");
        }
        catch (Exception ex)
        {
            FailEntry($"instance-leave-failed:{ex.Message}");
        }
    }

    private void ProcessXbmResultAvailability()
    {
        if (GetReadyAddon("XBMResult") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.ExitXbmResult);
    }

    private void ProcessXbmResultInitialCallback()
    {
        var addon = GetReadyAddon("XBMResult");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMResult", "open-result", 0))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitNeedGreed);
        Log.Information("[Entry] XBMResult callback 0 dispatched; waiting for NeedGreed.");
    }

    private void ProcessNeedGreedAvailability()
    {
        if (GetReadyAddon("NeedGreed") != null)
        {
            AdvanceEntry(EntryState.RollNeedGreed);
            return;
        }

        if (DateTime.UtcNow - entryStateStarted <
            TimeSpan.FromMilliseconds(NeedGreedAppearanceGracePeriodMilliseconds))
        {
            return;
        }

        if (GetReadyAddon("XBMResult") == null)
        {
            return;
        }

        Log.Information("[Entry] NeedGreed did not appear; closing XBMResult without a loot roll.");
        AdvanceEntry(EntryState.ConfirmXbmResultExit);
    }

    private void ProcessNeedGreedRoll()
    {
        var addon = GetReadyAddon("NeedGreed");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "NeedGreed", "roll-need-greed", -2))
        {
            return;
        }
        AdvanceEntry(EntryState.ConfirmXbmResultExit);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
        Log.Information("[Entry] NeedGreed roll dispatched callback=-2; waiting to close XBMResult.");
    }

    private void ProcessXbmResultExitCallback()
    {
        var addon = GetReadyAddon("XBMResult");
        if (addon == null)
        {
            return;
        }

        if (!TryFireCallback(addon, "XBMResult", "close-result", -1))
        {
            return;
        }
        AdvanceEntry(EntryState.AwaitCentralShroud);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
        Log.Information("[Entry] XBMResult exit callbacks dispatched; waiting for Central Shroud.");
    }

    private void ProcessCentralShroudReturn()
    {
        if (ClientTerritoryId == 148)
        {
            if (loopEntry && !stopAfterCurrentLoop)
            {
                Log.Information($"[Entry] board={selectedBoard} instance fully exited; restarting automation loop.");
                EndEntry(EntryState.Idle, "loop restart");
                StartBoardEntry(selectedBoard, true, selectedBoard3Route);
            }
            else
            {
                var reason = stopAfterCurrentLoop
                    ? "pending stop reached Central Shroud"
                    : "board instance fully exited";
                EndEntry(EntryState.Complete, reason);
                Log.Information("[Entry] board instance fully exited territory=148.");
            }
        }
    }

    internal void ClearBattleNpcActionTracker()
    {
        battleNpcActionTracker.Clear();
        BattleNpcActionTrackerCapturedAt = null;
    }

    internal void CaptureBattleNpcActions()
    {
        var now = DateTime.UtcNow;
        battleNpcActionTracker.Clear();
        foreach (var gameObject in ObjectTable)
        {
            if (gameObject.ObjectKind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc ||
                !gameObject.IsValid() || gameObject is not ICharacter character || IsBattleNpcPet(gameObject) ||
                character.Address == IntPtr.Zero)
            {
                continue;
            }

            var nativeCharacter = (Character*)character.Address;
            var castInfo = nativeCharacter->GetCastInfo();
            if (castInfo == null || !castInfo->IsCasting)
            {
                continue;
            }

            var entry = new BattleNpcActionTrackerEntry
            {
                ObjectId = gameObject.GameObjectId,
                BaseId = gameObject.BaseId,
                Name = gameObject.Name.ToString(),
                ActionType = castInfo->ActionType,
                ActionId = castInfo->ActionId,
                ActionName = GetActionName(castInfo->ActionId),
                TargetId = castInfo->TargetId.ToString() ?? string.Empty,
                CurrentCastTime = castInfo->CurrentCastTime,
                TotalCastTime = castInfo->TotalCastTime,
                Interruptible = castInfo->Interruptible,
            };
            battleNpcActionTracker.Add(entry.ObjectId, entry);
            Log.Information($"[ActionTracker] event=capture name=\"{EscapeLog(entry.Name)}\" " +
                $"baseId={entry.BaseId} objectId={entry.ObjectId:X} actionType={entry.ActionType} actionId={entry.ActionId} " +
                $"actionName=\"{EscapeLog(entry.ActionName)}\" " +
                $"targetId={entry.TargetId} currentCastTime={entry.CurrentCastTime:0.000} " +
                $"totalCastTime={entry.TotalCastTime:0.000} interruptible={entry.Interruptible}.");
        }

        BattleNpcActionTrackerCapturedAt = now;
        Log.Information($"[ActionTracker] event=capture-complete count={battleNpcActionTracker.Count} capturedAt={now:O}.");
    }

    private string GetActionName(uint actionId)
    {
        var sheet = DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
        if (sheet.TryGetRow(actionId, out var action))
        {
            var name = action.Name.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return $"Unknown action {actionId}";
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetCombatTarget()
    {
        var preferredTarget = GetPreferredCombatTarget();
        if (preferredTarget != null)
        {
            return preferredTarget;
        }

        return HasLivingCombatTarget() && IsRouteDrivenBoard
            ? null
            : HasLivingCombatTarget()
                ? GetHostileCombatFallbackTarget()
                : null;
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetPreferredCombatTarget()
        => ObjectTable
            .Where(gameObject => gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                gameObject is IBattleNpc &&
                gameObject.IsValid() &&
                gameObject.IsTargetable &&
                gameObject is ICharacter character && character.CurrentHp > 0 &&
                !IsBattleNpcPet(gameObject) &&
                IsCombatTargetBaseId(gameObject.BaseId))
            .OrderBy(gameObject => GetCombatTargetPriority(gameObject.BaseId))
            .ThenBy(gameObject => gameObject.GameObjectId)
            .FirstOrDefault();

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetCombatTargetByBaseId(uint baseId)
        => baseId == 0
            ? null
            : ObjectTable.FirstOrDefault(gameObject =>
                gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                gameObject is IBattleNpc && gameObject.IsValid() && gameObject.IsTargetable &&
                gameObject.BaseId == baseId && gameObject is ICharacter character && character.CurrentHp > 0 &&
                !IsBattleNpcPet(gameObject));

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetHostileCombatFallbackTarget()
    {
        var playerPosition = ObjectTable.LocalPlayer?.Position;
        return ObjectTable
            .Where(gameObject => gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                gameObject is IBattleNpc &&
                gameObject.IsValid() &&
                gameObject.IsTargetable &&
                gameObject is ICharacter character && character.CurrentHp > 0 &&
                !IsBattleNpcPet(gameObject) &&
                IsHostile(character))
            .OrderBy(gameObject => playerPosition.HasValue
                ? Vector3.Distance(playerPosition.Value, gameObject.Position)
                : float.MaxValue)
            .FirstOrDefault();
    }

    private static unsafe bool IsHostile(ICharacter character)
    {
        if (character.Address == IntPtr.Zero)
        {
            return false;
        }

        var nativeCharacter = (Character*)character.Address;
        return nativeCharacter != null && nativeCharacter->IsHostile;
    }

    private string GetCombatEntryTarget()
    {
        var target = GetCombatTarget();
        if (target != null)
        {
            return $"{target.Name} (BaseId {target.BaseId}, ObjectId {target.GameObjectId:X})";
        }

        return CurrentRouteNode == null
            ? "Unknown route combat target"
            : $"{CurrentRouteNode.Name} combat target";
    }

    private bool HasLivingCombatTarget()
        => ObjectTable.Any(gameObject =>
            gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
            gameObject is IBattleNpc &&
            gameObject.IsValid() &&
            gameObject is ICharacter character && character.CurrentHp > 0 &&
            !IsBattleNpcPet(gameObject) &&
            IsCombatTargetBaseId(gameObject.BaseId));

    private bool HasLivingStage4APriorityPiece()
        => ObjectTable.Any(gameObject =>
            gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
            gameObject.IsValid() &&
            gameObject is ICharacter character && character.CurrentHp > 0 &&
            !IsBattleNpcPet(gameObject) &&
            gameObject.BaseId == 19608);

    private bool IsCombatTargetBaseId(uint baseId)
        => IsCurrentBattleRouteNode() &&
            CurrentRouteNode?.CombatTargetBaseIds.Contains(baseId) == true;

    private bool IsBattleNpcPet(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
        => gameObject is IBattleNpc battleNpc &&
            string.Equals(battleNpc.BattleNpcKind.ToString(), "Pet", StringComparison.OrdinalIgnoreCase);

    private int GetCombatTargetPriority(uint baseId)
    {
        var node = CurrentRouteNode;
        if (node == null)
        {
            return int.MaxValue;
        }

        if (node.PriorityTargetBaseId == baseId)
        {
            return 0;
        }

        var targetOrder = Array.IndexOf(node.CombatTargetBaseIds, baseId);
        return targetOrder < 0 ? int.MaxValue : targetOrder + 1;
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetKnightTarget()
        => ObjectTable.FirstOrDefault(gameObject => gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
            gameObject.IsTargetable && gameObject is ICharacter character && character.CurrentHp > 0 && gameObject.BaseId == 19334);

    private bool HasOwnedPet()
    {
        var localPlayerId = ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        return ObjectTable.Any(gameObject =>
            gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
            gameObject.OwnerId == localPlayerId &&
            gameObject is IBattleNpc { BattleNpcKind: Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Pet } &&
            gameObject is ICharacter character && character.CurrentHp > 0);
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetOwnedFamiliar()
    {
        var localPlayer = ObjectTable.LocalPlayer;
        if (localPlayer == null)
        {
            return null;
        }

        return ObjectTable
            .Where(gameObject =>
                gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                gameObject.OwnerId == localPlayer.GameObjectId && gameObject.IsValid() &&
                gameObject is IBattleNpc { BattleNpcKind: Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Pet } &&
                gameObject is ICharacter character && character.CurrentHp > 0)
            .OrderBy(gameObject => Vector3.Distance(localPlayer.Position, gameObject.Position))
            .FirstOrDefault();
    }

    private void SetCombatTarget(Dalamud.Game.ClientState.Objects.Types.IGameObject target)
    {
        if (TargetManager.Target?.GameObjectId != target.GameObjectId)
        {
            TargetManager.Target = target;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(150);
            var selection = IsCombatTargetBaseId(target.BaseId) ? "preferred" : "hostile-fallback";
            Log.Information($"[Combat] target changed selection={selection} name=\"{EscapeLog(target.Name.ToString())}\" " +
                $"baseId={target.BaseId} objectId={target.GameObjectId:X}.");
        }
    }

    private bool TryUseCombatAction(uint actionId, Dalamud.Game.ClientState.Objects.Types.IGameObject target)
    {
        if (TargetManager.Target?.GameObjectId != target.GameObjectId || !CanUseAction(actionId, target.GameObjectId))
        {
            return false;
        }

        return UseAction(actionId, target.GameObjectId);
    }

    private static bool CanUseAction(uint actionId, ulong targetObjectId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager != null &&
            actionManager->GetActionStatus(ActionType.Action, actionId, targetObjectId, true, true) == 0;
    }

    private static bool UseAction(uint actionId, ulong targetObjectId)
    {
        var actionManager = ActionManager.Instance();
        return actionManager != null && actionManager->UseAction(ActionType.Action, actionId, targetObjectId);
    }

    internal void UseDebugAction()
    {
        if (DebugActionId <= 0)
        {
            Log.Warning("[Debug] action-test rejected: action ID must be greater than zero.");
            return;
        }

        var actionId = (uint)DebugActionId;
        var player = ObjectTable.LocalPlayer;
        if (player == null)
        {
            Log.Warning($"[Debug] op=action-test actionId={actionId} rejected: local player unavailable.");
            return;
        }

        var targetObjectId = player.GameObjectId;
        var hp = player is ICharacter character ? $"{character.CurrentHp}/{character.MaxHp}" : "unknown";
        var actionType = DebugActionTypeIndex switch
        {
            1 => ActionType.Item,
            2 => ActionType.EventItem,
            _ => ActionType.Action,
        };
        var actionManager = ActionManager.Instance();
        if (actionManager == null)
        {
            Log.Warning($"[Debug] op=action-test actionId={actionId} rejected: ActionManager unavailable.");
            return;
        }

        var status = actionManager->GetActionStatus(actionType, actionId, targetObjectId, true, true);
        var dispatched = status == 0 && actionManager->UseAction(actionType, actionId, targetObjectId);
        Log.Information($"[Debug] op=action-test actionType={actionType} actionId={actionId} target=self " +
            $"targetObjectId={targetObjectId:X} hp={hp} status={status} dispatched={dispatched}.");
    }

    private bool HasEntryTransitionCondition()
        => Condition[ConditionFlag.SufferingStatusAffliction2]
            || Condition[ConditionFlag.SufferingStatusAffliction63]
            || Condition[ConditionFlag.OccupiedInQuestEvent]
            || Condition[ConditionFlag.Occupied33]
            || Condition[ConditionFlag.OccupiedInCutSceneEvent]
            || Condition[ConditionFlag.BetweenAreas]
            || Condition[ConditionFlag.BetweenAreas51]
            || Condition[ConditionFlag.WaitingForDuty]
            || Condition[ConditionFlag.WaitingForDutyFinder];

    private void FailEntry(string reason)
    {
        EndEntry(EntryState.Failed, reason);
        Log.Warning($"[Entry] failed reason={EscapeLog(reason)}.");
    }

    private void TryStopVnav()
    {
        try
        {
            vnavStop.InvokeAction();
        }
        catch
        {
            // VNav may not be installed; stopping entry must still be safe.
        }
    }

    private void ResetMovement()
        => movement.Clear();

    private void StopMovement()
    {
        if (movement.VnavStarted)
        {
            TryStopVnav();
        }

        movement.VnavStarted = false;
    }

    private void ClearActionTriggerRuntime()
    {
        if (actionTriggerMovement != null)
        {
            StopMovement();
        }

        actionTriggerMovement = null;
        handledActionTriggers.Clear();
    }

    private MovementResult TryMoveToDestination(Vector3 destination, string label, string failureReason,
        float arrivalRadius)
    {
        var distance = Vector3.Distance(PlayerPosition, destination);
        if (distance <= arrivalRadius)
        {
            StopMovement();
            Log.Information($"[Entry] {label} destination reached destination={FormatVector(destination)} " +
                $"position={FormatVector(PlayerPosition)} distance={distance:F3} radius={arrivalRadius:F1}.");
            return MovementResult.Arrived;
        }

        if (movement.VnavStarted)
        {
            return MovementResult.Moving;
        }

        try
        {
            if (!vnavIsReady.InvokeFunc())
            {
                return MovementResult.Waiting;
            }

            if (!vnavPathfindAndMoveTo.InvokeFunc(destination, false))
            {
                FailEntry("vnav-move-rejected");
                return MovementResult.Waiting;
            }

            movement.VnavStarted = true;
            Log.Information($"[Entry] vnav {label} move started destination={FormatVector(destination)}.");
            return MovementResult.Moving;
        }
        catch (Exception ex)
        {
            FailEntry($"{failureReason}:{ex.Message}");
            return MovementResult.Waiting;
        }
    }

    private AtkUnitBase* GetReadyAddon(string name)
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName(name);
        return addon != null && addon->IsReady && addon->IsVisible ? addon : null;
    }

    private bool TryFireCallback(AtkUnitBase* addon, string expectedAddon, string operation, params object[] values)
    {
        if (addon == null || !addon->IsReady || !addon->IsVisible ||
            !string.Equals(addon->NameString, expectedAddon, StringComparison.Ordinal))
        {
            Log.Warning($"[Callback] rejected operation={operation} expectedAddon={expectedAddon} " +
                $"actualAddon={(addon == null ? "null" : addon->NameString)} state={entryState}.");
            return false;
        }

        if (!ValidateCallbackSchema(addon, expectedAddon, operation))
        {
            return false;
        }

        try
        {
            Callback.Fire(addon, true, values);
            Log.Debug($"[Callback] dispatched operation={operation} addon={expectedAddon} " +
                $"args={string.Join(",", values.Select(FormatCallbackValue))} state={entryState}.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[Callback] failed operation={operation} addon={expectedAddon} state={entryState}.");
            FailEntry($"callback-failed:{operation}");
            return false;
        }
    }

    private bool ValidateCallbackSchema(AtkUnitBase* addon, string expectedAddon, string operation)
    {
        var minimumValues = expectedAddon switch
        {
            "SelectYesno" => 1,
            "ContextMenu" => 9,
            "XBMContentsMainHUD" => 16,
            "XBMPetParty" => 7,
            "XBMMonsterNotebook" => 30,
            _ => 0,
        };
        if (minimumValues > 0 && (addon->AtkValues == null || addon->AtkValuesCount < minimumValues))
        {
            Log.Warning($"[Callback] rejected operation={operation} addon={expectedAddon} " +
                $"atkValuesCount={addon->AtkValuesCount} minimum={minimumValues} state={entryState}.");
            return false;
        }

        var schemaValid = expectedAddon switch
        {
            "SelectYesno" => IsAtkStringType(addon->AtkValues[0].Type),
            "ContextMenu" => addon->AtkValues[0].Type == AtkValueType.UInt,
            "XBMPetParty" => addon->AtkValues[5].Type == AtkValueType.Int,
            _ => true,
        };
        if (!schemaValid)
        {
            Log.Warning($"[Callback] rejected operation={operation} addon={expectedAddon} " +
                $"schema-invalid atkValuesCount={addon->AtkValuesCount} state={entryState}.");
        }

        return schemaValid;
    }

    private static bool IsAtkStringType(AtkValueType type)
        => type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.WideString or AtkValueType.ManagedString;

    private static string FormatCallbackValue(object value)
        => value is AtkValue atkValue ? atkValue.Type.ToString() : value?.ToString() ?? "null";

    private static bool TryReadAtkStringValue(AtkUnitBase* addon, int index, out string value)
    {
        value = string.Empty;
        if (!IsAtkIndexValid(addon, index))
        {
            return false;
        }

        var type = addon->AtkValues[index].Type;
        if (!IsAtkStringType(type))
        {
            return false;
        }

        value = addon->AtkValues[index].GetValueAsString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private bool TryGetSelectYesnoPrompt(AtkUnitBase* addon, out string prompt)
    {
        if (!TryReadAtkStringValue(addon, 0, out prompt))
        {
            Log.Warning($"[UI] SelectYesno schema rejected state={entryState} " +
                $"atkValuesCount={(addon == null ? 0 : addon->AtkValuesCount)}.");
            return false;
        }

        Log.Debug($"[UI] SelectYesno prompt=\"{EscapeLog(prompt)}\" state={entryState}.");
        return true;
    }

    private bool TryGetContextMenuUseIndex(AtkUnitBase* addon, out int useIndex)
    {
        useIndex = -1;
        if (addon == null || addon->AtkValues == null || addon->AtkValuesCount < 9 ||
            addon->AtkValues[0].Type != AtkValueType.UInt)
        {
            Log.Warning($"[UI] ContextMenu schema rejected state={entryState} " +
                $"atkValuesCount={(addon == null ? 0 : addon->AtkValuesCount)}.");
            return false;
        }

        var entriesCount = Math.Min((int)addon->AtkValues[0].UInt, addon->AtkValuesCount - 8);
        for (var index = 0; index < entriesCount; index++)
        {
            var value = addon->AtkValues[8 + index];
            if (!IsAtkStringType(value.Type))
            {
                continue;
            }

            var text = value.GetValueAsString().Trim();
            if (string.Equals(text, "Use", StringComparison.OrdinalIgnoreCase))
            {
                useIndex = index;
                Log.Debug($"[UI] ContextMenu Use entry index={useIndex} state={entryState}.");
                return true;
            }
        }

        Log.Warning($"[UI] ContextMenu has no English Use entry entries={entriesCount} state={entryState}.");
        return false;
    }

    private static bool IsExpectedRouteAddon(string addonName, RouteNode? node)
        => node == null || string.IsNullOrWhiteSpace(node.ExpectedAddon) ||
            string.Equals(node.ExpectedAddon, addonName, StringComparison.OrdinalIgnoreCase);

    private bool RequireExpectedRouteAddon(string addonName)
    {
        if (IsExpectedRouteAddon(addonName, CurrentRouteNode))
        {
            return true;
        }

        FailEntry($"route-addon-mismatch:expected={CurrentRouteNode?.ExpectedAddon}:actual={addonName}");
        return false;
    }

    private bool SetNextRouteNode(string reason)
    {
        var current = CurrentRouteNode;
        if (current == null || current.NextPhase == RoutePhase.None)
        {
            FailEntry($"route-next-node-missing:current={current?.Key ?? "unknown"}");
            return false;
        }

        return SetRoute(current.NextPhase, reason);
    }

    private bool IsCurrentBattleRouteNode()
        => CurrentRouteNode?.Kind == RouteNodeKind.Battle ||
            (CurrentRouteNode?.Kind == RouteNodeKind.Random && randomOutcome == RandomOutcomeKind.Battle);

    private bool IsCurrentTreasureRouteNode()
        => CurrentRouteNode?.Kind == RouteNodeKind.Treasure ||
            (CurrentRouteNode?.Kind == RouteNodeKind.Random && randomOutcome == RandomOutcomeKind.Treasure);

    private bool RequireRouteNodeKind(RouteNodeKind expectedKind)
    {
        var node = CurrentRouteNode;
        if (node?.Kind == expectedKind ||
            (node?.Kind == RouteNodeKind.Random && expectedKind == RouteNodeKind.Battle && randomOutcome == RandomOutcomeKind.Battle) ||
            (node?.Kind == RouteNodeKind.Random && expectedKind == RouteNodeKind.Treasure && randomOutcome == RandomOutcomeKind.Treasure))
        {
            return true;
        }

        FailEntry($"route-node-kind-mismatch:expected={expectedKind}:actual={node?.Key ?? "unknown"}/" +
            $"{node?.Kind.ToString() ?? "none"}");
        return false;
    }

    private bool SetRoute(RoutePhase next, string reason)
    {
        if (routePhase == next)
        {
            return true;
        }

        if (!ActiveRouteNodes.TryGetValue(next, out var nextNode))
        {
            FailEntry($"route-node-unknown:{next}");
            return false;
        }

        var current = CurrentRouteNode;
        if (current != null && current.NextPhase != next)
        {
            FailEntry($"route-transition-invalid:{current.Key}->{nextNode.Key}");
            return false;
        }

        Log.Information($"[Route] transition from={current?.Key ?? "None"}/{current?.Kind.ToString() ?? "None"} " +
            $"to={nextNode.Key}/{nextNode.Kind} reason=\"{EscapeLog(reason)}\".");
        ClearActionTriggerRuntime();
        routePhase = next;
        randomOutcome = RandomOutcomeKind.None;
        usedAttackItemIds.Clear();
        failedItemUseIds.Clear();
        sandstillArmed = false;
        sandstillRestartHandled = false;
        recoveryNoUsableItemsRoute = RoutePhase.None;
        recoveryNoUsableItemsThreshold = 0;
        return true;
    }

    private void AdvanceEntry(EntryState nextState, int delayMilliseconds = 500)
    {
        entryState = nextState;
        entryStateStarted = DateTime.UtcNow;
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(delayMilliseconds);
        Log.Information($"[Entry] state={nextState} target={EntryTarget}.");
    }

    private void OnCommand(string command, string args)
        => OpenMainWindow();

    private void OpenMainWindow()
        => mainWindow.Toggle();

    internal void OpenDebugTools()
        => debugWindow.Toggle();

    internal void LogOverview()
    {
        Log.Information($"[Debug] op=overview territory={ClientTerritoryId} loggedIn={IsTerritoryLoaded} " +
            $"playerLoaded={IsPlayerLoaded} objectCount={ObjectCount} target=\"{EscapeLog(TargetName)}\"");
    }

    internal void LogTerritoryData()
    {
        Log.Information($"[Debug] op=territory territory={ClientTerritoryId} loggedIn={IsTerritoryLoaded}");
    }

    internal void LogPlayerData()
    {
        var position = PlayerPosition;
        Log.Information($"[Debug] op=player loaded={IsPlayerLoaded} job={PlayerJobId} " +
            $"position=<{position.X:0.000},{position.Y:0.000},{position.Z:0.000}> target=\"{EscapeLog(TargetName)}\"");
    }

    internal void LogObjectSummary()
    {
        Log.Information($"[Debug] op=object-summary count={ObjectCount} territory={ClientTerritoryId}");
    }

    internal void LogNearbyBattleNpcs()
    {
        var npcs = ObjectTable
            .Where(gameObject => gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc)
            .Take(50)
            .ToArray();

        Log.Information($"[Debug] op=battle-npc-summary count={npcs.Length} objectTableCount={ObjectCount}");
        foreach (var npc in npcs)
        {
            var battleNpc = npc as IBattleNpc;
            Log.Information($"[Debug] op=battle-npc name=\"{EscapeLog(npc.Name.ToString())}\" " +
                $"objectId={npc.GameObjectId:X} baseId={npc.BaseId} position=<{npc.Position.X:0.000},{npc.Position.Y:0.000},{npc.Position.Z:0.000}> " +
                $"targetable={npc.IsTargetable} battleNpcKind={battleNpc?.BattleNpcKind} ownerId={npc.OwnerId:X}");
        }
    }

    internal void LogNonBattleNpcs()
    {
        var npcs = ObjectTable
            .Where(gameObject => gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)
            .Take(100)
            .ToArray();

        Log.Information($"[Debug] op=event-npc-summary count={npcs.Length} objectTableCount={ObjectCount}");
        foreach (var npc in npcs)
        {
            Log.Information($"[Debug] op=event-npc name=\"{EscapeLog(npc.Name.ToString())}\" " +
                $"objectId={npc.GameObjectId:X} baseId={npc.BaseId} position=<{npc.Position.X:0.000},{npc.Position.Y:0.000},{npc.Position.Z:0.000}> " +
                $"targetable={npc.IsTargetable}");
        }
    }

    internal void LogTargetedNpc()
    {
        var target = TargetManager.Target;
        if (target == null)
        {
            Log.Warning("[Debug] op=targeted-npc available=false reason=no-target");
            return;
        }

        Log.Information($"[Debug] op=targeted-npc available=true type={target.GetType().FullName} " +
            $"objectKind={target.ObjectKind} isEventNpc={target.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc}");
        Log.Information($"[Debug] op=targeted-npc-core name=\"{EscapeLog(target.Name.ToString())}\" " +
            $"objectId={target.GameObjectId:X} baseId={target.BaseId} valid={target.IsValid()} targetable={target.IsTargetable} " +
            $"position=<{target.Position.X:0.000},{target.Position.Y:0.000},{target.Position.Z:0.000}> " +
            $"rotation={target.Rotation:0.000} hitboxRadius={target.HitboxRadius:0.000}");

        foreach (var property in target.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 || property.Name is "Address" or "VTable")
            {
                continue;
            }

            try
            {
                var value = property.GetValue(target)?.ToString() ?? "null";
                Log.Information($"[Debug] op=targeted-npc-property name={property.Name} value=\"{EscapeLog(value)}\"");
            }
            catch (Exception ex)
            {
                Log.Warning($"[Debug] op=targeted-npc-property name={property.Name} error=\"{EscapeLog(ex.Message)}\"");
            }
        }
    }

    internal void LogPetInfo()
    {
        var localPlayerId = ObjectTable.LocalPlayer?.GameObjectId ?? 0;
        var pets = ObjectTable
            .Where(gameObject => gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                gameObject.OwnerId == localPlayerId)
            .ToArray();

        var companions = ObjectTable
            .Where(gameObject => gameObject.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion)
            .ToArray();

        Log.Information($"[Debug] op=pet-info playerObjectId={localPlayerId:X} ownedBattleNpcs={pets.Length} companions={companions.Length}");
        foreach (var pet in pets)
        {
            var battleNpcKind = pet is Dalamud.Game.ClientState.Objects.Types.IBattleNpc battleNpc
                ? battleNpc.BattleNpcKind.ToString()
                : "Unknown";
            var hp = pet is Dalamud.Game.ClientState.Objects.Types.ICharacter character
                ? $"{character.CurrentHp}/{character.MaxHp}"
                : "unknown";
            Log.Information($"[Debug] op=pet-info-owned name=\"{EscapeLog(pet.Name.ToString())}\" " +
                $"objectId={pet.GameObjectId:X} baseId={pet.BaseId} ownerId={pet.OwnerId:X} " +
                $"battleNpcKind={battleNpcKind} hp={hp} targetable={pet.IsTargetable} " +
                $"position=<{pet.Position.X:0.000},{pet.Position.Y:0.000},{pet.Position.Z:0.000}>");
        }

        foreach (var companion in companions)
        {
            Log.Information($"[Debug] op=pet-info-companion name=\"{EscapeLog(companion.Name.ToString())}\" " +
                $"objectId={companion.GameObjectId:X} baseId={companion.BaseId} ownerId={companion.OwnerId:X} " +
                $"targetable={companion.IsTargetable} position=<{companion.Position.X:0.000},{companion.Position.Y:0.000},{companion.Position.Z:0.000}>");
        }
    }

    internal void LogServiceAvailability()
    {
        Log.Information($"[Debug] op=services commandManager=true clientState=true playerState=true " +
            $"objectTable=true targetManager=true objectCount={ObjectCount}");
    }

    internal void LogLoadedAddons(bool visibleOnly)
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            Log.Warning("[Debug] op=addon-dump available=false reason=unit-manager-unavailable");
            return;
        }

        var list = &manager->AllLoadedUnitsList;
        var safeLength = Math.Min(list->Count, list->Entries.Length);
        var dumped = 0;
        for (var index = 0; index < safeLength; index++)
        {
            var addon = list->Entries[index].Value;
            if (addon == null || (visibleOnly && !addon->IsVisible))
            {
                continue;
            }

            DumpAddon(addon, "all-loaded", index);
            dumped++;
        }

        Log.Information($"[Debug] op=addon-dump-result source=all-loaded visibleOnly={visibleOnly} " +
            $"listCount={list->Count} dumped={dumped}");
    }

    internal void LogFocusedAddons()
    {
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null)
        {
            Log.Warning("[Debug] op=addon-dump available=false reason=unit-manager-unavailable");
            return;
        }

        var list = &manager->FocusedUnitsList;
        var safeLength = Math.Min(list->Count, list->Entries.Length);
        var dumped = 0;
        for (var index = 0; index < safeLength; index++)
        {
            var addon = list->Entries[index].Value;
            if (addon == null)
            {
                continue;
            }

            DumpAddon(addon, "focused", index);
            dumped++;
        }

        Log.Information($"[Debug] op=addon-dump-result source=focused listCount={list->Count} dumped={dumped}");
    }

    internal void LogLoadedLgbCollisionBoxes(bool nearbyOnly, float radius)
    {
        var layoutWorld = LayoutWorld.Instance();
        if (layoutWorld == null)
        {
            Log.Warning("[Debug] op=lgb-collision-boxes available=false reason=layout-world-unavailable");
            return;
        }

        var activeLayout = layoutWorld->ActiveLayout;
        if (activeLayout == null)
        {
            Log.Warning("[Debug] op=lgb-collision-boxes available=false reason=active-layout-unavailable");
            return;
        }

        var globalLayout = layoutWorld->GlobalLayout;
        var player = ObjectTable.LocalPlayer;
        var playerPosition = player?.Position ?? Vector3.Zero;
        var scanRadius = Math.Max(0f, radius);
        var count = 0;
        var total = 0;
        Log.Information($"[Debug] op=lgb-collision-boxes territory={ClientTerritoryId} nearbyOnly={nearbyOnly} " +
            $"radius={scanRadius:0.000} playerPosition={FormatVector(playerPosition)} scan-start");

        void ScanLayout(string source, LayoutManager* layout)
        {
            if (layout == null)
            {
                return;
            }

            if (!layout->InstancesByType.TryGetValuePointer(InstanceType.CollisionBox, out var collisionMapPointer)
                || collisionMapPointer == null || collisionMapPointer->Value == null)
            {
                Log.Information($"[Debug] op=lgb-collision-boxes-layout source={source} " +
                    $"territory={layout->TerritoryTypeId} collisionMap=false");
                return;
            }

            var collisionMap = collisionMapPointer->Value;
            Log.Information($"[Debug] op=lgb-collision-boxes-layout source={source} " +
                $"territory={layout->TerritoryTypeId} collisionMap=true count={collisionMap->Count}");

            foreach (var pair in *collisionMap)
            {
                var instance = pair.Item2.Value;
                if (instance == null)
                {
                    continue;
                }

                total++;
                var collisionBox = (CollisionBoxLayoutInstance*)instance;
                var transform = *instance->GetTransformImpl();
                var position = transform.Translation;
                var distance = Vector3.Distance(playerPosition, position);
                if (nearbyOnly && (player == null || distance > scanRadius))
                {
                    continue;
                }

                count++;
                Log.Information($"[Debug] op=lgb-collision-box source={source} instanceKey={pair.Item1} " +
                    $"layerKey={instance->Id.LayerKey} position={FormatVector(position)} " +
                    $"rotation={FormatQuaternion(transform.Rotation)} scale={FormatVector(transform.Scale)} " +
                    $"pcbPathCrc=0x{collisionBox->PcbPathCrc:X8} " +
                    $"colliderType={collisionBox->TriggerBoxLayoutInstance.Type} " +
                    $"activeByDefault={collisionBox->TriggerBoxLayoutInstance.ActiveByDefault} " +
                    $"colliderActive={instance->IsColliderActive()} distance={distance:0.000}");
            }
        }

        ScanLayout("active", activeLayout);
        if (globalLayout != null && globalLayout != activeLayout)
        {
            ScanLayout("global", globalLayout);
        }

        Log.Information($"[Debug] op=lgb-collision-boxes-result territory={ClientTerritoryId} " +
            $"available=true total={total} logged={count} nearbyOnly={nearbyOnly}");
    }

    internal void LogPetPartyRoster()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName("XBMPetParty");
        if (addon == null || !addon->IsReady)
        {
            Log.Warning("[Debug] op=pet-party-roster available=false reason=XBMPetParty-not-ready");
            return;
        }

        const int recordStart = 6;
        const int recordStride = 77;
        const int maximumRecords = 15;
        var rosterCount = ReadAtkInt(addon, 5);
        var recordCount = Math.Clamp(rosterCount, 0, maximumRecords);
        Log.Information($"[Debug] op=pet-party-roster available=true addon=XBMPetParty " +
            $"atkValuesCount={addon->AtkValuesCount} rosterCount={rosterCount} recordStride={recordStride}");

        for (var index = 0; index < recordCount; index++)
        {
            var start = recordStart + (index * recordStride);
            if (start + 27 >= addon->AtkValuesCount || addon->AtkValues == null)
            {
                Log.Warning($"[Debug] op=pet-party-record index={index} available=false reason=record-out-of-range start={start}");
                break;
            }

            var currentHp = ReadAtkUInt(addon, start + 5);
            var maximumHp = ReadAtkUInt(addon, start + 6);
            var healthPercent = maximumHp == 0 ? 0 : (int)Math.Clamp((ulong)currentHp * 100 / maximumHp, 0, 100);
            var eligible = maximumHp > 0 && currentHp > 0 &&
                (ulong)currentHp * 100 >= (ulong)maximumHp * (uint)PetHealthThresholdPercent;
            Log.Information($"[Debug] op=pet-party-record index={index} start={start} " +
                $"rank=\"{EscapeLog(ReadAtkString(addon, start))}\" id={ReadAtkUInt(addon, start + 1)} " +
                $"selected={ReadAtkBool(addon, start + 2)} name=\"{EscapeLog(ReadAtkString(addon, start + 3))}\" " +
                $"hp={currentHp}/{maximumHp} hpPercent={healthPercent} eligible={eligible} " +
                $"threshold={PetHealthThresholdPercent} hpText=\"{EscapeLog(ReadAtkString(addon, start + 4))}\" " +
                $"strength=\"{EscapeLog(ReadAtkString(addon, start + 19))}\" " +
                $"physicalResistance=\"{EscapeLog(ReadAtkString(addon, start + 21))}\" " +
                $"constitution=\"{EscapeLog(ReadAtkString(addon, start + 23))}\" " +
                $"intelligence=\"{EscapeLog(ReadAtkString(addon, start + 25))}\" " +
                $"magicalResistance=\"{EscapeLog(ReadAtkString(addon, start + 27))}\"");

            Log.Information($"[Debug] op=pet-party-record-raw index={index} " +
                $"iconA={ReadAtkString(addon, start + 32)} iconB={ReadAtkUInt(addon, start + 33)} " +
                $"effectIds={string.Join(",", Enumerable.Range(35, 16).Select(offset => ReadAtkUInt(addon, start + offset)))}");
        }

        Log.Information($"[Debug] op=pet-party-footer rosterCount={ReadAtkUInt(addon, 1162)} " +
            $"rosterText=\"{EscapeLog(ReadAtkString(addon, 1181))}\" " +
            $"instruction=\"{EscapeLog(ReadAtkString(addon, 1185))}\"");
    }

    internal void LogPetPartyNode11()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName("XBMPetParty");
        if (addon == null || !addon->IsReady || !addon->IsVisible)
        {
            Log.Warning("[Debug] op=pet-party-node11 available=false reason=XBMPetParty-not-ready");
            return;
        }

        var node = GetPetPartyListNode(addon);
        if (node == null)
        {
            Log.Warning("[Debug] op=pet-party-node11 available=false reason=node-11-not-found");
            return;
        }

        Log.Information($"[Debug] op=pet-party-node11 available=true nodeId={node->NodeId} " +
            $"nodeType={node->GetNodeType()} drawFlags=0x{(uint)node->DrawFlags:X}");
        // The inspector identifies node 11 as an AtkComponentNode even though
        // its base NodeType is reported as Collision by the client.
        var linkedComponent = ((AtkComponentNode*)node)->Component;
        if (linkedComponent == null)
        {
            Log.Warning("[Debug] op=pet-party-node11 available=false reason=linked-component-null");
            return;
        }

        Log.Information($"[Debug] op=pet-party-node11-linked componentType={linkedComponent->GetComponentType()} " +
            $"component={(nint)linkedComponent:X} topLevelIndex=13 itemCount=" +
            $"{(linkedComponent->GetComponentType() == ComponentType.List ? ((AtkComponentList*)linkedComponent)->GetItemCount() : -1)}");

        if (linkedComponent->GetComponentType() != ComponentType.List)
        {
            return;
        }

        var list = (AtkComponentList*)linkedComponent;
        var itemCount = Math.Clamp(list->GetItemCount(), 0, 50);
        for (var index = 0; index < itemCount; index++)
        {
            var renderer = list->GetItemRenderer(index);
            var selectionNode = FindRendererNode(renderer, 2);
            var familiarId = ReadPetPartyFamiliarId(addon, index);
            var familiarNumber = familiarId is >= 242001 and <= 242050 ? (int)(familiarId - 242000) : -1;
            var drawFlags = selectionNode == null ? "missing" : $"0x{(uint)selectionNode->DrawFlags:X}";
            Log.Information($"[Debug] op=pet-party-selection row={index} listItemIndex=" +
                $"{(renderer == null ? -1 : renderer->ListItemIndex)} renderer={(renderer == null ? 0 : (nint)renderer):X} " +
                $"nodePath=1>11>{(renderer == null ? 0 : renderer->ListItemIndex)}>2 " +
                $"familiarNumber={familiarNumber} familiarId={familiarId} selectionNode={(selectionNode == null ? 0 : (nint)selectionNode):X} drawFlags={drawFlags} " +
                $"state={(selectionNode == null ? "missing" : (selectionNode->DrawFlags & 0x100) == 0 ? "selected" : "unselected")}.");
        }
    }

    private void DumpPetPartyComponentChildren(AtkUldManager* manager, string path, ref int rowIndex)
    {
        if (manager == null || manager->NodeList == null)
        {
            return;
        }

        var count = Math.Min(manager->NodeListCount, (ushort)1000);
        for (var index = 0; index < count; index++)
        {
            var child = manager->NodeList[index];
            if (child == null)
            {
                continue;
            }

            var childPath = $"{path}>{child->NodeId}";
            Log.Information($"[Debug] op=pet-party-node11-child row={rowIndex} path={childPath} " +
                $"nodeType={child->GetNodeType()} visible={child->NodeFlags.HasFlag(NodeFlags.Visible)} " +
                $"drawFlags=0x{(uint)child->DrawFlags:X}");
            rowIndex++;

            var componentNode = child->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
            {
                DumpPetPartyComponentChildren(&componentNode->Component->UldManager, childPath, ref rowIndex);
            }
        }
    }

    private static AtkResNode* FindNodeById(AtkUldManager* manager, uint nodeId, HashSet<nint> visited)
    {
        if (manager == null || manager->NodeList == null)
        {
            return null;
        }

        var count = Math.Min(manager->NodeListCount, (ushort)10000);
        for (var index = 0; index < count; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !visited.Add((nint)node))
            {
                continue;
            }

            if (node->NodeId == nodeId)
            {
                return node;
            }

            if (node->GetNodeType() == NodeType.Component)
            {
                var component = node->GetAsAtkComponentNode();
                if (component != null && component->Component != null)
                {
                    var found = FindNodeById(&component->Component->UldManager, nodeId, visited);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
        }

        return null;
    }

    private void DumpPetPartyNodeChildren(AtkResNode* node, string path, ref int rowIndex)
    {
        if (node == null)
        {
            return;
        }

        // List components can report a non-component NodeType even though their
        // component payload contains the renderer list.
        var component = node->GetAsAtkComponentNode();
        if (component == null || component->Component == null)
        {
            return;
        }

        var list = &component->Component->UldManager;
        var count = Math.Min(list->NodeListCount, (ushort)1000);
        for (var index = 0; index < count; index++)
        {
            var child = list->NodeList[index];
            if (child == null)
            {
                continue;
            }

            var childPath = $"{path}>{child->NodeId}";
            Log.Information($"[Debug] op=pet-party-node11-child row={rowIndex} path={childPath} " +
                $"nodeType={child->GetNodeType()} visible={child->NodeFlags.HasFlag(NodeFlags.Visible)} " +
                $"drawFlags=0x{(uint)child->DrawFlags:X}");
            rowIndex++;
            DumpPetPartyNodeChildren(child, childPath, ref rowIndex);
        }
    }

    internal void LogStageDetailList()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName("XBMStageDetailList");
        if (addon == null || !addon->IsReady)
        {
            Log.Warning("[Debug] op=stage-detail-list available=false reason=XBMStageDetailList-not-ready");
            return;
        }

        Log.Information($"[Debug] op=stage-detail-list available=true addon=XBMStageDetailList " +
            $"atkValuesCount={addon->AtkValuesCount} visible={addon->IsVisible} rootNode={(nint)addon->RootNode:X}");
        var modeDetected = TryReadStageDetailMode(addon, out var modeIndex);
        Log.Information($"[Debug] op=stage-detail-mode detected={modeDetected} " +
            $"mode={(modeDetected ? GetCrucibleModeName(modeIndex) : "unknown")} " +
            $"modeIndex={(modeDetected ? modeIndex : -1)} configured={CrucibleModeName}.");

        var nodeCount = 0;
        var visitedNodes = new HashSet<nint>();
        DumpStageDetailNodeList(&addon->UldManager, "addon", ref nodeCount, visitedNodes);
        Log.Information($"[Debug] op=stage-detail-list-result nodeCount={nodeCount}");
    }

    internal void LogTreasureNodes()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName("XBMContentsTreasure");
        if (addon == null || !addon->IsReady)
        {
            Log.Warning("[Debug] op=treasure-nodes available=false reason=XBMContentsTreasure-not-ready");
            return;
        }

        Log.Information($"[Debug] op=treasure-nodes available=true addon=XBMContentsTreasure " +
            $"atkValuesCount={addon->AtkValuesCount} visible={addon->IsVisible} rootNode={(nint)addon->RootNode:X}");

        var nodeCount = 0;
        var visitedNodes = new HashSet<nint>();
        DumpTreasureNodeList(&addon->UldManager, "addon", ref nodeCount, visitedNodes);
        Log.Information($"[Debug] op=treasure-nodes-result nodeCount={nodeCount}");
    }

    internal void AnalyzeTreasureSelection()
    {
        var manager = RaptureAtkUnitManager.Instance();
        var addon = manager == null ? null : manager->GetAddonByName("XBMContentsTreasure");
        if (addon == null || !addon->IsReady)
        {
            Log.Warning("[TreasureAnalysis] available=false reason=XBMContentsTreasure-not-ready");
            return;
        }

        Log.Information("[TreasureAnalysis] begin");
        var knifeValueIndexes = FindAtkItemValueIndexes(addon, ThiefsKnifeItemId);
        var hasThiefsKnife = knifeValueIndexes.Count > 0;
        var selectionCount = hasThiefsKnife ? 2 : 1;
        Log.Information($"[TreasureAnalysis] thiefsKnife={hasThiefsKnife} itemId={ThiefsKnifeItemId} " +
            $"atkValueIndexes={string.Join(",", knifeValueIndexes)} requiredSelections={selectionCount}");

        var cards = ResolveTreasureCards(addon);
        foreach (var card in cards)
        {
            var knownRecovery = RecoveryItems.TryGetValue(card.ItemId, out var recoveryItem);
            Log.Information($"[TreasureAnalysis] card option={card.OptionIndex} rowId={card.RowId} itemId={card.ItemId} " +
                $"acquired={card.Acquired} healing={knownRecovery} healPercent={(knownRecovery ? recoveryItem.HealPercent : 0)} " +
                $"ash={(knownRecovery && recoveryItem.IsAsh)} name=\"{EscapeLog(card.Title)}\" " +
                $"effect=\"{EscapeLog(card.Effect.Replace("\r", "\\r").Replace("\n", "\\n"))}\"");
        }

        var usedOptions = new HashSet<int>();
        for (var selection = 1; selection <= selectionCount; selection++)
        {
            var selected = SelectTreasureCard(cards, usedOptions);
            if (selected != null)
            {
                usedOptions.Add(selected.OptionIndex);
                var knownRecovery = RecoveryItems.TryGetValue(selected.ItemId, out var recoveryItem);
                Log.Information($"[TreasureAnalysis] plan selection={selection}/{selectionCount} option={selected.OptionIndex} " +
                    $"itemId={selected.ItemId} name=\"{EscapeLog(selected.Title)}\" " +
                    $"healPercent={(knownRecovery ? recoveryItem.HealPercent : 0)} ash={(knownRecovery && recoveryItem.IsAsh)} " +
                    $"reason={GetTreasureSelectionReason(selected)} gearTier={GetTreasureGearTier(selected)}");
                continue;
            }

            Log.Warning($"[TreasureAnalysis] plan selection={selection}/{selectionCount} reason=no-unused-choice");
        }

        Log.Information("[TreasureAnalysis] end");
    }

    private List<TreasureCardInfo> ResolveTreasureCards(
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon)
    {
        var cards = new List<TreasureCardInfo>();
        var visitedNodes = new HashSet<nint>();
        CollectTreasureCardNodes(&addon->UldManager, cards, visitedNodes);

        foreach (var card in cards)
        {
            var visibleText = card.TextNodes.Where(node => node.Visible).ToList();
            card.Title = visibleText.OrderBy(node => node.ScreenY).FirstOrDefault()?.Text.Trim() ?? string.Empty;
            card.Effect = visibleText.OrderByDescending(node => node.ScreenY).FirstOrDefault()?.Text.Trim() ?? string.Empty;
        }

        var xbmItems = ReadXbmItemRecords(addon);
        var associatedCards = new HashSet<TreasureCardInfo>();
        var optionIndex = 0;
        foreach (var item in xbmItems.OrderBy(record => record.AtkIndex))
        {
            var card = cards.FirstOrDefault(candidate => !associatedCards.Contains(candidate) &&
                string.Equals(NormalizeTreasureText(candidate.Title), NormalizeTreasureText(item.Name), StringComparison.Ordinal));
            if (card == null)
            {
                continue;
            }

            card.OptionIndex = optionIndex++;
            card.RowId = item.RowId;
            card.ItemId = item.ItemId;
            associatedCards.Add(card);
        }

        return cards;
    }

    private TreasureDataSnapshot ReadTreasureDataSnapshot(
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon)
        => new()
        {
            Cards = ResolveTreasureCards(addon),
            KnifeValueIndexes = FindAtkItemValueIndexes(addon, ThiefsKnifeItemId),
        };

    private bool PollTreasureData(TreasureDataSnapshot snapshot, out bool timedOut)
    {
        var now = DateTime.UtcNow;
        treasure.DataPollCount++;
        var signature = snapshot.Signature;
        treasure.StableDataPolls = string.Equals(signature, treasure.LastDataSignature, StringComparison.Ordinal)
            ? treasure.StableDataPolls + 1
            : 1;
        treasure.LastDataSignature = signature;

        var elapsed = now - treasure.DataFirstObservedAt;
        timedOut = elapsed >= TimeSpan.FromMilliseconds(TreasureDataMaximumWaitMilliseconds);
        var stable = snapshot.CardsReady &&
            treasure.StableDataPolls >= TreasureDataStablePollsRequired &&
            elapsed >= TimeSpan.FromMilliseconds(TreasureDataMinimumWaitMilliseconds);
        Log.Debug($"[Entry] {TreasureRouteName()} treasure data poll={treasure.DataPollCount} " +
            $"cards={snapshot.Cards.Count} cardsReady={snapshot.CardsReady} " +
            $"knifeDetected={snapshot.KnifeValueIndexes.Count > 0} " +
            $"knifeIndexes={string.Join(",", snapshot.KnifeValueIndexes)} " +
            $"stablePolls={treasure.StableDataPolls} elapsedMs={(int)elapsed.TotalMilliseconds} " +
            $"timedOut={timedOut}.");
        return stable;
    }

    private static bool AreTreasureCardsReady(List<TreasureCardInfo> cards)
        => cards.Count >= 4 && cards.All(card =>
            card.OptionIndex >= 0 && card.RowId != 0 && card.ItemId != 0 &&
            !string.IsNullOrWhiteSpace(card.Title));

    private static TreasureCardInfo? SelectTreasureCard(
        List<TreasureCardInfo> cards,
        HashSet<int> usedOptionIndexes)
    {
        var availableCards = cards
            .Where(card => card.OptionIndex >= 0 && !card.Acquired &&
                !usedOptionIndexes.Contains(card.OptionIndex))
            .ToList();

        return availableCards
            .Where(card => RecoveryItems.ContainsKey(card.ItemId))
            .OrderByDescending(card => RecoveryItems[card.ItemId].HealPercent)
            .ThenByDescending(card => RecoveryItems[card.ItemId].IsAsh)
            .ThenBy(card => card.OptionIndex)
            .FirstOrDefault()
            ?? availableCards
                .Where(card => BeastGearPriority.ContainsKey(card.ItemId))
                .OrderBy(card => BeastGearPriority[card.ItemId])
                .ThenBy(card => card.OptionIndex)
                .FirstOrDefault()
            ?? availableCards.FirstOrDefault(card => card.OptionIndex == 0)
            ?? availableCards.FirstOrDefault(card => card.OptionIndex == 1)
            ?? availableCards.FirstOrDefault();
    }

    private static string GetTreasureSelectionReason(TreasureCardInfo? selected)
    {
        if (selected == null)
        {
            return "no-unused-choice";
        }

        if (RecoveryItems.ContainsKey(selected.ItemId))
        {
            return "healing-priority";
        }

        return BeastGearPriority.TryGetValue(selected.ItemId, out var tier)
            ? $"gear-tier={GetGearTierLabel(tier)} value={tier}"
            : "default-fallback";
    }

    private static string GetTreasureGearTier(TreasureCardInfo? selected)
        => selected != null && BeastGearPriority.TryGetValue(selected.ItemId, out var rank)
            ? $"{GetGearTierLabel(rank)}({rank})"
            : "none";

    private static string GetGearTierLabel(int tier)
        => tier switch
        {
            1 => "S",
            2 => "A",
            3 => "B",
            4 => "C",
            5 => "D",
            _ => "unknown",
        };

    private void LogTreasureChoices(List<TreasureCardInfo> cards)
    {
        foreach (var card in cards)
        {
            var knownRecovery = RecoveryItems.TryGetValue(card.ItemId, out var recoveryItem);
            var isGear = BeastGearPriority.TryGetValue(card.ItemId, out var gearTier);
            Log.Information($"[Entry] {TreasureRouteName()} possible option={card.OptionIndex} rowId={card.RowId} " +
                $"itemId={card.ItemId} acquired={card.Acquired} healing={knownRecovery} " +
                $"healPercent={(knownRecovery ? recoveryItem.HealPercent : 0)} ash={(knownRecovery && recoveryItem.IsAsh)} " +
                $"gear={isGear} gearTier={(isGear ? GetGearTierLabel(gearTier) : "none")} " +
                $"gearTierValue={(isGear ? gearTier : 0)} " +
                $"name=\"{EscapeLog(card.Title)}\" effect=\"{EscapeLog(card.Effect.Replace("\r", "\\r").Replace("\n", "\\n"))}\"");
        }
    }

    private void CollectTreasureCardNodes(
        AtkUldManager* manager,
        List<TreasureCardInfo> cards,
        HashSet<nint> visitedNodes)
    {
        if (manager == null || manager->NodeList == null)
        {
            return;
        }

        var count = Math.Min(manager->NodeListCount, (ushort)10000);
        for (var index = 0; index < count; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !visitedNodes.Add((nint)node))
            {
                continue;
            }

            if (node->GetNodeType() != NodeType.Component)
            {
                continue;
            }

            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode == null || componentNode->Component == null)
            {
                continue;
            }

            var componentType = componentNode->Component->GetComponentType().ToString();
            if (string.Equals(componentType, "Button", StringComparison.OrdinalIgnoreCase))
            {
                var card = new TreasureCardInfo
                {
                    ScreenX = node->ScreenX,
                    ScreenY = node->ScreenY,
                };
                CollectTreasureCardDetails(&componentNode->Component->UldManager, card, visitedNodes);
                if (card.TextNodes.Count > 0 && card.HasXbmItem)
                {
                    cards.Add(card);
                }

                continue;
            }

            CollectTreasureCardNodes(&componentNode->Component->UldManager, cards, visitedNodes);
        }
    }

    private void CollectTreasureCardDetails(
        AtkUldManager* manager,
        TreasureCardInfo card,
        HashSet<nint> visitedNodes)
    {
        if (manager == null || manager->NodeList == null)
        {
            return;
        }

        var count = Math.Min(manager->NodeListCount, (ushort)10000);
        for (var index = 0; index < count; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !visitedNodes.Add((nint)node))
            {
                continue;
            }

            var nodeType = node->GetNodeType();
            if (nodeType == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    card.TextNodes.Add(new TreasureNodeTextInfo
                    {
                        Text = text,
                        Visible = node->NodeFlags.HasFlag(NodeFlags.Visible),
                        ScreenX = node->ScreenX,
                        ScreenY = node->ScreenY,
                    });
                    if (string.Equals(text.Trim(), "Acquired.", StringComparison.OrdinalIgnoreCase) &&
                        node->NodeFlags.HasFlag(NodeFlags.Visible))
                    {
                        card.Acquired = true;
                    }
                }
            }
            else if (nodeType == NodeType.Component)
            {
                var componentNode = node->GetAsAtkComponentNode();
                if (componentNode == null || componentNode->Component == null)
                {
                    continue;
                }

                if (string.Equals(componentNode->Component->GetComponentType().ToString(),
                        "XBMItem", StringComparison.OrdinalIgnoreCase))
                {
                    card.HasXbmItem = true;
                }

                CollectTreasureCardDetails(&componentNode->Component->UldManager, card, visitedNodes);
            }
        }
    }

    private List<XbmItemRecord> ReadXbmItemRecords(
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon)
    {
        var records = new List<XbmItemRecord>();
        var seenRows = new HashSet<uint>();
        var sheet = DataManager.GetExcelSheet<RawRow>(name: "XBMItem");
        for (var index = 0; index < addon->AtkValuesCount; index++)
        {
            var value = addon->AtkValues[index];
            if (value.Type != AtkValueType.UInt || !seenRows.Add(value.UInt) ||
                !sheet.TryGetRow(value.UInt, out var row))
            {
                continue;
            }

            try
            {
                var itemId = Convert.ToUInt32(row.ReadColumn(1));
                var name = row.ReadColumn(11)?.ToString() ?? string.Empty;
                if (itemId != 0 && !string.IsNullOrWhiteSpace(name))
                {
                    records.Add(new XbmItemRecord
                    {
                        AtkIndex = index,
                        RowId = value.UInt,
                        ItemId = itemId,
                        Name = name,
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"[TreasureAnalysis] XBMItem row={value.UInt} read failed error={ex.GetType().Name}");
            }
        }

        return records;
    }

    private static List<int> FindAtkItemValueIndexes(
        FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon,
        uint itemId)
    {
        var indexes = new List<int>();
        for (var index = 0; index < addon->AtkValuesCount; index++)
        {
            var value = addon->AtkValues[index];
            if (value.Type == AtkValueType.UInt && value.UInt == itemId)
            {
                indexes.Add(index);
            }
        }

        return indexes;
    }

    private static string NormalizeTreasureText(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private void DumpTreasureNodeList(
        AtkUldManager* manager,
        string path,
        ref int nodeCount,
        HashSet<nint> visitedNodes)
    {
        if (manager == null || manager->NodeList == null || nodeCount >= 10000)
        {
            return;
        }

        var nodeCountInList = Math.Min(manager->NodeListCount, (ushort)10000);
        for (var index = 0; index < nodeCountInList && nodeCount < 10000; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !visitedNodes.Add((nint)node))
            {
                continue;
            }

            DumpTreasureNode(node, $"{path}.node[{index}]", ref nodeCount, visitedNodes);
        }
    }

    private void DumpTreasureNode(
        AtkResNode* node,
        string path,
        ref int nodeCount,
        HashSet<nint> visitedNodes)
    {
        nodeCount++;
        var visible = node->NodeFlags.HasFlag(NodeFlags.Visible);
        var nodeType = node->GetNodeType();
        if (nodeType == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Log.Information($"[Debug] op=treasure-node-text path={path} nodeId={node->NodeId} " +
                    $"visible={visible} position=<{node->ScreenX:0.0},{node->ScreenY:0.0}> " +
                    $"size=<{node->Width},{node->Height}> text=\"{EscapeLog(text)}\"");
            }
        }
        else if (nodeType == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var componentType = componentNode != null && componentNode->Component != null
                ? componentNode->Component->GetComponentType().ToString()
                : "Unknown";
            Log.Information($"[Debug] op=treasure-node-component path={path} nodeId={node->NodeId} " +
                $"visible={visible} componentType={componentType} position=<{node->ScreenX:0.0},{node->ScreenY:0.0}> " +
                $"size=<{node->Width},{node->Height}>");

            if (componentNode != null && componentNode->Component != null)
            {
                DumpTreasureNodeList(
                    &componentNode->Component->UldManager,
                    $"{path}.component",
                    ref nodeCount,
                    visitedNodes);
            }
        }
    }

    private void DumpStageDetailNodeList(
        AtkUldManager* manager,
        string path,
        ref int nodeCount,
        HashSet<nint> visitedNodes)
    {
        if (manager == null || manager->NodeList == null || nodeCount >= 10000)
        {
            return;
        }

        var nodeCountInList = Math.Min(manager->NodeListCount, (ushort)10000);
        for (var index = 0; index < nodeCountInList && nodeCount < 10000; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !visitedNodes.Add((nint)node))
            {
                continue;
            }

            DumpStageDetailNode(node, $"{path}.node[{index}]", ref nodeCount, visitedNodes);
        }
    }

    private void DumpStageDetailNode(
        AtkResNode* node,
        string path,
        ref int nodeCount,
        HashSet<nint> visitedNodes)
    {
        nodeCount++;
        var visible = node->NodeFlags.HasFlag(NodeFlags.Visible);
        var nodeType = node->GetNodeType();
        if (nodeType == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                var marker = text.Contains("Challenge This Board", StringComparison.OrdinalIgnoreCase)
                    ? " match=challenge-board"
                    : string.Empty;
                Log.Information($"[Debug] op=stage-detail-text path={path} nodeId={node->NodeId} visible={visible} " +
                    $"position=<{node->ScreenX:0.0},{node->ScreenY:0.0}> size=<{node->Width},{node->Height}> " +
                    $"text=\"{EscapeLog(text)}\"{marker}");
            }
        }
        else if (nodeType == NodeType.Component)
        {
            var componentNode = node->GetAsAtkComponentNode();
            var componentType = componentNode != null && componentNode->Component != null
                ? componentNode->Component->GetComponentType().ToString()
                : "Unknown";
            Log.Information($"[Debug] op=stage-detail-component path={path} nodeId={node->NodeId} visible={visible} " +
                $"componentType={componentType} position=<{node->ScreenX:0.0},{node->ScreenY:0.0}> " +
                $"size=<{node->Width},{node->Height}>");

            if (componentNode != null && componentNode->Component != null)
            {
                DumpStageDetailNodeList(
                    &componentNode->Component->UldManager,
                    $"{path}.component",
                    ref nodeCount,
                    visitedNodes);
            }
        }
    }

    private static bool TryReadStageDetailMode(AtkUnitBase* addon, out int modeIndex)
    {
        modeIndex = -1;
        if (addon == null)
        {
            return false;
        }

        var matches = new List<(string Text, float ScreenY)>();
        CollectStageDetailModeTexts(&addon->UldManager, matches, new HashSet<nint>());
        foreach (var match in matches.OrderBy(match => match.ScreenY))
        {
            for (var index = 0; index < CrucibleModeNames.Length; index++)
            {
                if (string.Equals(match.Text, CrucibleModeNames[index], StringComparison.OrdinalIgnoreCase))
                {
                    modeIndex = index;
                    return true;
                }
            }
        }

        return false;
    }

    private static void CollectStageDetailModeTexts(
        AtkUldManager* manager,
        List<(string Text, float ScreenY)> matches,
        HashSet<nint> visitedNodes)
    {
        if (manager == null || manager->NodeList == null)
        {
            return;
        }

        var count = Math.Min(manager->NodeListCount, (ushort)10000);
        for (var index = 0; index < count; index++)
        {
            var node = manager->NodeList[index];
            if (node == null || !visitedNodes.Add((nint)node) ||
                !node->NodeFlags.HasFlag(NodeFlags.Visible))
            {
                continue;
            }

            if (node->GetNodeType() == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString().Trim();
                if (CrucibleModeNames.Any(mode => string.Equals(mode, text, StringComparison.OrdinalIgnoreCase)))
                {
                    matches.Add((text, node->ScreenY));
                }

                continue;
            }

            if (node->GetNodeType() != NodeType.Component)
            {
                continue;
            }

            var componentNode = node->GetAsAtkComponentNode();
            if (componentNode != null && componentNode->Component != null)
            {
                CollectStageDetailModeTexts(&componentNode->Component->UldManager, matches, visitedNodes);
            }
        }
    }

    private static int ReadAtkInt(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) && addon->AtkValues[index].Type == AtkValueType.Int
            ? addon->AtkValues[index].Int
            : 0;

    private static uint ReadAtkUInt(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) && addon->AtkValues[index].Type == AtkValueType.UInt
            ? addon->AtkValues[index].UInt
            : 0;

    private static bool ReadAtkBool(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) && addon->AtkValues[index].Type == AtkValueType.Bool && addon->AtkValues[index].Bool;

    private static string ReadAtkString(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) &&
            addon->AtkValues[index].Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.WideString or AtkValueType.ManagedString
            ? addon->AtkValues[index].GetValueAsString()
            : string.Empty;

    private static bool IsAtkIndexValid(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => addon != null && addon->AtkValues != null && index >= 0 && index < addon->AtkValuesCount;

    private void DumpAddon(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, string source, int index)
    {
        Log.Information($"[Debug] op=addon source={source} index={index} name=\"{EscapeLog(addon->NameString)}\" " +
            $"address={(nint)addon:X} ready={addon->IsReady} visible={addon->IsVisible} " +
            $"id={addon->Id} parentId={addon->ParentId} hostId={addon->HostId} blockedParentId={addon->BlockedParentId} " +
            $"drawOrder={addon->DrawOrderIndex} atkValuesCount={addon->AtkValuesCount} " +
            $"position=<{addon->X},{addon->Y}> scale={addon->Scale:0.000} alpha={addon->Alpha} " +
            $"rootNode={(nint)addon->RootNode:X}");

        if (addon->AtkValues == null)
        {
            return;
        }

        for (var valueIndex = 0; valueIndex < addon->AtkValuesCount; valueIndex++)
        {
            var value = addon->AtkValues[valueIndex];
            Log.Information($"[Debug] op=addon-atk-value addon=\"{EscapeLog(addon->NameString)}\" " +
                $"index={valueIndex} type={value.Type} int={value.Int} uint={value.UInt} " +
                $"float={value.Float:0.000} value=\"{EscapeLog(value.GetValueAsString())}\"");
        }
    }

    private static string EscapeLog(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string FormatVector(Vector3 position)
        => $"<{position.X:0.000},{position.Y:0.000},{position.Z:0.000}>";

    private static string FormatQuaternion(Quaternion rotation)
        => $"<{rotation.X:0.000},{rotation.Y:0.000},{rotation.Z:0.000},{rotation.W:0.000}>";
}

[Serializable]
public sealed class PluginConfiguration : IPluginConfiguration
{
    public int Version { get; set; } = 1;
    public int CrucibleModeIndex { get; set; }
    public int PetHealthThresholdPercent { get; set; } = 100;
    public float MainWindowRunWidth { get; set; } = 640f;
    public float MainWindowRunHeight { get; set; } = 420f;
    public float MainWindowBoard1Width { get; set; } = 820f;
    public float MainWindowBoard1Height { get; set; } = 680f;
    public float MainWindowBoard2Width { get; set; } = 820f;
    public float MainWindowBoard2Height { get; set; } = 680f;
    public float MainWindowBoard3Width { get; set; } = 820f;
    public float MainWindowBoard3Height { get; set; } = 680f;
    public float MainWindowFirstMastersWidth { get; set; } = 820f;
    public float MainWindowFirstMastersHeight { get; set; } = 680f;
    public List<int> Board1FamiliarNumbers { get; set; } = new();
    public List<int> Board2FamiliarNumbers { get; set; } = new();
    public List<int> Board3FamiliarNumbers { get; set; } = new();
    public List<int> FirstMastersBoardFamiliarNumbers { get; set; } = new();
    public List<int> Board1StageFamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage1FamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage3BFamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage5FamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage7FamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage10FamiliarNumbers { get; set; } = new();
    public List<int> Board3Stage1FamiliarNumbers { get; set; } = new();
    public List<int> Board3Stage2FamiliarNumbers { get; set; } = new();
    public List<int> Board3Stage4AFamiliarNumbers { get; set; } = new();
    public List<int> Board3Stage4BFamiliarNumbers { get; set; } = new();
    public List<int> Board3Stage6FamiliarNumbers { get; set; } = new();
    public List<int> Board3Stage8FamiliarNumbers { get; set; } = new();
    public List<int> Board3Stage12FamiliarNumbers { get; set; } = new();
    public List<int> FirstMastersMove1FamiliarNumbers { get; set; } = new();
    public List<int> FirstMastersMove2BFamiliarNumbers { get; set; } = new();
    public List<int> FirstMastersMove5FamiliarNumbers { get; set; } = new();
    public List<int> FirstMastersMove7BFamiliarNumbers { get; set; } = new();
    public List<int> FirstMastersMove9FamiliarNumbers { get; set; } = new();

    public void Normalize()
    {
        CrucibleModeIndex = Math.Clamp(CrucibleModeIndex, 0, 3);
        PetHealthThresholdPercent = Math.Clamp(PetHealthThresholdPercent, 1, 100);
        MainWindowRunWidth = NormalizeWindowDimension(MainWindowRunWidth, 640f, 200f, 4000f);
        MainWindowRunHeight = NormalizeWindowDimension(MainWindowRunHeight, 420f, 200f, 2400f);
        MainWindowBoard1Width = NormalizeWindowDimension(MainWindowBoard1Width, 820f, 200f, 4000f);
        MainWindowBoard1Height = NormalizeWindowDimension(MainWindowBoard1Height, 680f, 200f, 2400f);
        MainWindowBoard2Width = NormalizeWindowDimension(MainWindowBoard2Width, 820f, 200f, 4000f);
        MainWindowBoard2Height = NormalizeWindowDimension(MainWindowBoard2Height, 680f, 200f, 2400f);
        MainWindowBoard3Width = NormalizeWindowDimension(MainWindowBoard3Width, 820f, 200f, 4000f);
        MainWindowBoard3Height = NormalizeWindowDimension(MainWindowBoard3Height, 680f, 200f, 2400f);
        MainWindowFirstMastersWidth = NormalizeWindowDimension(MainWindowFirstMastersWidth, 820f, 200f, 4000f);
        MainWindowFirstMastersHeight = NormalizeWindowDimension(MainWindowFirstMastersHeight, 680f, 200f, 2400f);
        Board1FamiliarNumbers = Board1FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(10).ToList();
        Board2FamiliarNumbers = Board2FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(12).ToList();
        Board3FamiliarNumbers = Board3FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(14).ToList();
        FirstMastersBoardFamiliarNumbers = FirstMastersBoardFamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(12).ToList();
        Board1StageFamiliarNumbers = NormalizeStageRoster(Board1StageFamiliarNumbers);
        Board2Stage1FamiliarNumbers = NormalizeStageRoster(Board2Stage1FamiliarNumbers);
        Board2Stage3BFamiliarNumbers = NormalizeStageRoster(Board2Stage3BFamiliarNumbers);
        Board2Stage5FamiliarNumbers = NormalizeStageRoster(Board2Stage5FamiliarNumbers);
        Board2Stage7FamiliarNumbers = NormalizeStageRoster(Board2Stage7FamiliarNumbers);
        Board2Stage10FamiliarNumbers = NormalizeStageRoster(Board2Stage10FamiliarNumbers);
        Board3Stage1FamiliarNumbers = NormalizeStageRoster(Board3Stage1FamiliarNumbers);
        Board3Stage2FamiliarNumbers = NormalizeStageRoster(Board3Stage2FamiliarNumbers);
        Board3Stage4AFamiliarNumbers = NormalizeStageRoster(Board3Stage4AFamiliarNumbers);
        Board3Stage4BFamiliarNumbers = NormalizeStageRoster(Board3Stage4BFamiliarNumbers);
        Board3Stage6FamiliarNumbers = NormalizeStageRoster(Board3Stage6FamiliarNumbers);
        Board3Stage8FamiliarNumbers = NormalizeStageRoster(Board3Stage8FamiliarNumbers);
        Board3Stage12FamiliarNumbers = NormalizeStageRoster(Board3Stage12FamiliarNumbers);
        FirstMastersMove1FamiliarNumbers = NormalizeStageRoster(FirstMastersMove1FamiliarNumbers);
        FirstMastersMove2BFamiliarNumbers = NormalizeStageRoster(FirstMastersMove2BFamiliarNumbers);
        FirstMastersMove5FamiliarNumbers = NormalizeStageRoster(FirstMastersMove5FamiliarNumbers);
        FirstMastersMove7BFamiliarNumbers = NormalizeStageRoster(FirstMastersMove7BFamiliarNumbers);
        FirstMastersMove9FamiliarNumbers = NormalizeStageRoster(FirstMastersMove9FamiliarNumbers);
    }

    private static float NormalizeWindowDimension(float value, float fallback, float minimum, float maximum)
        => float.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;

    private static List<int> NormalizeStageRoster(List<int> roster)
    {
        var normalized = new List<int>(3);
        var seen = new HashSet<int>();
        foreach (var number in roster.Take(3))
        {
            normalized.Add(number is >= 1 and <= 50 && seen.Add(number) ? number : 0);
        }

        while (normalized.Count < 3)
        {
            normalized.Add(0);
        }

        return normalized;
    }
}
