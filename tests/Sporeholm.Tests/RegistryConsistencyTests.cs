using Sporeholm.Simulation.Crafting;
using Sporeholm.Simulation.Items;
using Sporeholm.World;
using Xunit;

namespace Sporeholm.Tests;

// The executable version of the manual audit's "Pass A: Data Registry Consistency"
// (.claude/AUDIT.md). Every production path — recipes, crops — must resolve to
// registered entries in ItemRegistry and MaterialRegistry. The v0.8.9 butcher-flag
// bug and the v0.8.7 "recipe silently never offered" bug were both of this class.
public class RegistryConsistencyTests
{
    private static readonly HashSet<string> MaterialFamilies =
        MaterialRegistry.All.Select(m => m.Key.Family).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<(string, string)> MaterialKeys =
        MaterialRegistry.All.Select(m => (m.Key.Family, m.Key.SubType)).ToHashSet();

    // ── ItemRegistry invariants ────────────────────────────────────────────────

    [Fact]
    public void Item_subtypes_are_unique_within_their_kind()
    {
        var dupes = ItemRegistry.All
            .GroupBy(d => (d.Kind, d.SubType))
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key.Kind}/{g.Key.SubType}")
            .ToList();
        Assert.True(dupes.Count == 0, "Duplicate item defs: " + string.Join(", ", dupes));
    }

    [Fact]
    public void Item_AllowedFamilies_reference_registered_material_families()
    {
        var bad = ItemRegistry.All
            .SelectMany(d => d.AllowedFamilies.Select(f => (d, f)))
            .Where(x => !MaterialFamilies.Contains(x.f))
            .Select(x => $"{x.d.Kind}/{x.d.SubType} → '{x.f}'")
            .ToList();
        Assert.True(bad.Count == 0, "Items allowing unregistered material families: " + string.Join(", ", bad));
    }

    [Fact]
    public void Item_lookups_round_trip()
    {
        foreach (var def in ItemRegistry.All)
        {
            Assert.Same(def, ItemRegistry.Get(def.Kind, def.SubType));
            Assert.Contains(def, ItemRegistry.InKind(def.Kind));
        }
    }

    // ── MaterialRegistry invariants ────────────────────────────────────────────

    [Fact]
    public void Material_keys_are_unique()
    {
        var dupes = MaterialRegistry.All
            .GroupBy(m => m.Key)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key.ToString())
            .ToList();
        Assert.True(dupes.Count == 0, "Duplicate material keys: " + string.Join(", ", dupes));
    }

    [Fact]
    public void Material_multipliers_are_positive()
    {
        foreach (var m in MaterialRegistry.All)
        {
            Assert.True(m.DurabilityMul > 0, $"{m.Key} DurabilityMul");
            Assert.True(m.DecayRateMul > 0, $"{m.Key} DecayRateMul");
            Assert.True(m.ValueMul > 0, $"{m.Key} ValueMul");
        }
    }

    // ── RecipeRegistry ↔ Item/Material consistency (audit Pass A core) ─────────

    [Fact]
    public void Recipe_ids_are_unique_and_lookup_round_trips()
    {
        var dupes = RecipeRegistry.All.GroupBy(r => r.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(dupes.Count == 0, "Duplicate recipe ids: " + string.Join(", ", dupes));
        foreach (var r in RecipeRegistry.All)
            Assert.Same(r, RecipeRegistry.Get(r.Id));
    }

    [Fact]
    public void Every_recipe_output_is_a_registered_item()
    {
        var bad = RecipeRegistry.All
            .SelectMany(r => r.Outputs.Select(o => (r.Id, o)))
            .Where(x => ItemRegistry.Get(x.o.Kind, x.o.SubType) is null)
            .Select(x => $"{x.Id} → {x.o.Kind}/{x.o.SubType}")
            .ToList();
        Assert.True(bad.Count == 0, "Recipe outputs missing from ItemRegistry: " + string.Join(", ", bad));
    }

    [Fact]
    public void Every_recipe_output_material_is_registered()
    {
        // Explicit (family, subtype) pairs must exist; an empty subtype means
        // "roll within the family", so only the family must exist.
        var bad = new List<string>();
        foreach (var r in RecipeRegistry.All)
            foreach (var o in r.Outputs)
            {
                if (string.IsNullOrEmpty(o.MaterialSubType))
                {
                    if (!MaterialFamilies.Contains(o.MaterialFamily))
                        bad.Add($"{r.Id} → family '{o.MaterialFamily}'");
                }
                else if (!MaterialKeys.Contains((o.MaterialFamily, o.MaterialSubType)))
                    bad.Add($"{r.Id} → {o.MaterialFamily}/{o.MaterialSubType}");
            }
        Assert.True(bad.Count == 0, "Recipe outputs with unregistered materials: " + string.Join(", ", bad));
    }

    [Fact]
    public void Every_recipe_ingredient_family_and_subtype_resolve()
    {
        var bad = new List<string>();
        foreach (var r in RecipeRegistry.All)
            foreach (var ing in r.Ingredients)
            {
                if (ing.MaterialFamily is not null && !MaterialFamilies.Contains(ing.MaterialFamily))
                    bad.Add($"{r.Id} ← family '{ing.MaterialFamily}'");
                // RequiredSubType constrains the consumed stack's MATERIAL subtype
                // ("only Granite stone", "a DeadWood haft"). It must name a registered
                // material — in the ingredient's family when one is given — or the
                // ingredient can never be satisfied and the recipe is never offered
                // (the v0.8.7 bug class).
                if (ing.RequiredSubType is not null)
                {
                    bool exists = ing.MaterialFamily is not null
                        ? MaterialKeys.Contains((ing.MaterialFamily, ing.RequiredSubType))
                        : MaterialRegistry.All.Any(m => m.Key.SubType == ing.RequiredSubType);
                    if (!exists)
                        bad.Add($"{r.Id} ← {ing.MaterialFamily ?? "*"}/{ing.RequiredSubType}");
                }
            }
        Assert.True(bad.Count == 0, "Recipe ingredients that can never resolve: " + string.Join(", ", bad));
    }

    [Fact]
    public void Recipe_quantities_and_work_are_sane()
    {
        foreach (var r in RecipeRegistry.All)
        {
            Assert.True(r.WorkTicks > 0, $"{r.Id} WorkTicks");
            Assert.True(r.Outputs.Length > 0, $"{r.Id} has no outputs");
            foreach (var ing in r.Ingredients) Assert.True(ing.Amount > 0, $"{r.Id} ingredient amount");
            foreach (var o in r.Outputs) Assert.True(o.Amount > 0, $"{r.Id} output amount");
        }
    }

    // ── CropRegistry ↔ ItemRegistry ────────────────────────────────────────────

    [Fact]
    public void Every_crop_yield_is_a_registered_item()
    {
        var bad = CropRegistry.All
            .Where(c => ItemRegistry.Get(c.YieldItemKind, c.YieldItemSubType) is null)
            .Select(c => $"{c.Type} → {c.YieldItemKind}/{c.YieldItemSubType}")
            .ToList();
        Assert.True(bad.Count == 0, "Crop yields missing from ItemRegistry: " + string.Join(", ", bad));
    }

    [Fact]
    public void Crop_defs_are_unique_and_lookup_round_trips()
    {
        var dupes = CropRegistry.All.GroupBy(c => c.Type).Where(g => g.Count() > 1).Select(g => g.Key.ToString()).ToList();
        Assert.True(dupes.Count == 0, "Duplicate crop types: " + string.Join(", ", dupes));
        foreach (var c in CropRegistry.All)
            Assert.Same(c, CropRegistry.Get(c.Type));
    }

    [Fact]
    public void Crop_yield_ranges_and_growth_are_sane()
    {
        foreach (var c in CropRegistry.All)
        {
            Assert.True(c.YieldMin >= 1 && c.YieldMax >= c.YieldMin, $"{c.Type} yield range");
            Assert.True(c.GrowTicksPerStage > 0, $"{c.Type} grow ticks");
            Assert.True(c.BotanyMin >= 0 && c.BotanyMin <= 20, $"{c.Type} botany gate");
        }
    }

    [Fact]
    public void Botany_gating_is_monotonic()
    {
        // CanPlant must never allow a crop at a lower botany that it denies at a higher one.
        foreach (var c in CropRegistry.All)
        {
            Assert.False(CropRegistry.CanPlant(c.Type, c.BotanyMin - 1 < 0 ? -1 : c.BotanyMin - 1)
                         && c.BotanyMin > 0, $"{c.Type} plantable below its gate");
            Assert.True(CropRegistry.CanPlant(c.Type, c.BotanyMin), $"{c.Type} not plantable at its own gate");
            Assert.True(CropRegistry.CanPlant(c.Type, 20), $"{c.Type} not plantable at max botany");
        }
    }
}
