using System.Collections.Generic;

namespace Sporeholm.Simulation.Entities
{
    // v0.8.0 (Phase 8) — agricultural role tags per species. Kept in a side
    // registry keyed by EntityKind rather than on EntityDef, so adding tags
    // doesn't disturb the 17-field EntityDef record or its 15 Register calls.
    // The Phase-8 work loops (taming, tending, produce, breeding, hunting,
    // butchery — v0.8.1/8.2) dispatch off these flags, so they're species-
    // agnostic: a new species participates the moment it's tagged here.
    [System.Flags]
    public enum AgriculturalTag
    {
        None       = 0,
        Tameable   = 1 << 0,
        Pet        = 1 << 1,   // follows owner instead of living in a pen
        Pack       = 1 << 2,
        Mount      = 1 << 3,
        Grazer     = 1 << 4,   // eats grass/forage/fungi
        Carnivore  = 1 << 5,   // must be fed meat in a pen
        Omnivore   = 1 << 6,
        Milkable   = 1 << 7,
        Shearable  = 1 << 8,
        EggLayer   = 1 << 9,
        Butcherable = 1 << 10, // a corpse yields meat/hide/bone (see EntityDef.ButcherDrops)
        Breeds     = 1 << 11,  // two tamed adults in a pen can produce young
        HuntAnimal = 1 << 12,  // sensible wild hunting target
        War        = 1 << 13,  // can be trained to fight alongside colonists
    }

    public static class AgriculturalTags
    {
        // The 15 shipped species (v0.6.0). The next 15 (roster expansion to 30)
        // and later tiers add their rows here as they're registered.
        //
        // v0.8.9 — tag consumption status (agricultural-audit follow-up):
        //   CONSUMED by a live system today —
        //     Tameable, Butcherable, HuntAnimal (tame/hunt designation + corpse keep),
        //     Milkable, EggLayer, Shearable (auto-produce), Breeds (breeding),
        //     Grazer (wild graze-recovery).
        //   RESERVED (assigned as per-species design metadata but read by NO code
        //   yet — intentional, not bugs; a later phase consumes them) —
        //     Pet (follow-owner), Pack (haul), Mount (ride), Carnivore / Omnivore
        //     (pen feeding), War (combat training). Kept on purpose — the plan that
        //     wires these into real behavior is roadmap "Phase 8.6 — Animal Husbandry
        //     Depth". Do not treat their orphan status as dead code.
        private static readonly Dictionary<EntityKind, AgriculturalTag> _byKind = new()
        {
            // Friendly / livestock
            [EntityKind.Glowbunny]       = AgriculturalTag.Tameable | AgriculturalTag.Pet | AgriculturalTag.Grazer | AgriculturalTag.Butcherable | AgriculturalTag.Breeds,
            [EntityKind.Shroomgoat]      = AgriculturalTag.Tameable | AgriculturalTag.Grazer | AgriculturalTag.Milkable | AgriculturalTag.Shearable | AgriculturalTag.Butcherable | AgriculturalTag.Breeds,
            [EntityKind.Shroomalo]       = AgriculturalTag.Tameable | AgriculturalTag.Grazer | AgriculturalTag.Milkable | AgriculturalTag.Butcherable | AgriculturalTag.Breeds,
            [EntityKind.Mouse]           = AgriculturalTag.Tameable | AgriculturalTag.Pet | AgriculturalTag.Omnivore | AgriculturalTag.Butcherable,
            [EntityKind.Ladybug]         = AgriculturalTag.Pet,
            [EntityKind.HermitCrab]      = AgriculturalTag.Tameable | AgriculturalTag.Omnivore | AgriculturalTag.Butcherable,
            // Neutral
            [EntityKind.Squirrel]        = AgriculturalTag.Tameable | AgriculturalTag.Omnivore | AgriculturalTag.Butcherable,
            [EntityKind.BonecrestBeetle] = AgriculturalTag.Butcherable,
            [EntityKind.ForestBoar]      = AgriculturalTag.HuntAnimal | AgriculturalTag.Omnivore | AgriculturalTag.Butcherable | AgriculturalTag.Breeds,
            [EntityKind.CaveLizard]      = AgriculturalTag.Carnivore | AgriculturalTag.Butcherable,
            // Hostile — hunt targets only (Magic Wisp leaves no corpse).
            [EntityKind.AntSoldier]      = AgriculturalTag.Butcherable,
            [EntityKind.WaspRenegade]    = AgriculturalTag.Butcherable,
            [EntityKind.Snake]           = AgriculturalTag.HuntAnimal | AgriculturalTag.Butcherable,
            [EntityKind.Wolf]            = AgriculturalTag.HuntAnimal | AgriculturalTag.Carnivore | AgriculturalTag.Butcherable,
            [EntityKind.MagicWisp]       = AgriculturalTag.None,

            // ── v0.8.0a (Phase 8) roster expansion 15 → 30 ──────────────
            // Friendly
            [EntityKind.ShoreFrog]       = AgriculturalTag.Tameable | AgriculturalTag.Mount | AgriculturalTag.Butcherable,
            [EntityKind.HoneyBeeSwarm]   = AgriculturalTag.Tameable | AgriculturalTag.EggLayer | AgriculturalTag.Breeds,
            [EntityKind.SkyPony]         = AgriculturalTag.Tameable | AgriculturalTag.Mount | AgriculturalTag.Breeds | AgriculturalTag.Butcherable,
            [EntityKind.Hamspore]        = AgriculturalTag.Tameable | AgriculturalTag.Shearable | AgriculturalTag.Grazer | AgriculturalTag.Butcherable,
            [EntityKind.Mushroomoise]    = AgriculturalTag.Tameable | AgriculturalTag.Mount | AgriculturalTag.Grazer | AgriculturalTag.Butcherable,
            [EntityKind.Quokka]          = AgriculturalTag.Tameable | AgriculturalTag.Pet | AgriculturalTag.Grazer | AgriculturalTag.Breeds | AgriculturalTag.Butcherable,
            [EntityKind.PygmyMarmoset]   = AgriculturalTag.Tameable | AgriculturalTag.Pet | AgriculturalTag.Butcherable,
            [EntityKind.Hamster]         = AgriculturalTag.Tameable | AgriculturalTag.Grazer | AgriculturalTag.Butcherable,
            // Neutral
            [EntityKind.Grumper]         = AgriculturalTag.HuntAnimal | AgriculturalTag.Butcherable,
            [EntityKind.PygmyRabbit]     = AgriculturalTag.Tameable | AgriculturalTag.Grazer | AgriculturalTag.Breeds | AgriculturalTag.Butcherable,
            [EntityKind.Truffleboar]     = AgriculturalTag.Tameable | AgriculturalTag.HuntAnimal | AgriculturalTag.War | AgriculturalTag.Omnivore | AgriculturalTag.Butcherable,
            [EntityKind.RoyalAntelope]   = AgriculturalTag.Tameable | AgriculturalTag.Mount | AgriculturalTag.Grazer | AgriculturalTag.Butcherable,
            [EntityKind.PygmyTortoise]   = AgriculturalTag.Tameable | AgriculturalTag.Mount | AgriculturalTag.Pack | AgriculturalTag.Butcherable,
            [EntityKind.MeerkatSentry]   = AgriculturalTag.Tameable | AgriculturalTag.Pet | AgriculturalTag.Pack | AgriculturalTag.Butcherable,
            // Hostile
            [EntityKind.FennecFox]       = AgriculturalTag.Tameable | AgriculturalTag.Carnivore | AgriculturalTag.HuntAnimal | AgriculturalTag.Butcherable,
        };

        public static AgriculturalTag For(EntityKind kind) =>
            _byKind.TryGetValue(kind, out var t) ? t : AgriculturalTag.None;

        public static bool Has(EntityKind kind, AgriculturalTag tag) => (For(kind) & tag) != 0;
    }
}
