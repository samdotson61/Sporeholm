using System.Collections.Generic;
using SmurfulationC.Simulation;

namespace SmurfulationC
{
    // Storyteller selection — Phase 8 will give each profile a distinct event
    // weighting curve. Stubbed here so the scenario screen has a real choice
    // to display; Balanced is the only behaviour currently implemented (all
    // future storytellers fall back to Balanced behaviour for now).
    public enum StorytellerType
    {
        Balanced,
        Patient,        // stub — slower escalation, longer peace
        Random,         // stub — uniform event distribution
        Cataclysmic,    // stub — fewer events, larger spikes
    }

    // A single smurf as configured on the Scenario screen. Lives on WorldState
    // between scene transitions; consumed by SimulationManager.SeedColony when
    // the game scene starts.
    public sealed class SmurfTemplate
    {
        public string  Name        = "";
        public Sex     Sex         = Sex.Male;
        public string  Role        = "Forager";
        public int     Age         = 60;
        public List<string> Personality = new();
        // v0.3.43 — DF-style preferences (likes / dislikes) rolled during
        // scenario setup. Optional: null means SimulationManager.SeedColony
        // will roll a fresh set so legacy save flows still work.
        public Preferences? Preferences;

        // v0.3.47 (Phase 4 sub-B) — role-appropriate starting kit rolled
        // through ItemFactory at scenario time. SeedColony adds these to
        // the colony Inventory on game start. Null = "roll at SeedColony";
        // populated = "scenario screen pre-rolled (player saw and
        // optionally rerolled)".
        public List<SmurfulationC.Simulation.Items.Item>? StartingItems;
    }

    // A single line in the colony's starting inventory. Tokens are stable
    // strings (e.g. "food.rations.7d"); Phase 4 resolves them into typed
    // Item instances at SeedColony time and distributes them across smurfs
    // by role / need (mirrors Dwarf Fortress's Prepare Carefully model where
    // supplies are a colony pool, not a per-dwarf inventory).
    public sealed class InventoryEntry
    {
        public string Token    = "";
        public int    Quantity = 1;
    }

    // Full scenario configuration produced by the Scenario screen and consumed
    // by SimulationManager.SeedColony. Replaces the hard-coded founding seven.
    public sealed class ScenarioConfig
    {
        public string             ColonyName  = "";
        public StorytellerType    Storyteller = StorytellerType.Balanced;
        public List<SmurfTemplate>  Smurfs           = new();
        public List<InventoryEntry> StartingInventory = new();

        // v0.4.19 — raised from 25 → 250 to match the perf target colony
        // size. Placeholder ceiling until the planned RimWorld-style
        // preset-population dropdown (Crashlanded / Tribal / Refugees /
        // …) replaces the raw spinner.
        public const int MaxSmurfs = 250;
        public const int MinSmurfs = 1;
    }
}
