using System;
using System.Collections.Generic;
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
    private readonly Dictionary<string, string> familiarSearches = new();
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
        ImGui.TextWrapped("Move selection replaces configured familiars below this HP percentage with the next eligible roster pet.");
        ImGui.Separator();
        if (ImGui.CollapsingHeader("First Board Roster", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFamiliarRoster(1);
        }

        if (ImGui.CollapsingHeader("Second Board Roster", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFamiliarRoster(2);
        }

        if (ImGui.CollapsingHeader("Third Board Roster", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFamiliarRoster(3);
        }

        if (ImGui.CollapsingHeader("First Master's Board Roster", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawFamiliarRoster(4);
        }

        ImGui.Separator();
        if (ImGui.CollapsingHeader("First Board Battle Moves", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawStageRoster(1, 1, "First Board Move 1");
        }

        if (ImGui.CollapsingHeader("Second Board Battle Moves", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawStageRoster(2, 1, "Second Board Move 1 - Manticore");
            DrawStageRoster(2, 3, "Second Board Move 3B - Wyvern");
            DrawStageRoster(2, 5, "Second Board Move 5 - Voidmancer");
            DrawStageRoster(2, 7, "Second Board Move 7 - Tablitaur");
            DrawStageRoster(2, 10, "Second Board Move 10 - Loosefrox / Chewchum");
        }

        if (ImGui.CollapsingHeader("Third Board Battle Moves", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawStageRoster(3, 1, "Third Board Move 1 - Cavalier / Bone Bishop");
            DrawStageRoster(3, 2, "Third Board Move 2 - Ymir / Sahagin");
            DrawStageRoster(3, 4, "Third Board A Move 4A - Zu", "A");
            DrawStageRoster(3, 4, "Third Board B Move 4B - Catoblepas", "B");
            DrawStageRoster(3, 6, "Third Board Move 6 - Lakhamu");
            DrawStageRoster(3, 8, "Third Board Move 8 - Crawling / Shambling / Siren");
            DrawStageRoster(3, 12, "Third Board Move 12 - Guttler");
        }

        if (ImGui.CollapsingHeader("First Master's Board Battle Moves", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawStageRoster(4, 1, "Move 1 - Strix Piece");
            DrawStageRoster(4, 2, "Move 2B - Random: Queen Hawk / Corpse Flower", "B");
            DrawStageRoster(4, 5, "Move 5 - Gargoyle Piece");
            DrawStageRoster(4, 7, "Move 7B - Treant / Slugs", "B");
            DrawStageRoster(4, 9, "Move 9 - Borgny / Toxic Mass");
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
        var unlockedDataReady = plugin.UnlockedFamiliarDataReady;
        ImGui.TextUnformatted($"Unlocked familiars: {plugin.UnlockedFamiliarCount}/50");
        if (!unlockedDataReady)
        {
            ImGui.TextDisabled("Unlocked familiar data is still loading.");
        }

        if (unlockedDataReady && ImGui.BeginCombo($"Add familiar##board-{board}", "Select familiar"))
        {
            var searchKey = $"board-roster-{board}";
            var search = GetFamiliarSearch(searchKey);
            ImGui.SetNextItemWidth(240f);
            if (ImGui.InputText($"Search by number or name##{searchKey}", ref search, 64))
            {
                familiarSearches[searchKey] = search;
            }

            var matchingFamiliarCount = 0;
            for (var number = 1; number <= 50; number++)
            {
                if (roster.Contains(number) || !plugin.IsFamiliarUnlocked(number) ||
                    !MatchesFamiliarSearch(number, search))
                {
                    continue;
                }

                matchingFamiliarCount++;
                var label = $"No. {number} - {plugin.FamiliarName(number)}";
                if (ImGui.Selectable(label))
                {
                    plugin.AddFamiliarToRoster(board, number);
                }
            }

            if (matchingFamiliarCount == 0)
            {
                ImGui.TextDisabled("No matching familiars.");
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

    private void DrawStageRoster(int board, int stage, string label, string variant = "")
    {
        var boardRoster = plugin.GetFamiliarRoster(board);
        var stageRoster = plugin.GetStageFamiliarRoster(board, stage, variant);
        var assignedCount = stageRoster.Count(number => number > 0);
        ImGui.TextUnformatted($"{label} ({assignedCount}/3)");
        if (ImGui.BeginCombo($"Add familiar##stage-{board}-{stage}-{variant}", "Select familiar"))
        {
            var searchKey = $"stage-roster-{board}-{stage}-{variant}";
            var search = GetFamiliarSearch(searchKey);
            ImGui.SetNextItemWidth(240f);
            if (ImGui.InputText($"Search by number or name##{searchKey}", ref search, 64))
            {
                familiarSearches[searchKey] = search;
            }

            var matchingFamiliarCount = 0;
            foreach (var number in boardRoster)
            {
                if (stageRoster.Contains(number) || !MatchesFamiliarSearch(number, search))
                {
                    continue;
                }

                matchingFamiliarCount++;
                if (ImGui.Selectable($"No. {number} - {plugin.FamiliarName(number)}"))
                {
                    plugin.AddStageFamiliar(board, stage, number, variant);
                }
            }

            if (matchingFamiliarCount == 0)
            {
                ImGui.TextDisabled("No matching familiars.");
            }

            ImGui.EndCombo();
        }

        foreach (var number in stageRoster.Where(number => number > 0).ToArray())
        {
            ImGui.PushID($"stage-{board}-{stage}-{variant}-familiar-{number}");
            ImGui.TextUnformatted($"No. {number} - {plugin.FamiliarName(number)}");
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                plugin.RemoveStageFamiliar(board, stage, number, variant);
            }

            ImGui.PopID();
        }
    }

    private string GetFamiliarSearch(string key)
        => familiarSearches.TryGetValue(key, out var search) ? search : string.Empty;

    private bool MatchesFamiliarSearch(int number, string search)
        => string.IsNullOrWhiteSpace(search) ||
            number.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
            plugin.FamiliarName(number).Contains(search, StringComparison.OrdinalIgnoreCase);

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
        ImGui.TextUnformatted("Board Entry Automation");
        ImGui.TextWrapped("Targets Lauda, enters the Crucible, selects a board, challenges it, and follows the recorded route with VNav.");
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
            if (ImGui.Button("Start Second Board Entry"))
            {
                plugin.StartSecondBoardEntry();
            }

            if (ImGui.Button("Start Third Board A Entry"))
            {
                plugin.StartThirdBoardAEntry();
            }

            if (ImGui.Button("Start Third Board B Entry"))
            {
                plugin.StartThirdBoardBEntry();
            }

            if (ImGui.Button("Start First Master's Board Entry"))
            {
                plugin.StartFirstMastersBoardEntry();
            }
        }

        if (plugin.EntryActive)
        {
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
