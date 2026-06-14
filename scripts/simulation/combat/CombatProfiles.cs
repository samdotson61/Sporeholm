using System;
using Sporeholm.Simulation.Entities;

namespace Sporeholm.Simulation.Combat
{
    // v0.7.0 (Phase 7) — static lookups bridging the existing Item / Entity data
    // into combat. Kept in the Combat layer (not on ItemRegistry/EntityDef) so
    // the Items and Entities layers stay decoupled from combat — weapon damage
    // numbers still live on ItemSubTypeDef; only the *classification* lives here.
    public static class CombatProfiles
    {
        // Shroomp blood is blue (haemocyanin-based, per the mushroom-creature
        // canon). Packed 0xRRGGBBAA.
        public const uint ShroompBloodRgba = 0x4C78C0FFu;

        // Map a weapon/tool SubType (ItemRegistry key) to its damage profile
        // category. Drives the wound table + narrative verb. Unknown / non-weapon
        // subtypes fall back to Unarmed.
        public static WeaponType TypeForSubType(string subType) => subType switch
        {
            "Spear"               => WeaponType.Piercing,
            "Sword"               => WeaponType.Edged,
            "Axe"                 => WeaponType.Edged,
            "Knife"               => WeaponType.Edged,
            "Sickle"              => WeaponType.Edged,
            "Club"                => WeaponType.Blunt,
            "Hammer"              => WeaponType.Blunt,
            "Pick"                => WeaponType.Blunt,
            "Sling"               => WeaponType.Ranged,
            "Bow"                 => WeaponType.Ranged,
            "Crossbow"            => WeaponType.Ranged,
            "Atlatl"              => WeaponType.Ranged,
            "Focus"               => WeaponType.Magical,
            _                     => WeaponType.Unarmed,
        };

        // True for the ranged weapon types (drives skill selection + cadence).
        public static bool IsRanged(WeaponType t) =>
            t == WeaponType.Ranged || t == WeaponType.Magical;

        // A wild entity's natural attack. Damage comes from the entity's
        // per-individual jittered AttackPower; type / accuracy / reach / name are
        // per species. Reach is in tiles (converted to px by the engine).
        public static CombatProfile NaturalWeapon(EntityKind kind, float attackPower)
        {
            (WeaponType type, float acc, float rangeTiles, string name) spec = kind switch
            {
                EntityKind.Wolf            => (WeaponType.Edged,    0.80f, 1.6f, "fangs"),
                EntityKind.Snake           => (WeaponType.Piercing, 0.80f, 1.6f, "fangs"),
                EntityKind.WaspRenegade    => (WeaponType.Piercing, 0.78f, 1.9f, "stinger"),
                EntityKind.AntSoldier      => (WeaponType.Piercing, 0.75f, 1.4f, "mandibles"),
                EntityKind.BonecrestBeetle => (WeaponType.Piercing, 0.72f, 1.4f, "mandibles"),
                EntityKind.ForestBoar      => (WeaponType.Blunt,    0.78f, 1.6f, "tusks"),
                EntityKind.CaveLizard      => (WeaponType.Piercing, 0.78f, 1.5f, "bite"),
                EntityKind.Squirrel        => (WeaponType.Edged,    0.75f, 1.3f, "teeth"),
                EntityKind.Shroomgoat      => (WeaponType.Blunt,    0.70f, 1.5f, "horns"),
                EntityKind.Shroomalo       => (WeaponType.Blunt,    0.65f, 1.3f, "headbutt"),
                EntityKind.HermitCrab      => (WeaponType.Piercing, 0.70f, 1.4f, "pincer"),
                EntityKind.MagicWisp       => (WeaponType.Magical,  0.72f, 3.0f, "energy drain"),
                // Glowbunny / Mouse / Ladybug — harmless; only bite if forced.
                _                          => (WeaponType.Unarmed,  0.65f, 1.3f, "nip"),
            };
            int cooldown = IsRanged(spec.type)
                ? CombatTuning.RangedCooldownTicks
                : CombatTuning.MeleeCooldownTicks;
            bool venom = kind == EntityKind.Snake || kind == EntityKind.WaspRenegade;
            return new CombatProfile(attackPower, spec.acc, spec.type,
                spec.rangeTiles, cooldown, spec.name, venom);
        }

        // Natural-hide damage reduction (0..1) for entities. Most creatures have
        // none; armoured / tough ones shrug off a fraction. Shroomp armor comes
        // from worn apparel instead (see Shroomp.ArmorFractionAt).
        public static float NaturalArmor(EntityKind kind) => kind switch
        {
            EntityKind.BonecrestBeetle => 0.40f,   // chitin ridge plating
            EntityKind.ForestBoar      => 0.20f,   // thick bristled hide
            EntityKind.HermitCrab      => 0.30f,   // borrowed shell
            EntityKind.Wolf            => 0.10f,
            _                          => 0.0f,
        };

        // Blood-spatter colour for the feedback overlay, by species class.
        public static uint BloodRgba(EntityKind kind)
        {
            // Mushroom-kin fauna bleed pale fungal spore-fluid (teal), not red —
            // they share the colony's fungal biology, not mammalian blood.
            switch (kind)
            {
                case EntityKind.Shroomgoat:
                case EntityKind.Shroomalo:
                case EntityKind.Glowbunny:
                    return 0x6FB58CFFu;   // spore-teal
            }
            EntityClass cls = EntityRegistry.Get(kind).Class;
            return cls switch
            {
                EntityClass.Mammal     => 0xB01818FFu,   // red
                EntityClass.Insect     => 0x9FB04AFFu,   // pale green ichor
                EntityClass.Reptile    => 0x7A2A2AFFu,   // dark red
                EntityClass.Crustacean => 0x6FA0C0FFu,   // pale blue
                EntityClass.Bird       => 0xB01818FFu,   // red
                EntityClass.Mythical   => 0xA060D0FFu,   // violet (Magic Wisp)
                _                      => 0xB01818FFu,
            };
        }

        // Flavour body-part names for an entity hit (narrative only — entity
        // damage is a single HP pool). Picked at random per hit.
        public static string PickEntityPart(EntityKind kind, Random rng)
        {
            string[] parts = kind switch
            {
                EntityKind.Snake           => SnakeParts,
                EntityKind.CaveLizard      => LizardParts,
                EntityKind.WaspRenegade    => WingedInsectParts,
                EntityKind.Ladybug         => WingedInsectParts,
                EntityKind.AntSoldier      => InsectParts,
                EntityKind.BonecrestBeetle => BeetleParts,
                EntityKind.HermitCrab      => CrabParts,
                EntityKind.MagicWisp       => WispParts,
                _                          => MammalParts,
            };
            return parts[rng.Next(parts.Length)];
        }

        private static readonly string[] MammalParts       = { "head", "flank", "foreleg", "hind leg", "tail" };
        private static readonly string[] SnakeParts         = { "head", "coils", "tail" };
        private static readonly string[] LizardParts        = { "head", "body", "tail", "leg" };
        private static readonly string[] InsectParts        = { "head", "thorax", "abdomen", "leg" };
        private static readonly string[] WingedInsectParts  = { "head", "thorax", "abdomen", "leg", "wing" };
        private static readonly string[] BeetleParts        = { "head", "carapace", "leg", "mandible" };
        private static readonly string[] CrabParts          = { "shell", "claw", "leg" };
        private static readonly string[] WispParts          = { "core", "halo" };
    }
}
