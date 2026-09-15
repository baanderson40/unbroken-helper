using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace UnbrokenHelper.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private readonly Dictionary<int, int> rosterPages = new();
    private int selectedBoard = 1;
    private bool selectedBoard3RouteA;
    private bool loopRuns = true;

    public MainWindow(Plugin plugin)
        : base("Unbroken Helper###UnbrokenHelperMain")
    {
        this.plugin = plugin;
        Size = new Vector2(820, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public void Dispose()
    {
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##unbroken-main-tabs"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Run"))
        {
            DrawRun();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Board 1"))
        {
            DrawBoard(1);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Board 2"))
        {
            DrawBoard(2);
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Board 3"))
        {
            DrawBoard(3);
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawRun()
    {
        ImGui.TextUnformatted("Crucible Automation");
        ImGui.TextWrapped("Choose a board, confirm its familiar setup, and start the automation.");
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
        ImGui.Checkbox("Loop after completion", ref loopRuns);
        ImGui.TextDisabled("Loop is enabled by default and restarts after returning to the Central Shroud.");

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
            if (ImGui.Button("Stop automation", new Vector2(180f, 32f)))
            {
                plugin.StopEntry();
            }
        }

        ImGui.SameLine();
        ImGui.TextUnformatted(plugin.EntryActive ? "Running" : "Stopped");

        ImGui.Separator();
        ImGui.TextUnformatted("Run status");
        ImGui.TextUnformatted($"State: {plugin.EntryStateText}");
        ImGui.TextUnformatted($"Waiting for: {plugin.EntryTarget}");
        ImGui.TextUnformatted($"Territory: {plugin.ClientTerritoryId}");

        ImGui.Separator();
        ImGui.TextUnformatted("Need to inspect something?");
        ImGui.TextWrapped("The original Debug window is still available for addon, object, action, sheet, and route diagnostics.");
        if (ImGui.Button("Open Developer Tools"))
        {
            plugin.OpenDebugTools();
        }
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
                    DrawStageAssignment(board, stage.Stage, stage.Variant, stage.StageLabel, stage.Encounter);
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

        var page = rosterPages.TryGetValue(board, out var selectedPage) ? selectedPage : 0;
        ImGui.TextUnformatted("Familiar notebook");
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
            if (!unlocked)
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

    private void DrawStageAssignment(int board, int stage, string variant, string stageLabel, string encounter)
    {
        var roster = plugin.GetStageFamiliarRoster(board, stage, variant);
        var assignedCount = roster.Count(number => number > 0);
        var complete = assignedCount == 3;
        ImGui.PushID($"main-stage-{board}-{stage}-{variant}");
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(stageLabel);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(encounter);

        for (var slot = 0; slot < 3; slot++)
        {
            ImGui.TableNextColumn();
            DrawFamiliarSlot(board, stage, variant, roster, slot);
        }

        ImGui.TableNextColumn();
        ImGui.TextColored(complete
            ? new Vector4(0.35f, 0.85f, 0.45f, 1f)
            : new Vector4(1f, 0.75f, 0.25f, 1f), complete ? "Complete" : $"Missing {3 - assignedCount}");

        ImGui.PopID();
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

    private static IReadOnlyList<(int Stage, string Variant, string StageLabel, string Encounter)> GetStages(int board)
        => board switch
        {
            1 => new[] { (1, string.Empty, "Stage 1", "Board 1 Stage 1") },
            2 => new[]
            {
                (1, string.Empty, "Stage 1", "Manticore"),
                (3, "B", "Stage 3B", "Wyvern"),
                (5, string.Empty, "Stage 5", "Voidmancer"),
                (7, string.Empty, "Stage 7", "Tablitaur"),
                (10, string.Empty, "Stage 10", "Loosefrox / Chewchum"),
            },
            3 => new[]
            {
                (1, string.Empty, "Stage 1", "Cavalier / Bone Bishop"),
                (2, string.Empty, "Stage 2", "Ymir / Sahagin"),
                (4, "A", "Stage 4A", "Zu"),
                (4, "B", "Stage 4B", "Catoblepas"),
                (6, string.Empty, "Stage 6", "Lakhamu"),
                (8, string.Empty, "Stage 8", "Siren"),
                (12, string.Empty, "Stage 12", "Guttler"),
            },
            _ => Array.Empty<(int, string, string, string)>(),
        };
}
