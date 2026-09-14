using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Configuration;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
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
using UnbrokenHelper.Windows;
using Callback = ECommons.Automation.Callback;

namespace UnbrokenHelper;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string CommandName = "/unbroken";
    private const float StageTransitionArrivalRadius = 0.5f;
    private const float CombatPositionArrivalRadius = 1.0f;
    private const int RecoverySessionTimeoutSeconds = 30;
    private const int CombatRecoveryCheckMilliseconds = 500;
    private const int TreasureDataPollMilliseconds = 250;
    private const int TreasureDataMinimumWaitMilliseconds = 750;
    private const int TreasureDataMaximumWaitMilliseconds = 2000;
    private const int TreasureDataStablePollsRequired = 2;
    private const uint ThiefsKnifeItemId = 243057;
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
    private uint BoardTerritory => selectedBoard == 1 ? 1339u : 1340u;
    private Vector3 CurrentStageStartPosition => selectedBoard == 1
        ? board1RouteNode?.Position ?? Vector3.Zero
        : CurrentRouteNode?.Position ?? new Vector3(-700.000f, 0f, -8.318f);
    private Vector3 SecondBoardShopPosition => new(-700.003f, 0f, -18.000f);
    private Vector3 Stage9ShopPosition => new(-700.271f, 0f, -74.179f);
    private List<int> ConfiguredFamiliarNumbers => selectedBoard == 1
        ? configuration.Board1FamiliarNumbers
        : configuration.Board2FamiliarNumbers;
    private List<int> ConfiguredStageFamiliarNumbers => selectedBoard == 1
        ? configuration.Board1StageFamiliarNumbers
        : CurrentRouteNode?.FamiliarStageNumber switch
        {
            3 => configuration.Board2Stage3BFamiliarNumbers,
            5 => configuration.Board2Stage5FamiliarNumbers,
            7 => configuration.Board2Stage7FamiliarNumbers,
            10 => configuration.Board2Stage10FamiliarNumbers,
            _ => configuration.Board2Stage1FamiliarNumbers,
        };

    private readonly PluginConfiguration configuration;
    private readonly RouteNode? board1RouteNode;
    private readonly IReadOnlyDictionary<Board2RoutePhase, RouteNode> board2RouteNodes;
    private readonly DebugWindow debugWindow;
    private readonly ICallGateSubscriber<bool> vnavIsReady;
    private readonly ICallGateSubscriber<Vector3, bool, bool> vnavPathfindAndMoveTo;
    private readonly ICallGateSubscriber<object> vnavStop;
    private readonly ICallGateSubscriber<string, bool> bossModSetActive;
    private readonly ICallGateSubscriber<bool> bossModClearActive;
    private readonly ICallGateSubscriber<byte, object> rsrChangeMode;

    private enum RouteNodeKind
    {
        Battle,
        Rest,
        Treasure,
        Shop,
    }

    private enum Board2RoutePhase
    {
        None,
        Stage1,
        Shop2,
        Stage3B,
        Rest4B,
        Stage5,
        Treasure6,
        Stage7,
        Treasure8,
        Shop9,
        Stage10,
    }

    private sealed class RouteNode
    {
        internal Board2RoutePhase Phase { get; init; }
        internal RouteNodeKind Kind { get; init; }
        internal int StageNumber { get; init; }
        internal string Variant { get; init; } = string.Empty;
        internal string Name { get; init; } = string.Empty;
        internal Vector3 Position { get; init; }
        internal string ExpectedAddon { get; init; } = string.Empty;
        internal Board2RoutePhase NextPhase { get; init; }
        internal int RecoveryThreshold { get; init; } = 60;
        internal int RecoveryTarget { get; init; } = 90;
        internal int FamiliarStageNumber { get; init; }
        internal string FamiliarRosterKey { get; init; } = string.Empty;
        internal bool DirectResultFlow { get; init; }
        internal uint[] CombatTargetBaseIds { get; init; } = Array.Empty<uint>();
        internal uint PriorityTargetBaseId { get; init; }

        internal string Key => Variant.Length == 0 ? $"Stage{StageNumber}" : $"Stage{StageNumber}{Variant}";
    }

    private RouteNode? CurrentRouteNode
        => selectedBoard == 1
            ? board1RouteNode
            : board2RouteNodes.GetValueOrDefault(board2Route);

    private EntryState entryState;
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
    private bool loopEntry;
    private Board2RoutePhase board2Route;
    private Board2RoutePhase recoveryNoUsableItemsRoute;
    private int recoveryNoUsableItemsThreshold;
    private readonly TreasureSession treasure = new();
    private readonly PetPartySession petParty = new();
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
        internal uint StartingHp { get; set; }
        internal DateTime AttemptStarted { get; set; }
        internal DateTime HpPollStarted { get; set; }
        internal DateTime LastCheckLog { get; set; }
        internal DateTime LastCombatRecoveryCheck { get; set; }
        internal int ThresholdPercent { get; set; } = 60;
        internal int TargetPercent { get; set; } = 90;
        internal bool RsrDisabled { get; set; }
        internal HashSet<int> FailedSlots { get; } = new();

        internal bool Active => Continuation != EntryState.Idle;

        internal void Clear()
        {
            Continuation = EntryState.Idle;
            LastWaitLog = DateTime.MinValue;
            StartedAt = DateTime.MinValue;
            Attempt = 0;
            SelectedSlot = -1;
            StartingHp = 0;
            AttemptStarted = DateTime.MinValue;
            HpPollStarted = DateTime.MinValue;
            LastCheckLog = DateTime.MinValue;
            LastCombatRecoveryCheck = DateTime.MinValue;
            ThresholdPercent = 60;
            TargetPercent = 90;
            RsrDisabled = false;
            FailedSlots.Clear();
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
        Combat,
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
        AwaitSecondBoardShopClosed,
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
    internal string EntryTarget => entryState switch
    {
        EntryState.TargetLauda => "Lauda (BaseId 1059316)",
        EntryState.AwaitLaudaReady => "Lauda ready and targetable",
        EntryState.AwaitSelectIconString => "SelectIconString",
        EntryState.AwaitSelectString => "SelectString",
        EntryState.AwaitStageList => "XBMStageList",
        EntryState.AwaitStageDetail => "XBMStageDetailList",
        EntryState.AwaitFirstBoardTerritory => $"Territory {BoardTerritory}",
        EntryState.MoveToStageStart => $"VNav {CurrentRouteNode?.Name ?? "stage start"}",
        EntryState.AwaitPreChallengeRoster => "XBMPetParty pre-challenge roster",
        EntryState.AwaitStagePetParty => $"XBMPetParty {CurrentRouteNode?.Name ?? "battle"} roster",
        EntryState.SelectStagePetParty => $"XBMPetParty stage familiar index {petParty.StageToggleIndex}",
        EntryState.OpenFamiliarNotebook => "XBMPetParty callback 5",
        EntryState.AwaitFamiliarNotebook => "XBMMonsterNotebook",
        EntryState.SelectFamiliarNotebook => "XBMMonsterNotebook reconciliation",
        EntryState.AwaitFamiliarRosterUpdate => "XBMPetParty pre-challenge roster update",
        EntryState.AwaitInInstanceStageDetail => $"XBMStageDetailList {CurrentRouteNode?.Name ?? "battle"}",
        EntryState.CommenceInInstanceStage => "XBMStageDetailList callback 8",
        EntryState.AwaitStageLoaded => "Stage conditions clear",
        EntryState.PrepareCombat => "Battlehorn 44881",
        EntryState.AwaitBattlehorn => "Battlehorn resolution",
        EntryState.ActivateBossMod => "BossMod AutoDuty Passive",
        EntryState.PrepareKnight => "Bone Knight action 46751",
        EntryState.AwaitKnightAction => "Bone Knight action resolution",
        EntryState.MoveBehindKnight => "VNav rear position",
        EntryState.Combat => GetCombatEntryTarget(),
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
        EntryState.AwaitRecoveryHp => "recovery HP increase",
        EntryState.MoveToStage4BRest => "VNav Stage 4B tent",
        EntryState.AwaitStage4BTent => "XBMPetParty",
        EntryState.RestStage4B => "XBMPetParty callback 3",
        EntryState.AwaitStage4BConfirmation => "SelectYesno after Stage 4B rest",
        EntryState.MoveToStage6Start => "VNav Stage 6 treasure spot",
        EntryState.MoveToStage10Start => "VNav Stage 10 start",
        EntryState.AwaitStage6Treasure => "XBMContentsTreasure",
        EntryState.SelectStage6Treasure => "XBMContentsTreasure selection",
        EntryState.AwaitStage6TreasureConfirmation => "SelectYesno after Stage 6 treasure",
        EntryState.AwaitStage8Treasure => "XBMContentsTreasure (Stage 8 manual position)",
        EntryState.MoveToStage8Start => "VNav Stage 8 treasure spot",
        EntryState.SelectStage8Treasure => "XBMContentsTreasure selection",
        EntryState.AwaitStage8TreasureConfirmation => "SelectYesno after Stage 8 treasure",
        EntryState.MoveToSecondBoardShop => "VNav Second Board item shop",
        EntryState.AwaitBoardExitTransitionStart => "Second Board exit transition",
        EntryState.AwaitBoardExitTransitionReady => "Second Board map ready",
        EntryState.AwaitSecondBoardShop => "XBMContentsItemShop",
        EntryState.CloseSecondBoardShop => "XBMContentsItemShop callback 0",
        EntryState.AwaitSecondBoardShopClosed => "XBMContentsItemShop closed",
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
        var routeCatalog = new BoardRouteCatalogLoader(Log).Load(PluginInterface);
        board1RouteNode = BuildRouteNode(routeCatalog.FindRoute("board1", "board1-default")?.Nodes.FirstOrDefault());
        board2RouteNodes = BuildBoard2Route(routeCatalog.FindRoute("board2", "board2-b"));
        if (board1RouteNode == null || board2RouteNodes.Count == 0)
        {
            Log.Error("[Data] Board route catalog did not provide the active Board 1 or Board 2 B route; automation will remain unavailable.");
        }
        vnavIsReady = PluginInterface.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        vnavPathfindAndMoveTo = PluginInterface.GetIpcSubscriber<Vector3, bool, bool>("vnavmesh.SimpleMove.PathfindAndMoveTo");
        vnavStop = PluginInterface.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        bossModSetActive = PluginInterface.GetIpcSubscriber<string, bool>("BossMod.Presets.SetActive");
        bossModClearActive = PluginInterface.GetIpcSubscriber<bool>("BossMod.Presets.ClearActive");
        rsrChangeMode = PluginInterface.GetIpcSubscriber<byte, object>("RotationSolverReborn.ChangeOperatingMode");
        Framework.Update += OnFrameworkUpdate;
        debugWindow = new DebugWindow(this);
        WindowSystem.AddWindow(debugWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Unbroken Helper debug window."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenDebugWindow;
        PluginInterface.UiBuilder.OpenConfigUi += OpenDebugWindow;

        Log.Information("Unbroken Helper loaded.");
    }

    internal IReadOnlyList<int> GetFamiliarRoster(int board)
        => board == 1 ? configuration.Board1FamiliarNumbers : configuration.Board2FamiliarNumbers;

    internal int FamiliarRosterCapacity(int board) => board == 1 ? 10 : 12;

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

    internal IReadOnlyList<int> GetStageFamiliarRoster(int board, int stage)
        => board == 1 ? configuration.Board1StageFamiliarNumbers
            : stage switch
            {
                3 => configuration.Board2Stage3BFamiliarNumbers,
                5 => configuration.Board2Stage5FamiliarNumbers,
                7 => configuration.Board2Stage7FamiliarNumbers,
                10 => configuration.Board2Stage10FamiliarNumbers,
                _ => configuration.Board2Stage1FamiliarNumbers,
            };

    internal void AddStageFamiliar(int board, int stage, int number)
    {
        var roster = GetMutableStageRoster(board, stage);
        var boardRoster = board == 1 ? configuration.Board1FamiliarNumbers : configuration.Board2FamiliarNumbers;
        if (!boardRoster.Contains(number) || roster.Contains(number) || roster.Count >= 3)
        {
            return;
        }

        roster.Add(number);
        SaveConfiguration();
    }

    internal void RemoveStageFamiliar(int board, int stage, int number)
    {
        if (GetMutableStageRoster(board, stage).Remove(number))
        {
            SaveConfiguration();
        }
    }

    private List<int> GetMutableStageRoster(int board, int stage)
        => board == 1 ? configuration.Board1StageFamiliarNumbers
            : stage switch
            {
                3 => configuration.Board2Stage3BFamiliarNumbers,
                5 => configuration.Board2Stage5FamiliarNumbers,
                7 => configuration.Board2Stage7FamiliarNumbers,
                10 => configuration.Board2Stage10FamiliarNumbers,
                _ => configuration.Board2Stage1FamiliarNumbers,
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

    internal void AddFamiliarToRoster(int board, int number)
    {
        var roster = board == 1 ? configuration.Board1FamiliarNumbers : configuration.Board2FamiliarNumbers;
        if (number is < 1 or > 50 || roster.Contains(number) || roster.Count >= FamiliarRosterCapacity(board))
        {
            return;
        }

        roster.Add(number);
        SaveConfiguration();
    }

    internal void RemoveFamiliarFromRoster(int board, int number)
    {
        var roster = board == 1 ? configuration.Board1FamiliarNumbers : configuration.Board2FamiliarNumbers;
        if (roster.Remove(number))
        {
            SaveConfiguration();
        }
    }

    private void SaveConfiguration() => PluginInterface.SavePluginConfig(configuration);

    private static IReadOnlyDictionary<Board2RoutePhase, RouteNode> BuildBoard2Route(BoardRouteDefinition? route)
    {
        var nodes = new Dictionary<Board2RoutePhase, RouteNode>();
        if (route == null || !route.Enabled)
        {
            return nodes;
        }

        foreach (var nodeData in route.Nodes)
        {
            var node = BuildRouteNode(nodeData);
            if (node == null || node.Phase == Board2RoutePhase.None)
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
            !Enum.TryParse<Board2RoutePhase>(nodeData.Phase, ignoreCase: true, out var phase) ||
            phase == Board2RoutePhase.None)
        {
            return null;
        }

        var nextPhase = string.IsNullOrWhiteSpace(nodeData.NextPhase)
            ? Board2RoutePhase.None
            : Enum.TryParse<Board2RoutePhase>(nodeData.NextPhase, ignoreCase: true, out var parsedNextPhase)
                ? parsedNextPhase
                : Board2RoutePhase.None;

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
        };
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
        ECommonsMain.Dispose();
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenDebugWindow;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenDebugWindow;
        CommandManager.RemoveHandler(CommandName);

        WindowSystem.RemoveAllWindows();
        debugWindow.Dispose();
    }

    internal void StartFirstBoardEntry() => StartBoardEntry(1, true);

    internal void StartSecondBoardEntry() => StartBoardEntry(2, true);

    private void StartBoardEntry(int board, bool loop)
    {
        if (EntryActive)
        {
            return;
        }

        ResetEntryRuntime("new entry");
        entryState = EntryState.AwaitLaudaReady;
        selectedBoard = board;
        loopEntry = loop;
        board2Route = Board2RoutePhase.None;
        entryStateStarted = DateTime.UtcNow;
        nextEntryAction = DateTime.MinValue;
        Log.Information($"[Entry] started board={selectedBoard} entry automation.");
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

        try
        {
            bossModClearActive.InvokeFunc();
        }
        catch (Exception ex)
        {
            Log.Debug($"[Combat] BossMod reset unavailable: {ex.Message}");
        }
    }

    private void ResetEntryRuntime(string reason)
    {
        if (potionDebugState != PotionDebugState.Idle)
        {
            potionDebugState = PotionDebugState.Idle;
            potionDebugStateStarted = DateTime.MinValue;
            Log.Information($"[Debug] potion-use cancelled reason=\"{EscapeLog(reason)}\".");
        }

        CancelRecovery(reason);
        TryStopVnav();
        movement.Clear();
        treasure.Clear();
        recovery.Clear();
        ResetCombatProviders();

        board2Route = Board2RoutePhase.None;
        recoveryNoUsableItemsRoute = Board2RoutePhase.None;
        recoveryNoUsableItemsThreshold = 0;
        petParty.Clear();
        knightSetupComplete = false;
        knightRearMoveStarted = false;
        knightRearDestination = Vector3.Zero;
        boardChallengeConfirmed = false;
        laudaReadySince = null;
        Log.Information($"[Entry] runtime reset reason=\"{EscapeLog(reason)}\".");
    }

    private void EndEntry(EntryState nextState, string reason)
    {
        var previousState = entryState;
        ResetEntryRuntime(reason);
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

        var stateTimeout = entryState is EntryState.AwaitFirstBoardTerritory or EntryState.MoveToStageStart or EntryState.AwaitStageLoaded or EntryState.MoveToStage4BRest or EntryState.MoveToStage6Start or EntryState.MoveToStage8Start or EntryState.MoveToStage10Start or EntryState.MoveToSecondBoardShop or EntryState.AwaitBoardExitTransitionStart or EntryState.AwaitBoardExitTransitionReady
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

        if (entryState == EntryState.Combat && selectedBoard == 2 &&
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
            case EntryState.AwaitSecondBoardShopClosed:
                ProcessSecondBoardShopClosed();
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
        if (entryState is not (EntryState.Combat or EntryState.AwaitRecoveryMainHud or
            EntryState.AwaitRecoveryContextMenu or EntryState.AwaitRecoveryHp) ||
            GetReadyAddon("XBMResult") == null)
        {
            return false;
        }

        var failedState = entryState;
        var recoveryWasActive = recovery.Active;
        if (recoveryWasActive)
        {
            CancelRecovery("mid-stage XBMResult");
        }

        AdvanceEntry(EntryState.AwaitXbmResult);
        Log.Warning($"[Failure] XBMResult detected mid-stage state={failedState}; starting result exit flow.");
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

            Callback.Fire(addon, true, 6, 0, new AtkValue { Type = AtkValueType.Undefined });
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

        Callback.Fire(
            contextMenu,
            true,
            0,
            0,
            (uint)0,
            new AtkValue { Type = AtkValueType.Undefined },
            new AtkValue { Type = AtkValueType.Undefined });
        potionDebugState = PotionDebugState.Idle;
        Log.Information("[Debug] potion-use selected ContextMenu use callback=0,0,0,undefined,undefined.");
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

        TargetSystem.Instance()->InteractWithObject(lauda.Struct(), false);
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

            return string.Equals(resident.Singular.ToString(), "Lauda", StringComparison.OrdinalIgnoreCase) &&
                (resident.Title.ToString().Contains("Crucible", StringComparison.OrdinalIgnoreCase) ||
                 resident.Title.ToString().Contains("Unbroken", StringComparison.OrdinalIgnoreCase));
        });
    }

    private void ProcessSelectIconString()
    {
        var addon = GetReadyAddon("SelectIconString");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, 1);
        AdvanceEntry(EntryState.AwaitSelectString);
    }

    private void ProcessSelectString()
    {
        var addon = GetReadyAddon("SelectString");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, 0);
        AdvanceEntry(EntryState.AwaitStageList);
    }

    private void ProcessStageList()
    {
        var addon = GetReadyAddon("XBMStageList");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, 1, selectedBoard);
        if (selectedBoard == 2)
        {
            if (!SetBoard2Route(Board2RoutePhase.Stage1, "Board 2 stage list selected"))
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

        var addon = GetReadyAddon("XBMStageDetailList");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, 8);
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

        var prompt = addon->AtkValues[0].GetValueAsString();
        if (!prompt.Contains("Proceed to challenge this board?", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Callback.Fire(addon, true, 0);
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

        if (GetReadyAddon("XBMPetParty") != null)
        {
            StopMovement();
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
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
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
            return;
        }

        if (TryMoveToDestination(destination, "stage start", "vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
        {
            boardMovementReady = true;
            petParty.StageToggleIndex = 0;
            AdvanceEntry(EntryState.AwaitStagePetParty);
        }
    }

    private void ProcessPreChallengeRosterAvailability()
    {
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
            .Where(number => number <= 25)
            .Concat(toRemove.Where(number => number > 25))
            .Concat(toAdd.Where(number => number <= 25))
            .Concat(toAdd.Where(number => number > 25)));
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

        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        var records = ReadPetPartyRecords(addon);
        if (records.Count == 0)
        {
            Log.Debug("[Entry] in-board familiar roster is not populated yet; waiting before stage selection.");
            return;
        }

        if (records.Any(record => record.MaximumHp == 0))
        {
            Log.Debug("[Entry] in-board familiar HP data is not populated yet; waiting before stage selection.");
            return;
        }

        var configured = ConfiguredStageFamiliarNumbers.Distinct().ToList();
        if (configured.Count != 3)
        {
            FailEntry($"stage-familiar-roster-requires-3:{configured.Count}");
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
        var addon = GetReadyAddon("XBMPetParty");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, 5);
        petParty.NotebookPage = -1;
        petParty.NotebookPhase = 0;
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
            Callback.Fire(addon, true, 3, (uint)0);
            petParty.NotebookPage = 0;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
            Log.Information("[Entry] notebook switched to page 1.");
            return;
        }

        var available = ReadNotebookFamiliarNumbers(addon);
        var phaseNumbers = petParty.NotebookPhase switch
        {
            0 => petParty.FamiliarRemovalNumbers.Where(number => number <= 25),
            1 => petParty.FamiliarRemovalNumbers.Where(number => number > 25 && number <= 50),
            2 => petParty.FamiliarAdditionNumbers.Where(number => number <= 25),
            _ => petParty.FamiliarAdditionNumbers.Where(number => number > 25 && number <= 50),
        };
        Log.Information($"[Entry] notebook page={petParty.NotebookPage + 1} phase={petParty.NotebookPhase} " +
            $"atkValuesCount={addon->AtkValuesCount} availableCount={available.Count} " +
            $"requested={string.Join(",", petParty.PendingFamiliarNumbers)} " +
            $"availableNumbers={string.Join(",", available.Select(entry => entry.Number))}.");

        var resolvedIndices = new List<(int Number, int UiIndex)>();
        foreach (var number in phaseNumbers)
        {
            var uiIndex = FindNotebookFamiliarIndex(addon, number);
            if (uiIndex < 0)
            {
                Log.Warning($"[Entry] familiar-not-found number={number} name=\"{FamiliarName(number)}\" " +
                    $"available={string.Join(",", available.Select(entry => $"{entry.Number}:{entry.PetId}"))}.");
                FailEntry($"familiar-not-found:{number}");
                return;
            }

            resolvedIndices.Add((number, uiIndex));
        }

        foreach (var (number, uiIndex) in resolvedIndices)
        {
            Callback.Fire(addon, true, 7, (uint)uiIndex);
            Log.Information($"[Entry] familiar No. {number} selected from notebook uiIndex={uiIndex}.");
        }

        if (petParty.NotebookPhase < 3)
        {
            var nextPage = petParty.NotebookPhase is 0 or 2 ? 1u : 0u;
            Callback.Fire(addon, true, 3, nextPage);
            petParty.NotebookPage = (int)nextPage;
            petParty.NotebookPhase++;
            nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
            Log.Information($"[Entry] notebook phase complete; switched to page {petParty.NotebookPage + 1}.");
            return;
        }

        Callback.Fire(addon, true, 0);
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

    private static List<PetPartyRecord> ReadPetPartyRecords(AtkUnitBase* addon)
    {
        var result = new List<PetPartyRecord>();
        var count = Math.Clamp(ReadAtkInt(addon, 5), 0, 12);
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

    private static HashSet<int> ReadCurrentFamiliarNumbers(AtkUnitBase* addon)
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
            Log.Warning($"[Entry] stage pet rejected number={familiarNumber} " +
                $"name=\"{EscapeLog(record?.Name ?? FamiliarName(familiarNumber))}\" threshold={threshold} {reason}.");

            var replacement = fallbackCandidates.FirstOrDefault(candidate => !used.Contains(candidate.FamiliarNumber));
            if (replacement == null)
            {
                Log.Error($"[Entry] stage pet replacement unavailable rejected={familiarNumber} " +
                    $"threshold={threshold} effective={string.Join(",", desired)}.");
                FailEntry($"stage-familiar-no-healthy-replacement:{familiarNumber}");
                return null;
            }

            desired.Add(replacement.FamiliarNumber);
            used.Add(replacement.FamiliarNumber);
            Log.Information($"[Entry] stage pet replacement rejected={familiarNumber} " +
                $"replacement={replacement.FamiliarNumber} name=\"{EscapeLog(replacement.Name)}\" " +
                $"hp={replacement.CurrentHp}/{replacement.MaximumHp} hpPercent={replacement.HealthPercent} " +
                $"threshold={threshold}.");
        }

        Log.Information($"[Entry] stage pet selection configured={string.Join(",", configured)} " +
            $"effective={string.Join(",", desired)} threshold={threshold}.");
        return desired;
    }

    private static int FindNotebookFamiliarIndex(AtkUnitBase* addon, int familiarNumber)
    {
        const int recordStart = 22;
        const int recordStride = 8;
        var recordCount = Math.Max(0, (addon->AtkValuesCount - recordStart) / recordStride);
        for (var index = 0; index < recordCount; index++)
        {
            var offset = recordStart + index * recordStride;
            if (ReadAtkUInt(addon, offset + 2) == familiarNumber)
            {
                return index;
            }
        }

        return -1;
    }

    private static List<(int Number, uint PetId)> ReadNotebookFamiliarNumbers(AtkUnitBase* addon)
    {
        const int recordStart = 22;
        const int recordStride = 8;
        var result = new List<(int Number, uint PetId)>();
        var recordCount = Math.Max(0, (addon->AtkValuesCount - recordStart) / recordStride);
        for (var index = 0; index < recordCount; index++)
        {
            var offset = recordStart + index * recordStride;
            var number = ReadAtkUInt(addon, offset + 2);
            var petId = ReadAtkUInt(addon, offset + 6);
            if (number is >= 1 and <= 50 && petId >= 242001 && petId <= 242050)
            {
                result.Add(((int)number, petId));
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
            FailEntry("stage-familiar-selection-not-prepared");
            return;
        }

        if (desired.Count != 3)
        {
            FailEntry($"stage-familiar-roster-requires-3:{desired.Count}");
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
                Log.Warning($"[Entry] stage familiar verification mismatch retry={petParty.StageSelectionRetryCount}/3 " +
                    $"current={string.Join(",", current.Order())} desired={string.Join(",", desired.Order())} " +
                    $"queued={string.Join(",", toToggle)}.");
                if (petParty.StageSelectionRetryCount >= 3)
                {
                    FailEntry($"stage-familiar-verification-failed:current={string.Join(",", current.Order())}:desired={string.Join(",", desired.Order())}");
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
            FailEntry($"stage-familiar-not-in-board-roster:{familiarNumber}");
            return;
        }

        Callback.Fire(addon, true, 1, (uint)rosterIndex);
        Log.Information($"[Entry] stage familiar toggle number={familiarNumber} rosterIndex={rosterIndex}.");
        petParty.StageToggleIndex++;
        AdvanceEntry(EntryState.SelectStagePetParty);
    }

    private static HashSet<int> ReadStageSelectedFamiliarNumbers(AtkUnitBase* addon)
    {
        var result = new HashSet<int>();
        var list = GetPetPartyList(addon);
        if (list == null)
        {
            return result;
        }

        var count = Math.Min(Math.Clamp(ReadAtkInt(addon, 5), 0, 12), Math.Clamp(list->GetItemCount(), 0, 50));
        for (var index = 0; index < count; index++)
        {
            var renderer = list->GetItemRenderer(index);
            var selectionNode = FindRendererNode(renderer, 2);
            if (renderer == null || selectionNode == null || (selectionNode->DrawFlags & 0x100) != 0)
            {
                continue;
            }

            var id = ReadPetPartyFamiliarId(addon, index);
            if (id is >= 242001 and <= 242050)
            {
                result.Add((int)(id - 242000));
            }
        }

        return result;
    }

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
        return node != null && node->NodeId == 11 ? node : null;
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

            if (node->NodeId == 2)
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

    private static int FindPetPartyRosterIndex(AtkUnitBase* addon, int familiarNumber)
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

        if (GetReadyAddon("XBMStageDetailList") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.CommenceInInstanceStage);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
    }

    private void ProcessInInstanceCommence()
    {
        var addon = GetReadyAddon("XBMStageDetailList");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, 8);
        AdvanceEntry(EntryState.AwaitStageLoaded);
        movementConditionsClearSince = null;
        Log.Information("[Entry] first-board stage commenced; waiting for stage load.");
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
        if (!CanUseAction(46751, knight.GameObjectId))
        {
            return;
        }

        var used = UseAction(46751, knight.GameObjectId);
        Log.Information($"[Combat] Bone Knight rear action action=46751 target={knight.GameObjectId:X} " +
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
        var target = GetCombatTarget();
        if (target == null)
        {
            return;
        }

        SetCombatTarget(target);
        try
        {
            if (!bossModSetActive.InvokeFunc("AutoDuty Passive"))
            {
                FailEntry("bossmod-preset-rejected");
                return;
            }

            Log.Information("[Combat] BossMod preset activated name=AutoDuty Passive.");
            rsrChangeMode.InvokeAction(3);
            Log.Information("[Combat] RotationSolverReborn mode activated mode=Manual.");
            AdvanceEntry(EntryState.Combat);
        }
        catch (Exception ex)
        {
            FailEntry($"bossmod-unavailable:{ex.Message}");
        }
    }

    private void ProcessCombat()
    {
        if (HasEntryTransitionCondition())
        {
            return;
        }

        var target = GetCombatTarget();
        if (target == null)
        {
            if (HasLivingCombatTarget())
            {
                // Encounter targets can remain alive but temporarily untargetable.
                return;
            }

            if (CurrentRouteNode?.DirectResultFlow == true)
            {
                AdvanceEntry(EntryState.AwaitXbmResult);
                Log.Information("[Combat] Stage 10 targets cleared; waiting for XBMResult.");
                return;
            }

            Log.Information("[Combat] stage targets cleared; beginning stage exit.");
            AdvanceEntry(EntryState.AwaitContentsBotty);
            return;
        }

        if (selectedBoard == 1 && target.BaseId == 19334 && !knightSetupComplete)
        {
            AdvanceEntry(EntryState.PrepareKnight);
            return;
        }

        SetCombatTarget(target);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(250);
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

        Callback.Fire(addon, true, 1);
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

        Callback.Fire(addon, true, 0);
        if (selectedBoard == 2 && CurrentRouteNode?.Kind == RouteNodeKind.Battle)
        {
            var completedNode = CurrentRouteNode;
            var nextNode = board2RouteNodes.GetValueOrDefault(completedNode.NextPhase);
            if (nextNode == null || !SetNextRouteNode($"{completedNode.Name} complete"))
            {
                return;
            }

            var player = ObjectTable.LocalPlayer as ICharacter;
            var hp = player == null ? "unavailable" : $"{player.CurrentHp}/{player.MaxHp}";
            if (nextNode.Kind == RouteNodeKind.Rest)
            {
                Log.Information($"[Recovery] phase=post-stage route={completedNode.Key} hp={hp} decision=defer-to-{nextNode.Key}-rest.");
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
            Log.Information("[Entry] post-stage results confirmed; waiting for contents exit.");
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

    private bool TryBeginRecovery(EntryState continuation, int? thresholdOverride = null, int? targetOverride = null)
    {
        if (selectedBoard == 2 && board2Route != Board2RoutePhase.None &&
            recoveryNoUsableItemsRoute == board2Route &&
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
                Log.Information($"[Recovery] phase={(isCombatCheck ? "combat" : "post-stage")} decision=player-unavailable.");
            }
            return false;
        }

        var hpPercent = (int)((long)player.CurrentHp * 100 / player.MaxHp);
        var thresholdPercent = thresholdOverride ?? 60;
        var targetPercent = targetOverride ?? 90;
        var next = thresholdPercent == 10 ? "Stage 4B rest" : "next route";
        if (hpPercent > thresholdPercent)
        {
            if (logCheck)
            {
                Log.Information($"[Recovery] phase={(isCombatCheck ? "combat" : "post-stage")} hp={player.CurrentHp}/{player.MaxHp} " +
                    $"percent={hpPercent} threshold={thresholdPercent} next=\"{next}\" decision=skip-above-threshold.");
            }
            return false;
        }

        if (logCheck)
        {
            Log.Information($"[Recovery] phase={(isCombatCheck ? "combat" : "post-stage")} hp={player.CurrentHp}/{player.MaxHp} " +
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
            if (selectedBoard == 2)
            {
                recoveryNoUsableItemsRoute = board2Route;
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
        var player = ObjectTable.LocalPlayer as ICharacter;
        recovery.StartingHp = player?.CurrentHp ?? 0;
        recovery.AttemptStarted = DateTime.UtcNow;
        Callback.Fire(addon, true, 6, (uint)slot, new AtkValue { Type = AtkValueType.Undefined });
        AdvanceEntry(EntryState.AwaitRecoveryContextMenu, 0);
        Log.Information($"[Recovery] attempt={recovery.Attempt} content-menu requested slot={slot} itemId={itemId} " +
            $"name=\"{EscapeLog(itemName)}\" healPercent={healPercent} hudUsable={usable} " +
            $"decision=attempt-anyway startingHp={recovery.StartingHp}.");
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
                recovery.FailedSlots.Add(recovery.SelectedSlot);
                Log.Information($"[Recovery] attempt={recovery.Attempt} context menu timeout; " +
                    $"slot={recovery.SelectedSlot} marked-failed; retrying.");
                AdvanceEntry(EntryState.AwaitRecoveryMainHud, 0);
            }
            return;
        }

        Log.Information($"[Recovery] context-menu available=true atkValuesCount={addon->AtkValuesCount}.");

        Callback.Fire(
            addon,
            true,
            0,
            0,
            (uint)0,
            new AtkValue { Type = AtkValueType.Undefined },
            new AtkValue { Type = AtkValueType.Undefined });
        recovery.HpPollStarted = DateTime.UtcNow;
        AdvanceEntry(EntryState.AwaitRecoveryHp, 0);
        Log.Information($"[Recovery] attempt={recovery.Attempt} context-menu use callback dispatched; polling HP.");
    }

    private void ProcessRecoveryHp()
    {
        var player = ObjectTable.LocalPlayer as ICharacter;
        if (player != null && player.CurrentHp > recovery.StartingHp)
        {
            Log.Information($"[Recovery] attempt={recovery.Attempt} HP increased from {recovery.StartingHp} to {player.CurrentHp}; success.");
            CompleteRecovery(recovery.Continuation, "HP increased");
            return;
        }

        if (DateTime.UtcNow - recovery.HpPollStarted < TimeSpan.FromSeconds(2))
        {
            return;
        }

        recovery.FailedSlots.Add(recovery.SelectedSlot);
        Log.Information($"[Recovery] attempt={recovery.Attempt} HP unchanged at {player?.CurrentHp ?? 0}; " +
            $"slot={recovery.SelectedSlot} marked-failed; retrying.");
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
            .Where(candidate => !recovery.FailedSlots.Contains(candidate.Slot))
            .ToList();
        if (availableCandidates.Count == 0 && candidates.Count > 0)
        {
            recovery.FailedSlots.Clear();
            availableCandidates = candidates;
            Log.Information("[Recovery] all known recovery slots have failed this session; restarting candidate cycle.");
        }

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
        if (TryMoveToDestination(destination, "Stage 4B rest", "stage4b-rest-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
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

        Callback.Fire(addon, true, 3);
        AdvanceEntry(EntryState.AwaitStage4BConfirmation);
        Log.Information("[Entry] Stage 4B rest dispatched callback=3; waiting for confirmation.");
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

        Callback.Fire(addon, true, 0);
        if (!SetNextRouteNode("Stage 4B rest confirmed"))
        {
            return;
        }
        boardMovementTransitionObserved = false;
        boardMovementClearSince = null;
        boardMovementReady = false;
        vnavMoveStarted = false;
        movementConditionsClearSince = null;
        AdvanceEntry(EntryState.AwaitBoardExitTransitionStart);
        Log.Information("[Entry] Stage 4B rest confirmed; waiting for transition to Stage 5.");
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
        if (TryMoveToDestination(destination, "Stage 6 treasure", "stage6-treasure-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
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
        if (TryMoveToDestination(destination, "Stage 10", "stage10-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
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

        Callback.Fire(addon, true, 2, optionIndex);
        treasure.UsedOptionIndexes.Add(optionIndex);
        treasure.SelectedOptionIndex = optionIndex;
        treasure.SelectedItemId = selected?.ItemId ?? 0;
        treasure.Attempt = 1;
        treasure.OptionRequestedAt = DateTime.UtcNow;
        AdvanceEntry(confirmationState);
        Log.Information($"[Entry] {TreasureRouteName()} selection={treasure.CompletedSelections + 1}/{treasure.RequiredSelections} " +
            $"option={optionIndex} itemId={treasure.SelectedItemId} name=\"{EscapeLog(selected?.Title ?? "unknown")}\" " +
            $"reason={(selected != null && RecoveryItems.ContainsKey(selected.ItemId) ? "healing-priority" : "default-fallback")}; " +
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
            Callback.Fire(selectYesno, true, 0);
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
            Callback.Fire(treasureAddon, true, 2, 1);
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
        => CurrentRouteNode?.Kind == RouteNodeKind.Treasure
            ? CurrentRouteNode.Name
            : "Unexpected treasure";

    private void CompleteTreasure()
    {
        var route = board2Route;
        var currentNode = CurrentRouteNode;
        var nextNode = currentNode == null ? null : board2RouteNodes.GetValueOrDefault(currentNode.NextPhase);
        treasure.Clear();
        ResetMovement();
        if (currentNode?.Kind != RouteNodeKind.Treasure || nextNode == null || !SetNextRouteNode($"{currentNode.Name} complete"))
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
        if (TryMoveToDestination(destination, "Stage 8 treasure", "stage8-treasure-vnav-unavailable", StageTransitionArrivalRadius) == MovementResult.Arrived)
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

        boardMovementTransitionObserved = false;
        boardMovementClearSince = null;
        boardMovementReady = false;
        vnavMoveStarted = false;
        var node = CurrentRouteNode;
        switch (node?.Kind)
        {
            case RouteNodeKind.Battle when node.DirectResultFlow:
                AdvanceEntry(EntryState.AwaitStageLoaded);
                Log.Information($"[Entry] transition ready; waiting for {node.Name} map to settle.");
                break;
            case RouteNodeKind.Battle:
                AdvanceEntry(EntryState.MoveToStageStart);
                Log.Information($"[Entry] transition ready; moving to {node.Name}.");
                break;
            case RouteNodeKind.Treasure when node.Phase == Board2RoutePhase.Treasure6:
                AdvanceEntry(EntryState.MoveToStage6Start);
                Log.Information($"[Entry] transition ready; moving to {node.Name} spot.");
                break;
            case RouteNodeKind.Treasure when node.Phase == Board2RoutePhase.Treasure8:
                AdvanceEntry(EntryState.MoveToStage8Start);
                Log.Information($"[Entry] transition ready; moving to {node.Name} spot.");
                break;
            case RouteNodeKind.Shop:
                AdvanceEntry(EntryState.MoveToSecondBoardShop);
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

        if (ClientTerritoryId != 1340)
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

        var addon = GetReadyAddon("XBMContentsItemShop");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, 0);
        vnavMoveStarted = false;
        AdvanceEntry(EntryState.AwaitSecondBoardExitConfirmation);
        Log.Information("[Entry] Second Board item shop closed; waiting for exit confirmation.");
    }

    private void ProcessSecondBoardShopClosed()
    {
        if (!RequireRouteNodeKind(RouteNodeKind.Shop))
        {
            return;
        }

        if (GetReadyAddon("XBMContentsItemShop") != null)
        {
            return;
        }

        AdvanceEntry(EntryState.AwaitInstanceLeave);
        Log.Information("[Entry] Second Board item shop closed; requesting board instance exit.");
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
        var nextNode = currentNode == null ? null : board2RouteNodes.GetValueOrDefault(currentNode.NextPhase);
        if (currentNode?.Kind != RouteNodeKind.Shop || nextNode?.Kind != RouteNodeKind.Battle)
        {
            FailEntry($"shop-next-node-invalid:{currentNode?.Key ?? "unknown"}");
            return;
        }

        Callback.Fire(addon, true, 0);
        if (nextNode.DirectResultFlow)
        {
            if (!SetNextRouteNode($"{currentNode.Name} confirmed"))
            {
                return;
            }
            boardMovementTransitionObserved = false;
            boardMovementClearSince = null;
            boardMovementReady = false;
            vnavMoveStarted = false;
            movementConditionsClearSince = null;
            AdvanceEntry(EntryState.MoveToStage10Start);
            Log.Information($"[Entry] {currentNode.Name} confirmed; moving to {nextNode.Name} start.");
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
        if (TryBeginRecovery(EntryState.MoveToStageStart, 60, 90))
        {
            Log.Information($"[Entry] {currentNode.Name} confirmed; checking recovery items before {nextNode.Name}.");
            return;
        }

        AdvanceEntry(EntryState.MoveToStageStart);
        Log.Information($"[Entry] {currentNode.Name} confirmed; moving to {nextNode.Name} start.");
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
        Callback.Fire(addon, true, 3);
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

        Callback.Fire(addon, true, 0);
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

        Callback.Fire(addon, true, 0);
        AdvanceEntry(EntryState.AwaitNeedGreed);
        Log.Information("[Entry] XBMResult callback 0 dispatched; waiting for NeedGreed.");
    }

    private void ProcessNeedGreedAvailability()
    {
        if (GetReadyAddon("NeedGreed") == null)
        {
            return;
        }

        AdvanceEntry(EntryState.RollNeedGreed);
    }

    private void ProcessNeedGreedRoll()
    {
        var addon = GetReadyAddon("NeedGreed");
        if (addon == null)
        {
            return;
        }

        Callback.Fire(addon, true, -2);
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

        Callback.Fire(addon, true, -1);
        AdvanceEntry(EntryState.AwaitCentralShroud);
        nextEntryAction = DateTime.UtcNow.AddMilliseconds(500);
        Log.Information("[Entry] XBMResult exit callbacks dispatched; waiting for Central Shroud.");
    }

    private void ProcessCentralShroudReturn()
    {
        if (ClientTerritoryId == 148)
        {
            if (loopEntry)
            {
                Log.Information($"[Entry] board={selectedBoard} instance fully exited; restarting automation loop.");
                EndEntry(EntryState.Idle, "loop restart");
                StartBoardEntry(selectedBoard, true);
            }
            else
            {
                EndEntry(EntryState.Complete, "board instance fully exited");
                Log.Information("[Entry] board instance fully exited territory=148.");
            }
        }
    }

    private Dalamud.Game.ClientState.Objects.Types.IGameObject? GetCombatTarget()
    {
        var preferredTarget = GetPreferredCombatTarget();
        return preferredTarget ?? (HasLivingCombatTarget() ? GetHostileCombatFallbackTarget() : null);
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
            .FirstOrDefault();

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

    private bool IsCombatTargetBaseId(uint baseId)
        => CurrentRouteNode?.Kind == RouteNodeKind.Battle &&
            CurrentRouteNode.CombatTargetBaseIds.Contains(baseId);

    private bool IsBattleNpcPet(Dalamud.Game.ClientState.Objects.Types.IGameObject gameObject)
        => gameObject is IBattleNpc battleNpc &&
            string.Equals(battleNpc.BattleNpcKind.ToString(), "Pet", StringComparison.OrdinalIgnoreCase);

    private int GetCombatTargetPriority(uint baseId)
        => CurrentRouteNode?.PriorityTargetBaseId == baseId ? 0 : 1;

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
        => ActionManager.Instance()->GetActionStatus(ActionType.Action, actionId, targetObjectId, true, true) == 0;

    private static bool UseAction(uint actionId, ulong targetObjectId)
        => ActionManager.Instance()->UseAction(ActionType.Action, actionId, targetObjectId);

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
        var status = ActionManager.Instance()->GetActionStatus(actionType, actionId, targetObjectId, true, true);
        var dispatched = status == 0 && ActionManager.Instance()->UseAction(actionType, actionId, targetObjectId);
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

    private bool SetNextRouteNode(string reason)
    {
        var current = CurrentRouteNode;
        if (current == null || current.NextPhase == Board2RoutePhase.None)
        {
            FailEntry($"route-next-node-missing:current={current?.Key ?? "unknown"}");
            return false;
        }

        return SetBoard2Route(current.NextPhase, reason);
    }

    private bool RequireRouteNodeKind(RouteNodeKind expectedKind)
    {
        var node = CurrentRouteNode;
        if (node?.Kind == expectedKind)
        {
            return true;
        }

        FailEntry($"route-node-kind-mismatch:expected={expectedKind}:actual={node?.Key ?? "unknown"}/" +
            $"{node?.Kind.ToString() ?? "none"}");
        return false;
    }

    private bool SetBoard2Route(Board2RoutePhase next, string reason)
    {
        if (board2Route == next)
        {
            return true;
        }

        if (!board2RouteNodes.TryGetValue(next, out var nextNode))
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
        board2Route = next;
        recoveryNoUsableItemsRoute = Board2RoutePhase.None;
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
        => OpenDebugWindow();

    private void OpenDebugWindow()
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
                    $"reason={(knownRecovery ? "healing-priority" : "default-fallback")}");
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

    private TreasureCardInfo? SelectTreasureCard(
        List<TreasureCardInfo> cards,
        HashSet<int> usedOptionIndexes)
        => cards
            .Where(card => card.OptionIndex >= 0 && !card.Acquired && !usedOptionIndexes.Contains(card.OptionIndex) &&
                RecoveryItems.ContainsKey(card.ItemId))
            .OrderByDescending(card => RecoveryItems[card.ItemId].HealPercent)
            .ThenByDescending(card => RecoveryItems[card.ItemId].IsAsh)
            .ThenBy(card => card.OptionIndex)
            .FirstOrDefault()
            ?? cards.FirstOrDefault(card => card.OptionIndex == 0 && !card.Acquired &&
                !usedOptionIndexes.Contains(card.OptionIndex))
            ?? cards.FirstOrDefault(card => card.OptionIndex == 1 && !card.Acquired &&
                !usedOptionIndexes.Contains(card.OptionIndex))
            ?? cards.FirstOrDefault(card => card.OptionIndex >= 0 && !card.Acquired &&
                !usedOptionIndexes.Contains(card.OptionIndex));

    private void LogTreasureChoices(List<TreasureCardInfo> cards)
    {
        foreach (var card in cards)
        {
            var knownRecovery = RecoveryItems.TryGetValue(card.ItemId, out var recoveryItem);
            Log.Information($"[Entry] {TreasureRouteName()} possible option={card.OptionIndex} rowId={card.RowId} " +
                $"itemId={card.ItemId} acquired={card.Acquired} healing={knownRecovery} " +
                $"healPercent={(knownRecovery ? recoveryItem.HealPercent : 0)} ash={(knownRecovery && recoveryItem.IsAsh)} " +
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

    private static int ReadAtkInt(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) ? addon->AtkValues[index].Int : 0;

    private static uint ReadAtkUInt(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) ? addon->AtkValues[index].UInt : 0;

    private static bool ReadAtkBool(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) && addon->AtkValues[index].Bool;

    private static string ReadAtkString(FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase* addon, int index)
        => IsAtkIndexValid(addon, index) ? addon->AtkValues[index].GetValueAsString() : string.Empty;

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
    public int PetHealthThresholdPercent { get; set; } = 100;
    public List<int> Board1FamiliarNumbers { get; set; } = new();
    public List<int> Board2FamiliarNumbers { get; set; } = new();
    public List<int> Board1StageFamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage1FamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage3BFamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage5FamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage7FamiliarNumbers { get; set; } = new();
    public List<int> Board2Stage10FamiliarNumbers { get; set; } = new();

    public void Normalize()
    {
        PetHealthThresholdPercent = Math.Clamp(PetHealthThresholdPercent, 1, 100);
        Board1FamiliarNumbers = Board1FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(10).ToList();
        Board2FamiliarNumbers = Board2FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(12).ToList();
        Board1StageFamiliarNumbers = Board1StageFamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(3).ToList();
        Board2Stage1FamiliarNumbers = Board2Stage1FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(3).ToList();
        Board2Stage3BFamiliarNumbers = Board2Stage3BFamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(3).ToList();
        Board2Stage5FamiliarNumbers = Board2Stage5FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(3).ToList();
        Board2Stage7FamiliarNumbers = Board2Stage7FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(3).ToList();
        Board2Stage10FamiliarNumbers = Board2Stage10FamiliarNumbers.Where(number => number is >= 1 and <= 50).Distinct().Take(3).ToList();
    }
}
