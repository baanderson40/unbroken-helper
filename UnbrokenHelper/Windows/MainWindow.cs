using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using UnbrokenHelper.Data;

namespace UnbrokenHelper.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private sealed class StageDisplay
    {
        internal int Stage { get; init; }
        internal string Variant { get; init; } = string.Empty;
        internal string StageLabel { get; init; } = string.Empty;
        internal string Encounter { get; init; } = string.Empty;
        internal string MoveDetails { get; init; } = string.Empty;
        internal string EncounterDetails { get; init; } = string.Empty;
    }

    private readonly Plugin plugin;
    private readonly Dictionary<int, int> rosterPages = new();
    private int selectedBoard = 1;
    private bool selectedBoard3RouteA;
    private bool loopRuns = true;
    private int activeTab = -1;
    private bool tabSizeApplyPending;
    private Vector2? observedWindowSize;
    private bool windowSizeSavePending;
    private DateTime windowSizeChangedAt;

    public MainWindow(Plugin plugin)
        : base("Unbroken Helper###UnbrokenHelperMain")
    {
        this.plugin = plugin;
        Size = new Vector2(820, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(200, 200),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Bug,
            ShowTooltip = () => ImGui.SetTooltip("Open Developer Tools"),
            Click = _ => plugin.OpenDebugTools(),
        });
    }

    public void Dispose()
    {
        if (activeTab >= 0 && observedWindowSize.HasValue)
        {
            plugin.SaveMainWindowTabSize(activeTab, observedWindowSize.Value);
        }
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##unbroken-main-tabs"))
        {
            return;
        }

        var currentTab = -1;
        if (ImGui.BeginTabItem("Run"))
        {
            currentTab = 0;
            DrawRun();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Board 1"))
        {
            currentTab = 1;
            DrawBoard(1);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Board 2"))
        {
            currentTab = 2;
            DrawBoard(2);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Board 3"))
        {
            currentTab = 3;
            DrawBoard(3);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        UpdateTabWindowSize(currentTab);
    }

    private void UpdateTabWindowSize(int currentTab)
    {
        if (currentTab < 0)
        {
            return;
        }

        var currentSize = ImGui.GetWindowSize();
        if (activeTab != currentTab)
        {
            if (activeTab >= 0)
            {
                plugin.SaveMainWindowTabSize(activeTab, currentSize);
            }

            activeTab = currentTab;
            observedWindowSize = null;
            windowSizeSavePending = false;
            Size = plugin.GetMainWindowTabSize(currentTab);
            SizeCondition = ImGuiCond.Always;
            tabSizeApplyPending = true;
            return;
        }

        if (tabSizeApplyPending)
        {
            Size = null;
            tabSizeApplyPending = false;
            observedWindowSize = currentSize;
            return;
        }

        if (!observedWindowSize.HasValue ||
            Vector2.DistanceSquared(observedWindowSize.Value, currentSize) > 0.25f)
        {
            observedWindowSize = currentSize;
            windowSizeSavePending = true;
            windowSizeChangedAt = DateTime.UtcNow;
        }

        if (windowSizeSavePending && DateTime.UtcNow - windowSizeChangedAt >= TimeSpan.FromMilliseconds(500))
        {
            plugin.SaveMainWindowTabSize(activeTab, currentSize);
            windowSizeSavePending = false;
        }
    }

    private void DrawRun()
    {
        ImGui.TextUnformatted("Crucible Automation");
        ImGui.Separator();

        var board = selectedBoard - 1;
        ImGui.SetNextItemWidth(180f);
        if (ImGui.Combo("Board", ref board, "Board 1\0Board 2\0Board 3\0"))
        {
            selectedBoard = board + 1;
        }

        if (selectedBoard == 3)
        {
            var route = selectedBoard3RouteA ? 0 : 1;
            ImGui.SetNextItemWidth(180f);
            if (ImGui.Combo("Route", ref route, "Board 3A\0Board 3B\0"))
            {
                selectedBoard3RouteA = route == 0;
            }
        }

        ImGui.SameLine();
        if (plugin.EntryActive)
        {
            loopRuns = plugin.LoopAfterCompletion;
        }

        ImGui.BeginDisabled(plugin.EntryActive);
        ImGui.Checkbox("Loop after completion", ref loopRuns);
        ImGui.EndDisabled();
        ImGui.Separator();
        var configured = plugin.IsBoardConfigurationComplete(selectedBoard, selectedBoard3RouteA);
        if (configured)
        {
            ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.45f, 1f), "Familiar setup is complete.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.75f, 0.25f, 1f),
                "Familiar setup is incomplete. Configure the selected board before starting.");
        }

        if (!plugin.EntryActive)
        {
            ImGui.BeginDisabled(!configured);
            if (ImGui.Button("Start automation", new Vector2(180f, 32f)))
            {
                StartSelectedBoard();
            }

            ImGui.EndDisabled();
        }
        else
        {
            if (plugin.LoopAfterCompletion)
            {
                if (plugin.StopAfterCurrentLoopPending)
                {
                    if (ImGui.Button("Cancel pending stop", new Vector2(180f, 32f)))
                    {
                        plugin.CancelStopAfterCurrentLoop();
                    }
                }
                else if (ImGui.Button("Stop after current loop", new Vector2(180f, 32f)))
                {
                    plugin.RequestStopAfterCurrentLoop();
                }

                ImGui.SameLine();
            }

            if (ImGui.Button("Stop automation", new Vector2(180f, 32f)))
            {
                plugin.StopEntry();
            }
        }

        if (plugin.StopAfterCurrentLoopPending)
        {
            ImGui.TextDisabled("Pending stop: the current loop will finish before automation stops at Central Shroud.");
        }

        ImGui.Separator();
    }

    private void StartSelectedBoard()
    {
        switch (selectedBoard)
        {
            case 1:
                plugin.StartFirstBoardEntry(loopRuns);
                break;
            case 2:
                plugin.StartSecondBoardEntry(loopRuns);
                break;
            case 3 when selectedBoard3RouteA:
                plugin.StartThirdBoardAEntry(loopRuns);
                break;
            case 3:
                plugin.StartThirdBoardBEntry(loopRuns);
                break;
        }
    }

    private void DrawBoard(int board)
    {
        ImGui.TextUnformatted($"Board {board} Configuration");
        ImGui.TextWrapped("Set the board roster first, then assign three familiars to each battle stage.");

        var threshold = plugin.PetHealthThresholdPercent;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputInt("Minimum pet HP %", ref threshold))
        {
            plugin.SetPetHealthThresholdPercent(threshold);
        }

        ImGui.Separator();
        var boardRoster = plugin.GetFamiliarRoster(board);
        if (ImGui.CollapsingHeader(
                $"Board Roster ({boardRoster.Count}/{plugin.FamiliarRosterCapacity(board)})##main-board-roster-section-{board}",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawBoardRoster(board);
        }

        var stages = GetStages(board);
        var completeStages = stages.Count(stage =>
            plugin.GetStageFamiliarRoster(board, stage.Stage, stage.Variant).Count(number => number > 0) == 3);
        if (ImGui.CollapsingHeader(
                $"Stage Assignments ({completeStages}/{stages.Count} complete)##main-stage-section-{board}",
                ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (ImGui.BeginTable($"##main-stage-assignments-{board}", 6,
                    ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Stage", ImGuiTableColumnFlags.WidthFixed, 65f);
                ImGui.TableSetupColumn("Encounter", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Battlehorn 1", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("Battlehorn 2", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("Battlehorn 3", ImGuiTableColumnFlags.WidthFixed, 120f);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 75f);
                ImGui.TableHeadersRow();

                foreach (var stage in stages)
                {
                    DrawStageAssignment(board, stage);
                }

                ImGui.EndTable();
            }
        }
    }

    private void DrawBoardRoster(int board)
    {
        var roster = plugin.GetFamiliarRoster(board);
        ImGui.TextDisabled($"Unlocked familiars: {plugin.UnlockedFamiliarCount}/50");

        if (!plugin.UnlockedFamiliarDataReady)
        {
            ImGui.TextDisabled("Unlocked familiar data is still loading.");
        }

        if (plugin.TryGetOpenMonsterNotebook(out var notebookEntries))
        {
            DrawLiveNotebookRoster(board, roster, notebookEntries);
            return;
        }

        var page = rosterPages.TryGetValue(board, out var selectedPage) ? selectedPage : 0;
        ImGui.TextUnformatted("Familiar catalog");
        ImGui.SameLine();
        ImGui.BeginDisabled(page == 0);
        if (ImGui.SmallButton("<##familiar-page-previous"))
        {
            page--;
            rosterPages[board] = page;
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextUnformatted($"Page {page + 1} / 2");
        ImGui.SameLine();
        ImGui.BeginDisabled(page == 1);
        if (ImGui.SmallButton(">##familiar-page-next"))
        {
            page++;
            rosterPages[board] = page;
        }

        ImGui.EndDisabled();
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 110f);
        ImGui.BeginDisabled(plugin.EntryActive || roster.Count == 0);
        if (ImGui.Button("Clear roster", new Vector2(110f, 0f)))
        {
            plugin.ClearFamiliarRoster(board);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Clear this board roster and all Battlehorn assignments.");
        }

        if (!ImGui.BeginTable($"##main-board-notebook-{board}", 5,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        for (var row = 0; row < 5; row++)
        {
            ImGui.TableNextRow();
            for (var column = 0; column < 5; column++)
            {
                var number = page * 25 + row * 5 + column + 1;
                ImGui.TableNextColumn();
                DrawFamiliarNotebookTile(board, number, roster);
            }
        }

        ImGui.EndTable();
    }

    private void DrawLiveNotebookRoster(
        int board,
        IReadOnlyList<int> roster,
        IReadOnlyList<NotebookFamiliarEntry> notebookEntries)
    {
        ImGui.TextUnformatted("Live Monster Notebook");
        ImGui.SameLine();
        ImGui.TextDisabled("Mirroring the current game page and sort order.");
        ImGui.SameLine(ImGui.GetWindowContentRegionMax().X - 110f);
        ImGui.BeginDisabled(plugin.EntryActive || roster.Count == 0);
        if (ImGui.Button("Clear roster", new Vector2(110f, 0f)))
        {
            plugin.ClearFamiliarRoster(board);
        }

        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Clear this board roster and all Battlehorn assignments.");
        }

        if (notebookEntries.Count == 0)
        {
            ImGui.TextDisabled("The open notebook page has no familiar entries yet.");
            return;
        }

        if (!ImGui.BeginTable($"##main-board-live-notebook-{board}", 5,
                ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.BordersInnerV))
        {
            return;
        }

        for (var index = 0; index < notebookEntries.Count; index++)
        {
            if (index % 5 == 0)
            {
                ImGui.TableNextRow();
            }

            ImGui.TableNextColumn();
            DrawFamiliarNotebookTile(board, notebookEntries[index].Number, roster);
        }

        ImGui.EndTable();
    }

    private void DrawFamiliarNotebookTile(int board, int number, IReadOnlyList<int> roster)
    {
        var selected = roster.Contains(number);
        var unlocked = plugin.IsFamiliarUnlocked(number);
        var enabled = unlocked && !plugin.EntryActive &&
            (selected || roster.Count < plugin.FamiliarRosterCapacity(board));

        ImGui.PushID($"main-notebook-familiar-{board}-{number}");
        if (selected)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.20f, 0.45f, 0.30f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.25f, 0.55f, 0.35f, 1f));
        }

        ImGui.BeginDisabled(!enabled);
        if (ImGui.Button($"No. {number:00}\n{plugin.FamiliarName(number)}", new Vector2(-1f, 50f)))
        {
            if (selected)
            {
                plugin.RemoveFamiliarFromRoster(board, number);
            }
            else
            {
                plugin.AddFamiliarToRoster(board, number);
            }
        }

        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            var combatInfo = plugin.GetFamiliarCombatInfo(number);
            if (combatInfo != null)
            {
                DrawFamiliarCombatTooltip(number, combatInfo);
            }
            else if (!unlocked)
            {
                ImGui.SetTooltip("Familiar is locked or unavailable.");
            }
            else if (!selected && roster.Count >= plugin.FamiliarRosterCapacity(board))
            {
                ImGui.SetTooltip("Board roster is full.");
            }
            else if (selected)
            {
                ImGui.SetTooltip("Click to remove from this board roster.");
            }
            else
            {
                ImGui.SetTooltip("Click to add to this board roster.");
            }
        }

        if (selected)
        {
            ImGui.PopStyleColor(2);
        }

        ImGui.PopID();
    }

    private static void DrawFamiliarCombatTooltip(int number, FamiliarCombatEntry combatInfo)
    {
        ImGui.BeginTooltip();
        ImGui.TextUnformatted($"No. {number:00} combat information");
        ImGui.Separator();
        ImGui.TextUnformatted($"Class: {combatInfo.BeastClass}");
        ImGui.TextUnformatted($"Auto attack: {combatInfo.AutoAttackType}");
        DrawActionSummary("Tempered Release", combatInfo.TemperedRelease);
        DrawActionSummary("Trick", combatInfo.Trick);
        DrawActionSummary("Borrow", combatInfo.Borrow);
        ImGui.EndTooltip();
    }

    private static void DrawActionSummary(string label, FamiliarActionSummary action)
    {
        ImGui.Spacing();
        ImGui.TextUnformatted($"{label}: {action.Name} ({action.TargetType})");
        ImGui.TextWrapped(action.Description);
    }

    private void DrawStageAssignment(int board, StageDisplay stage)
    {
        var roster = plugin.GetStageFamiliarRoster(board, stage.Stage, stage.Variant);
        var assignedCount = roster.Count(number => number > 0);
        var complete = assignedCount == 3;
        ImGui.PushID($"main-stage-{board}-{stage.Stage}-{stage.Variant}");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(stage.StageLabel);
        DrawHoverTooltip(stage.MoveDetails);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(stage.Encounter);
        DrawHoverTooltip(stage.EncounterDetails);

        for (var slot = 0; slot < 3; slot++)
        {
            ImGui.TableNextColumn();
            DrawFamiliarSlot(board, stage.Stage, stage.Variant, roster, slot);
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(complete
            ? new Vector4(0.35f, 0.85f, 0.45f, 1f)
            : new Vector4(1f, 0.75f, 0.25f, 1f), complete ? "Complete" : $"Missing {3 - assignedCount}");

        ImGui.PopID();
    }

    private static void DrawHoverTooltip(string details)
    {
        if (string.IsNullOrWhiteSpace(details) || !ImGui.IsItemHovered())
        {
            return;
        }

        ImGui.BeginTooltip();
        ImGui.TextUnformatted(details);
        ImGui.EndTooltip();
    }

    private void DrawFamiliarSlot(int board, int stage, string variant, IReadOnlyList<int> roster, int slot)
    {
        var current = slot < roster.Count ? roster[slot] : 0;
        var label = current == 0 ? "Empty" : plugin.FamiliarName(current);
        ImGui.SetNextItemWidth(-1f);
        ImGui.BeginDisabled(plugin.EntryActive || slot > roster.Count);
        if (ImGui.BeginCombo($"##slot-{slot}", label))
        {
            if (current != 0 && ImGui.Selectable("Empty"))
            {
                plugin.SetStageFamiliarSlot(board, stage, slot, 0, variant);
            }

            foreach (var number in plugin.GetFamiliarRoster(board))
            {
                if (number != current && roster.Contains(number))
                {
                    continue;
                }

                if (ImGui.Selectable($"No. {number} - {plugin.FamiliarName(number)}", number == current))
                {
                    plugin.SetStageFamiliarSlot(board, stage, slot, number, variant);
                }
            }

            ImGui.EndCombo();
        }

        ImGui.EndDisabled();
    }

    private static IReadOnlyList<StageDisplay> GetStages(int board)
        => board switch
        {
            1 => new[]
            {
                new StageDisplay { Stage = 1, StageLabel = "Stage 1", Encounter = "Board 1 Stage 1" },
            },
            2 => new[]
            {
                new StageDisplay { Stage = 1, StageLabel = "Stage 1", Encounter = "Manticore" },
                new StageDisplay { Stage = 3, Variant = "B", StageLabel = "Stage 3B", Encounter = "Wyvern" },
                new StageDisplay { Stage = 5, StageLabel = "Stage 5", Encounter = "Voidmancer" },
                new StageDisplay { Stage = 7, StageLabel = "Stage 7", Encounter = "Tablitaur" },
                new StageDisplay { Stage = 10, StageLabel = "Stage 10", Encounter = "Loosefrox / Chewchum" },
            },
            // Battle reference: https://ffxiv.consolegameswiki.com/wiki/Third_Board_of_the_Unbroken
            3 => new[]
            {
                new StageDisplay
                {
                    Stage = 1,
                    StageLabel = "Move 1",
                    Encounter = "Cavalier / Bone Bishop",
                    MoveDetails = "Move 1\nEnemy 1: Combat 2 types of beast.\nSource: Third Board of the Unbroken wiki.",
                    EncounterDetails = "Cavalier Piece\nWeakness: Fire\nVulnerabilities: Poison\n\n" +
                        "Bone Bishop\nWeakness: Blunt\nVulnerabilities: Slow, Petrification, Paralysis, Stun, Bind",
                },
                new StageDisplay
                {
                    Stage = 2,
                    StageLabel = "Move 2",
                    Encounter = "Ymir / Sahagin",
                    MoveDetails = "Move 2\nEnemy 2: Combat 2 types of beast.\nSource: Third Board of the Unbroken wiki.",
                    EncounterDetails = "Ymir Piece\nWeakness: Ice\nVulnerabilities: Stun, Bind, Poison\n\n" +
                        "Sahagin Piece\nWeakness: Blunt\nVulnerabilities: Interruption, Blind",
                },
                new StageDisplay
                {
                    Stage = 4,
                    Variant = "A",
                    StageLabel = "Move 4A",
                    Encounter = "Zu",
                    MoveDetails = "Move 4A - Left Path\nElite enemy: Combat 3 types of beast.\nSource: Third Board of the Unbroken wiki.",
                    EncounterDetails = "Zu Piece\nWeakness: Earth\nVulnerabilities: Blind, Poison\n\n" +
                        "Cockerel Piece\nWeakness: Blunt\nVulnerabilities: Slow, Petrification, Paralysis, Blind, Stun, Sleep, Bind, Heavy, Flat damage, Poison\n\n" +
                        "Pullet Piece\nWeakness: Blunt\nVulnerabilities: Slow, Petrification, Interruption, Blind, Stun, Sleep, Flat damage, Poison",
                },
                new StageDisplay
                {
                    Stage = 4,
                    Variant = "B",
                    StageLabel = "Move 4B",
                    Encounter = "Catoblepas",
                    MoveDetails = "Move 4B - Right Path\nEnemy: Combat 1 type of beast.\nSource: Third Board of the Unbroken wiki.",
                    EncounterDetails = "Catoblepas Piece\nWeakness: Wind\nVulnerabilities: Poison",
                },
                new StageDisplay
                {
                    Stage = 6,
                    StageLabel = "Move 6",
                    Encounter = "Lakhamu / Golem",
                    MoveDetails = "Move 6\nEnemy 4: Combat 2 types of beast.\nSource: Third Board of the Unbroken wiki.",
                    EncounterDetails = "Lakhamu Piece\nWeakness: Water\nVulnerabilities: None\n\n" +
                        "Golem Piece\nWeakness: Water\nVulnerabilities: Slow, Stun, Sleep, Flat damage",
                },
                new StageDisplay
                {
                    Stage = 8,
                    StageLabel = "Move 8",
                    Encounter = "Siren",
                    MoveDetails = "Move 8\nElite enemy 2: Combat 3 types of beast.\nSource: Third Board of the Unbroken wiki.",
                    EncounterDetails = "Siren Piece\nWeakness: Piercing\nVulnerabilities: Poison\n\n" +
                        "Shambling Piece\nWeakness: Fire\nVulnerabilities: Slow, Paralysis, Bind, Heavy\n\n" +
                        "Crawling Piece\nWeakness: Fire\nVulnerabilities: None",
                },
                new StageDisplay
                {
                    Stage = 12,
                    StageLabel = "Move 12",
                    Encounter = "Guttler / Thanatos",
                    MoveDetails = "Move 12\nBoss of the Board: Combat 2 types of beast.\nSource: Third Board of the Unbroken wiki.",
                    EncounterDetails = "Guttler the Gutter\nWeakness: None\nVulnerabilities: Poison\n\n" +
                        "Thanatos Piece\nWeakness: Lightning\nVulnerabilities: Slow, Petrification, Stun",
                },
            },
            _ => Array.Empty<StageDisplay>(),
        };
}
