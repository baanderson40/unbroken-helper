using System;
using System.Collections.Generic;
using System.Linq;

namespace UnbrokenHelper.Data;

public sealed class FamiliarCombatCatalog
{
    public int SchemaVersion { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Patch { get; set; } = string.Empty;
    public List<FamiliarCombatEntry> Familiars { get; set; } = new();

    public FamiliarCombatEntry? Find(int number)
        => Familiars.FirstOrDefault(familiar => familiar.Number == number);
}

public sealed class FamiliarCombatEntry
{
    public int Number { get; set; }
    public string BeastClass { get; set; } = string.Empty;
    public string AutoAttackType { get; set; } = string.Empty;
    public FamiliarActionSummary TemperedRelease { get; set; } = new();
    public FamiliarActionSummary Trick { get; set; } = new();
    public FamiliarActionSummary Borrow { get; set; } = new();
}

public sealed class FamiliarActionSummary
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
}
