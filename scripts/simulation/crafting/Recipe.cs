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

    // v0.6.2 (Phase 5.6 ship) — `Station` controls which built structure
    // can execute the recipe. Lets the Cooking split route food recipes
    // (CookMeal / JuiceBerries) to the new CookingTable and away from the
    // Workbench, while Bonfire gets to act as a 50%-speed fallback so a
    // bare colony can still cook before the player builds a proper table.
    //
    //   Workbench    — workbench-only (tools, knives, cloth, planks, etc.). DEFAULT.
    //   CookingTable — cooking recipes; runs at CookingTable at full speed,
    //                  or at a Bonfire at WorkTicks × BonfireSpeedMul (× 2.0 → 50% speed).
    //
    // Adding a new station means extending this enum and teaching
    // BillSystem.GetStationKind(StructureType) about it.
    public enum RecipeStation
    {
        Workbench    = 0,
        CookingTable = 1,
    }

    public sealed record RecipeDef(
        string     Id,                         // stable key, e.g. "CookMeal", "BrewBerryWine"
        string     DisplayName,                // player-facing label
        string     Description,                // tooltip / picker preview
        RecipeIngredient[] Ingredients,        // what gets consumed
        RecipeOutput[]     Outputs,            // what gets produced
        int        WorkTicks,                  // base sim-ticks at SkillCurve 1.0× (scaled by primary skill speed)
        string     PrimarySkill,               // e.g. "Crafting", "Cooking", "Mining"
        int        SkillMinimum         = 0,   // pawns below this skill level can't take the bill
        string?    SecondarySkill       = null,// some recipes train two skills (e.g. Magic Herb Poultice = Crafting + Healing)
        int        SecondaryMinimum     = 0,
        int        XpReward             = 80,  // primary skill XP per completion (matches the existing Cook 80)
        RecipeStation Station           = RecipeStation.Workbench,
        bool       AllowBonfireFallback  = false, // true = recipe can also run at Bonfire (× BonfireSpeedMul)
        float      BonfireSpeedMul       = 2.0f); // > 1.0 = slower at Bonfire. 2.0 = half speed.
}
