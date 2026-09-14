using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace UnbrokenHelper.Windows;

public sealed class DebugWindow : Window, IDisposable
{
    private enum DebugSection
    {
        Overview,
        Territory,
        Player,
            Objects,
            Addons,
        Actions,
        FamiliarRosters,
        XBMPetSheet,
        XBMItemSheet,
        Entry,
            Services,
    }

    private readonly Plugin plugin;
    private DebugSection selectedSection = DebugSection.Overview;
    private int xbmpetRow = 1;
    private float lgbCollisionRadius = 100f;

    public DebugWindow(Plugin plugin)
        : base("Unbroken Helper Debug###UnbrokenHelperDebug")
    {
        this.plugin = plugin;
        Size = new Vector2(760, 500);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(540, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        var contentSize = ImGui.GetContentRegionAvail();
        var navigationWidth = Math.Clamp(contentSize.X * 0.28f, 180f, 240f);
        var childHeight = MathF.Max(1f, contentSize.Y);
        var contentWidth = MathF.Max(1f, contentSize.X - navigationWidth - ImGui.GetStyle().ItemSpacing.X);

        if (ImGui.BeginChild("##debug-sections", new Vector2(navigationWidth, childHeight), true))
        {
            DrawSectionList();
            ImGui.EndChild();
        }

        ImGui.SameLine();

        if (ImGui.BeginChild("##debug-content", new Vector2(contentWidth, childHeight), true))
        {
            DrawSectionContent();
            ImGui.EndChild();
        }
    }

    private void DrawSectionList()
    {
        ImGui.TextUnformatted("Sections");
        ImGui.Separator();

        DrawSectionButton(DebugSection.Overview, "Overview");
        DrawSectionButton(DebugSection.Territory, "Territory");
        DrawSectionButton(DebugSection.Player, "Player");
        DrawSectionButton(DebugSection.Objects, "Objects");
        DrawSectionButton(DebugSection.Addons, "Addons");
        DrawSectionButton(DebugSection.Actions, "Actions");
        DrawSectionButton(DebugSection.FamiliarRosters, "Familiar Rosters");
        DrawSectionButton(DebugSection.XBMPetSheet, "XBMPet Sheet");
        DrawSectionButton(DebugSection.XBMItemSheet, "XBMItem Sheet");
        DrawSectionButton(DebugSection.Entry, "Entry Automation");
        DrawSectionButton(DebugSection.Services, "Services");
    }

    private void DrawSectionButton(DebugSection section, string label)
    {
        var selected = selectedSection == section;
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, ImGui.GetStyle().Colors[(int)ImGuiCol.Header]);
        }

        if (ImGui.Button($"{label}##debug-section-{section}", new Vector2(-1f, 0f)))
        {
            selectedSection = section;
        }

        if (selected)
        {
            ImGui.PopStyleColor();
        }
    }

    private void DrawSectionContent()
    {
        switch (selectedSection)
        {
            case DebugSection.Overview:
                DrawOverview();
                break;
            case DebugSection.Territory:
                DrawTerritory();
                break;
            case DebugSection.Player:
                DrawPlayer();
                break;
            case DebugSection.Objects:
                DrawObjects();
                break;
            case DebugSection.Addons:
                DrawAddons();
                break;
            case DebugSection.Actions:
                DrawActions();
                break;
            case DebugSection.FamiliarRosters:
                DrawFamiliarRosters();
                break;
            case DebugSection.XBMPetSheet:
                DrawXBMPetSheet();
                break;
            case DebugSection.XBMItemSheet:
                DrawXBMItemSheet();
                break;
            case DebugSection.Entry:
                DrawEntry();
                break;
            case DebugSection.Services:
                DrawServices();
                break;
        }
    }

    private void DrawFamiliarRosters()
    {
        ImGui.TextUnformatted("Configured Familiar Rosters");
        ImGui.TextWrapped("Missing familiars are added from the Monster Notebook when automation starts. Existing familiars are preserved.");
        var petHealthThreshold = plugin.PetHealthThresholdPercent;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Minimum Pet HP %", ref petHealthThreshold))
        {
            plugin.SetPetHealthThresholdPercent(petHealthThreshold);
        }
        ImGui.TextWrapped("Stage selection replaces configured familiars below this HP percentage with the next eligible roster pet.");
        ImGui.Separator();
        DrawFamiliarRoster(1);
        ImGui.Separator();
        DrawFamiliarRoster(2);
        ImGui.Separator();
        if (ImGui.CollapsingHeader("Board 1 Battle Stages", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawStageRoster(1, 1, "Board 1 Stage 1");
        }

        if (ImGui.CollapsingHeader("Board 2 Battle Stages", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawStageRoster(2, 1, "Board 2 Stage 1 - Manticore");
            DrawStageRoster(2, 3, "Board 2 Stage 3B - Wyvern");
            DrawStageRoster(2, 5, "Board 2 Stage 5 - Voidmancer");
            DrawStageRoster(2, 7, "Board 2 Stage 7 - Tablitaur");
            DrawStageRoster(2, 10, "Board 2 Stage 10 - Loosefrox / Chewchum");
        }
    }

    private void DrawXBMPetSheet()
    {
        ImGui.TextUnformatted("XBMPet Sheet Dump");
        ImGui.TextWrapped("Dump every column from one XBMPet row to the Dalamud log. This is read-only.");
        ImGui.Separator();

        ImGui.SetNextItemWidth(120f);
        ImGui.InputInt("Row ID", ref xbmpetRow);
        ImGui.SameLine();
        if (ImGui.Button("Dump Row"))
        {
            plugin.LogXBMPetRow(xbmpetRow);
        }
    }

    private void DrawXBMItemSheet()
    {
        ImGui.TextUnformatted("XBMItem Sheet Dump");
        ImGui.TextWrapped("Dump all non-empty columns for every XBMItem row to the Dalamud log. This is read-only.");
        ImGui.Separator();

        if (ImGui.Button("Dump All Items"))
        {
            plugin.LogXBMItems();
        }
    }

    private void DrawFamiliarRoster(int board)
    {
        var roster = plugin.GetFamiliarRoster(board);
        ImGui.TextUnformatted($"Board {board} ({roster.Count}/{plugin.FamiliarRosterCapacity(board)})");
        if (ImGui.BeginCombo($"Add familiar##board-{board}", "Select familiar"))
        {
            for (var number = 1; number <= 50; number++)
            {
                if (roster.Contains(number))
                {
                    continue;
                }

                var label = $"No. {number} - {plugin.FamiliarName(number)}";
                if (ImGui.Selectable(label))
                {
                    plugin.AddFamiliarToRoster(board, number);
                }
            }

            ImGui.EndCombo();
        }

        foreach (var number in roster.ToArray())
        {
            ImGui.PushID($"board-{board}-familiar-{number}");
            ImGui.TextUnformatted($"No. {number} - {plugin.FamiliarName(number)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                plugin.RemoveFamiliarFromRoster(board, number);
            }

            ImGui.PopID();
        }
    }

    private void DrawStageRoster(int board, int stage, string label)
    {
        var boardRoster = plugin.GetFamiliarRoster(board);
        var stageRoster = plugin.GetStageFamiliarRoster(board, stage);
        ImGui.TextUnformatted($"{label} ({stageRoster.Count}/3)");
        if (ImGui.BeginCombo($"Add familiar##stage-{board}-{stage}", "Select familiar"))
        {
            foreach (var number in boardRoster)
            {
                if (stageRoster.Contains(number))
                {
                    continue;
                }

                if (ImGui.Selectable($"No. {number} - {plugin.FamiliarName(number)}"))
                {
                    plugin.AddStageFamiliar(board, stage, number);
                }
            }

            ImGui.EndCombo();
        }

        foreach (var number in stageRoster.ToArray())
        {
            ImGui.PushID($"stage-{board}-{stage}-familiar-{number}");
            ImGui.TextUnformatted($"No. {number} - {plugin.FamiliarName(number)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                plugin.RemoveStageFamiliar(board, stage, number);
            }

            ImGui.PopID();
        }
    }

    private void DrawOverview()
    {
        ImGui.TextUnformatted("Overview");
        ImGui.TextDisabled("Unbroken Helper debug skeleton");
        ImGui.Separator();
        ImGui.TextWrapped("This window is the starting point for Crucible of the Unbroken discovery and automation.");
        ImGui.TextWrapped("Current actions only inspect game state or write diagnostic information to the Dalamud log.");

        ImGui.Separator();
        ImGui.TextUnformatted($"Territory: {plugin.ClientTerritoryId}");
        ImGui.TextUnformatted($"Player loaded: {plugin.IsPlayerLoaded}");
        ImGui.TextUnformatted($"Objects available: {plugin.ObjectCount}");

        if (ImGui.Button("Log Overview"))
        {
            plugin.LogOverview();
        }
    }

    private void DrawTerritory()
    {
        ImGui.TextUnformatted("Territory");
        ImGui.TextWrapped("Inspect the active territory before adding Crucible-specific territory detection.");
        ImGui.Separator();
        ImGui.TextUnformatted($"Territory type ID: {plugin.ClientTerritoryId}");
        ImGui.TextUnformatted($"Territory loaded: {plugin.IsTerritoryLoaded}");

        if (ImGui.Button("Dump Territory Data"))
        {
            plugin.LogTerritoryData();
        }
    }

    private void DrawPlayer()
    {
        ImGui.TextUnformatted("Player");
        ImGui.Separator();
        ImGui.TextUnformatted($"Loaded: {plugin.IsPlayerLoaded}");
        ImGui.TextUnformatted($"Job row ID: {plugin.PlayerJobId}");
        ImGui.TextUnformatted($"Position: {FormatVector(plugin.PlayerPosition)}");
        ImGui.TextUnformatted($"Target: {plugin.TargetName}");

        if (ImGui.Button("Dump Player Data"))
        {
            plugin.LogPlayerData();
        }
    }

    private void DrawObjects()
    {
        ImGui.TextUnformatted("Objects");
        ImGui.TextWrapped("Object inspection will become the foundation for familiar, enemy, and encounter discovery.");
        ImGui.Separator();
        ImGui.TextUnformatted($"Object count: {plugin.ObjectCount}");

        if (ImGui.Button("Scan Objects"))
        {
            plugin.LogObjectSummary();
        }

        ImGui.SameLine();
        if (ImGui.Button("Dump Nearby Battle NPCs"))
        {
            plugin.LogNearbyBattleNpcs();
        }

        if (ImGui.Button("Dump Targeted NPC"))
        {
            plugin.LogTargetedNpc();
        }

        if (ImGui.Button("Dump Pet Info"))
        {
            plugin.LogPetInfo();
        }
    }

    private void DrawServices()
    {
        ImGui.TextUnformatted("Services");
        ImGui.TextWrapped("Dependency and client-state checks for the initial plugin skeleton.");
        ImGui.Separator();
        ImGui.TextUnformatted("Dalamud plugin services are available.");

        if (ImGui.Button("Log Service Availability"))
        {
            plugin.LogServiceAvailability();
        }
    }

    private void DrawEntry()
    {
        ImGui.TextUnformatted("First Board Entry");
        ImGui.TextWrapped("Targets Lauda, enters the Crucible, selects the first board, challenges it, and moves to the recorded starting coordinate with VNav.");
        ImGui.Separator();
        ImGui.TextUnformatted($"State: {plugin.EntryStateText}");
        ImGui.TextUnformatted($"Waiting for: {plugin.EntryTarget}");
        ImGui.TextUnformatted($"Territory: {plugin.ClientTerritoryId}");

        if (!plugin.EntryActive && ImGui.Button("Start First Board Entry"))
        {
            plugin.StartFirstBoardEntry();
        }

        if (!plugin.EntryActive)
        {
            ImGui.SameLine();
            if (ImGui.Button("Start Second Board Entry"))
            {
                plugin.StartSecondBoardEntry();
            }
        }

        if (plugin.EntryActive)
        {
            ImGui.SameLine();
            if (ImGui.Button("Stop Entry"))
            {
                plugin.StopEntry();
            }
        }
    }

    private void DrawActions()
    {
        ImGui.TextUnformatted("Actions");
        ImGui.TextWrapped("Test a self-targeted action dispatch, suitable for Crucible-managed item actions.");
        ImGui.Separator();

        var actionId = plugin.DebugActionId;
        if (ImGui.InputInt("Action ID", ref actionId))
        {
            plugin.DebugActionId = actionId;
        }

        var actionTypeIndex = plugin.DebugActionTypeIndex;
        if (ImGui.Combo("Action Type", ref actionTypeIndex, "Action\0Item\0EventItem\0"))
        {
            plugin.DebugActionTypeIndex = actionTypeIndex;
        }

        ImGui.TextUnformatted($"Self target object ID: {plugin.PlayerObjectId:X}");

        if (ImGui.Button("Use Action"))
        {
            plugin.UseDebugAction();
        }

        ImGui.SameLine();
        if (ImGui.Button("Use G3 Beast Potion"))
        {
            plugin.UseDebugPotion();
        }
    }

    private void DrawAddons()
    {
        ImGui.TextUnformatted("Addons");
        ImGui.TextWrapped("Dump loaded game UI addons and their AtkValue slots to the Dalamud log. These actions do not fire callbacks or modify UI state.");
        ImGui.Separator();

        if (ImGui.Button("Dump Loaded Addons"))
        {
            plugin.LogLoadedAddons(visibleOnly: false);
        }

        ImGui.SameLine();
        if (ImGui.Button("Dump Visible Addons"))
        {
            plugin.LogLoadedAddons(visibleOnly: true);
        }

        if (ImGui.Button("Dump Focused Addons"))
        {
            plugin.LogFocusedAddons();
        }

        if (ImGui.Button("Dump XBMPetParty Roster"))
        {
            plugin.LogPetPartyRoster();
        }

        if (ImGui.Button("Dump XBMPetParty Node 11"))
        {
            plugin.LogPetPartyNode11();
        }

        if (ImGui.Button("Dump XBMStageDetailList"))
        {
            plugin.LogStageDetailList();
        }

        if (ImGui.Button("Dump XBMContentsTreasure Nodes"))
        {
            plugin.LogTreasureNodes();
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Loaded LGB CollisionBoxes");
        ImGui.TextWrapped("CollisionBox transforms include world position, rotation, and scale. The nearby scan uses the player position.");
        if (ImGui.Button("Dump All LGB CollisionBoxes"))
        {
            plugin.LogLoadedLgbCollisionBoxes(nearbyOnly: false, radius: 0f);
        }

        ImGui.SetNextItemWidth(120f);
        ImGui.InputFloat("Nearby Radius", ref lgbCollisionRadius, 10f, 50f, "%.0f");
        ImGui.SameLine();
        if (ImGui.Button("Dump Nearby CollisionBoxes"))
        {
            plugin.LogLoadedLgbCollisionBoxes(nearbyOnly: true, radius: lgbCollisionRadius);
        }

        if (ImGui.Button("Analyze Treasure Selection"))
        {
            plugin.AnalyzeTreasureSelection();
        }
    }

    private static string FormatVector(Vector3 position)
        => $"<{position.X:0.0}, {position.Y:0.0}, {position.Z:0.0}>";
}
