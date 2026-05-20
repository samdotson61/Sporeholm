using Sporeholm.Simulation.Items;

namespace Sporeholm.Simulation.Crafting
{
    // v0.5.84s — Phase 5.5 Crafting Bills System. A `Recipe` defines a
    // recipe a workbench can execute: what it consumes, what it produces,
    // how long it takes, and what skill drives it. RecipeRegistry holds
    // the static list; Bill instances (per-workbench) reference recipes
    // by `Id` string.
    //
    // RimWorld parallel: `RecipeDef` in Defs/RecipeDefs/. The fields below
    // map to RecipeDef's: defName → Id, label → DisplayName, ingredients
    // → Ingredients, products → Output, workAmount → WorkTicks,
    // workSpeedStat → PrimarySkill, skillRequirements → SkillRequirement.
    //
    // Material handling: each Ingredient names an `ItemKind` (Food /
    // Material / Magic / etc.) plus a `MaterialFamily` ("Plant", "Stone",
    // "Wood", "Magic", "Bone"). Inventory.ConsumeByFamily picks the
    // best-quality stack matching that family; the Bill can further
    // constrain to a specific subtype (e.g. "only Granite stone").
    //
    // v0.5.84t — MaterialFamily is now nullable. null = "any family of this
    // Kind" (e.g. CookMeal takes 4 of any Food: Plant berries, Magic essence,
    // future Meat, etc.). Inventory.ConsumeByKind covers the consume side.
    public sealed record RecipeIngredient(
        ItemKind   Kind,
        string?    MaterialFamily,
        int        Amount,
        string?    RequiredSubType = null);   // null = any subtype within family

    public sealed record RecipeOutput(
        ItemKind   Kind,
        string     SubType,
        string     MaterialFamily,
        string     MaterialSubType,
        int        Amount,
        bool       RollQuality = true);       // most recipes roll quality from skill

    public sealed record RecipeDef(
        string     Id,                         // stable key, e.g. "CookMeal", "BrewBerryWine"
        string     DisplayName,                // player-facing label
        string     Description,                // tooltip / picker preview
        RecipeIngredient[] Ingredients,        // what gets consumed
        RecipeOutput[]     Outputs,            // what gets produced
        int        WorkTicks,                  // base sim-ticks at SkillCurve 1.0× (scaled by primary skill speed)
        string     PrimarySkill,               // e.g. "Crafting", "Cooking" (post-restructure → Crafting), "Mining"
        int        SkillMinimum         = 0,   // pawns below this skill level can't take the bill
        string?    SecondarySkill       = null,// some recipes train two skills (e.g. Magic Herb Poultice = Crafting + Healing)
        int        SecondaryMinimum     = 0,
        int        XpReward             = 80); // primary skill XP per completion (matches the existing Cook 80)
}
