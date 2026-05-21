using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation.Entities
{
    // v0.6.0 (Phase 6 — Roadmap §6.2). Static species registry. 15
    // species spanning the full disposition / biome / role spread per the
    // §6.2 baseline. Adding a species: append the new EntityKind, append
    // the EntityDef here, append a sprite painter in EntityColonyView.
    // PopulationCap + SpawnWeight tuned so a default-sized map (~240×150)
    // carries a believable ambient population: ~6-10 friendlies +
    // ~4-6 neutrals + ~2-4 hostiles per fresh worldgen.
    public static class EntityRegistry
    {
        private static readonly Dictionary<EntityKind, EntityDef> _byKind = new();
        private static readonly EntityDef[] _all;

        static EntityRegistry()
        {
            // ── Friendly / Passive (6) ─────────────────────────────────
            Register(new EntityDef(
                EntityKind.Glowbunny, "Glowbunny", EntityClass.Mammal, Disposition.Friendly,
                BiomeTag.Forest | BiomeTag.MagicGrove | BiomeTag.Hills,
                MaxHealth: 22f, BaseSpeedPxPerSec: 28f, BodyRadiusPx: 7f,
                AttackPower: 0f, AggroRangePx: 0f, FleeRangePx: 120f,
                MinGroupSize: 1, MaxGroupSize: 3, PopulationCapPerMap: 8, SpawnWeight: 1.2f,
                ButcherDrops: new[] { ("Meat", 1, 2), ("Hide", 1, 1) }));

            Register(new EntityDef(
                EntityKind.Shroomgoat, "Shroomgoat", EntityClass.Mammal, Disposition.Friendly,
                BiomeTag.Hills | BiomeTag.Forest | BiomeTag.MagicGrove,
                MaxHealth: 42f, BaseSpeedPxPerSec: 22f, BodyRadiusPx: 9f,
                AttackPower: 4f, AggroRangePx: 0f, FleeRangePx: 90f,
                MinGroupSize: 1, MaxGroupSize: 2, PopulationCapPerMap: 5, SpawnWeight: 0.9f,
                ButcherDrops: new[] { ("Meat", 2, 4), ("Hide", 1, 2), ("Bone", 1, 2) }));

            Register(new EntityDef(
                EntityKind.Shroomalo, "Shroomalo", EntityClass.Mammal, Disposition.Friendly,
                BiomeTag.AllOutdoor,
                MaxHealth: 30f, BaseSpeedPxPerSec: 24f, BodyRadiusPx: 8f,
                AttackPower: 2f, AggroRangePx: 0f, FleeRangePx: 60f,    // very low flee — friendly
                MinGroupSize: 1, MaxGroupSize: 4, PopulationCapPerMap: 10, SpawnWeight: 1.6f,
                ButcherDrops: new[] { ("Meat", 1, 3), ("Hide", 1, 1), ("SmallMushroom", 1, 2) }));

            Register(new EntityDef(
                EntityKind.Mouse, "Mouse", EntityClass.Mammal, Disposition.Friendly,
                BiomeTag.Forest | BiomeTag.Plains | BiomeTag.Hills | BiomeTag.Caves,
                MaxHealth: 8f, BaseSpeedPxPerSec: 36f, BodyRadiusPx: 4f,
                AttackPower: 0f, AggroRangePx: 0f, FleeRangePx: 80f,
                MinGroupSize: 1, MaxGroupSize: 2, PopulationCapPerMap: 8, SpawnWeight: 1.0f,
                ButcherDrops: new[] { ("Meat", 1, 1) }));

            Register(new EntityDef(
                EntityKind.Ladybug, "Ladybug", EntityClass.Insect, Disposition.Friendly,
                BiomeTag.Forest | BiomeTag.Plains | BiomeTag.MagicGrove,
                MaxHealth: 6f, BaseSpeedPxPerSec: 18f, BodyRadiusPx: 4f,
                AttackPower: 0f, AggroRangePx: 0f, FleeRangePx: 50f,
                MinGroupSize: 1, MaxGroupSize: 3, PopulationCapPerMap: 6, SpawnWeight: 0.7f,
                ButcherDrops: new[] { ("BoneFragment", 1, 1) }));   // chitin substitute pre-Phase 9

            Register(new EntityDef(
                EntityKind.HermitCrab, "Hermit Crab", EntityClass.Crustacean, Disposition.Friendly,
                BiomeTag.Coast | BiomeTag.Island,
                MaxHealth: 14f, BaseSpeedPxPerSec: 16f, BodyRadiusPx: 6f,
                AttackPower: 1f, AggroRangePx: 0f, FleeRangePx: 40f,
                MinGroupSize: 1, MaxGroupSize: 2, PopulationCapPerMap: 4, SpawnWeight: 0.5f,
                ButcherDrops: new[] { ("Meat", 1, 1), ("BoneFragment", 1, 2) }));

            // ── Neutral (4) ─────────────────────────────────────────────
            Register(new EntityDef(
                EntityKind.Squirrel, "Squirrel", EntityClass.Mammal, Disposition.Neutral,
                BiomeTag.Forest | BiomeTag.Hills,
                MaxHealth: 18f, BaseSpeedPxPerSec: 34f, BodyRadiusPx: 5f,
                AttackPower: 2f, AggroRangePx: 0f, FleeRangePx: 60f,
                MinGroupSize: 1, MaxGroupSize: 2, PopulationCapPerMap: 6, SpawnWeight: 1.0f,
                ButcherDrops: new[] { ("Meat", 1, 1), ("Hide", 1, 1) }));

            Register(new EntityDef(
                EntityKind.BonecrestBeetle, "Bonecrest Beetle", EntityClass.Insect, Disposition.Neutral,
                BiomeTag.Plains | BiomeTag.Hills | BiomeTag.Mountains,
                MaxHealth: 26f, BaseSpeedPxPerSec: 14f, BodyRadiusPx: 7f,
                AttackPower: 6f, AggroRangePx: 50f, FleeRangePx: 0f,   // doesn't flee
                MinGroupSize: 1, MaxGroupSize: 2, PopulationCapPerMap: 5, SpawnWeight: 0.8f,
                ButcherDrops: new[] { ("Bone", 1, 3), ("BoneFragment", 1, 2) }));

            Register(new EntityDef(
                EntityKind.ForestBoar, "Forest Boar", EntityClass.Mammal, Disposition.Neutral,
                BiomeTag.Forest | BiomeTag.Hills,
                MaxHealth: 48f, BaseSpeedPxPerSec: 26f, BodyRadiusPx: 9f,
                AttackPower: 10f, AggroRangePx: 80f, FleeRangePx: 0f,
                MinGroupSize: 1, MaxGroupSize: 2, PopulationCapPerMap: 4, SpawnWeight: 0.7f,
                ButcherDrops: new[] { ("Meat", 3, 5), ("Hide", 1, 2), ("Bone", 1, 2) }));

            Register(new EntityDef(
                EntityKind.CaveLizard, "Cave Lizard", EntityClass.Reptile, Disposition.Neutral,
                BiomeTag.Caves | BiomeTag.Mountains | BiomeTag.Peaks,
                MaxHealth: 28f, BaseSpeedPxPerSec: 22f, BodyRadiusPx: 7f,
                AttackPower: 7f, AggroRangePx: 100f, FleeRangePx: 0f,
                MinGroupSize: 1, MaxGroupSize: 2, PopulationCapPerMap: 5, SpawnWeight: 0.8f,
                ButcherDrops: new[] { ("Meat", 1, 2), ("Bone", 1, 2) }));

            // ── Hostile (5) ─────────────────────────────────────────────
            Register(new EntityDef(
                EntityKind.AntSoldier, "Ant Soldier", EntityClass.Insect, Disposition.Hostile,
                BiomeTag.Forest | BiomeTag.Plains | BiomeTag.Desert,
                MaxHealth: 10f, BaseSpeedPxPerSec: 26f, BodyRadiusPx: 4f,
                AttackPower: 5f, AggroRangePx: 130f, FleeRangePx: 0f,
                MinGroupSize: 3, MaxGroupSize: 6, PopulationCapPerMap: 10, SpawnWeight: 1.0f,
                ButcherDrops: new[] { ("BoneFragment", 1, 1) }));

            Register(new EntityDef(
                EntityKind.WaspRenegade, "Wasp Renegade", EntityClass.Insect, Disposition.Hostile,
                BiomeTag.Forest | BiomeTag.Plains | BiomeTag.MagicGrove,
                MaxHealth: 12f, BaseSpeedPxPerSec: 38f, BodyRadiusPx: 5f,
                AttackPower: 7f, AggroRangePx: 140f, FleeRangePx: 0f,
                MinGroupSize: 1, MaxGroupSize: 3, PopulationCapPerMap: 6, SpawnWeight: 0.6f,
                ButcherDrops: new[] { ("BoneFragment", 1, 1) }));

            Register(new EntityDef(
                EntityKind.Snake, "Snake", EntityClass.Reptile, Disposition.Hostile,
                BiomeTag.Forest | BiomeTag.Plains | BiomeTag.Desert,
                MaxHealth: 18f, BaseSpeedPxPerSec: 18f, BodyRadiusPx: 6f,
                AttackPower: 8f, AggroRangePx: 90f, FleeRangePx: 0f,
                MinGroupSize: 1, MaxGroupSize: 1, PopulationCapPerMap: 5, SpawnWeight: 0.6f,
                ButcherDrops: new[] { ("Meat", 1, 1), ("Hide", 1, 1) }));

            Register(new EntityDef(
                EntityKind.Wolf, "Wolf", EntityClass.Mammal, Disposition.Hostile,
                BiomeTag.Forest | BiomeTag.Hills | BiomeTag.Mountains,
                MaxHealth: 55f, BaseSpeedPxPerSec: 30f, BodyRadiusPx: 9f,
                AttackPower: 12f, AggroRangePx: 180f, FleeRangePx: 0f,
                MinGroupSize: 2, MaxGroupSize: 4, PopulationCapPerMap: 4, SpawnWeight: 0.4f,
                ButcherDrops: new[] { ("Meat", 2, 4), ("Hide", 1, 2), ("Bone", 1, 2) }));

            Register(new EntityDef(
                EntityKind.MagicWisp, "Magic Wisp", EntityClass.Mythical, Disposition.Hostile,
                BiomeTag.MagicGrove,
                MaxHealth: 14f, BaseSpeedPxPerSec: 18f, BodyRadiusPx: 5f,
                AttackPower: 3f, AggroRangePx: 90f, FleeRangePx: 30f,   // flees when struck
                MinGroupSize: 1, MaxGroupSize: 1, PopulationCapPerMap: 2, SpawnWeight: 0.3f,
                ButcherDrops: new[] { ("MagicEssence", 1, 1) }));

            _all = new EntityDef[_byKind.Count];
            int i = 0;
            foreach (var def in _byKind.Values) _all[i++] = def;
        }

        private static void Register(EntityDef def)
        {
            _byKind[def.Kind] = def;
        }

        public static EntityDef Get(EntityKind kind) =>
            _byKind.TryGetValue(kind, out var d) ? d : throw new ArgumentOutOfRangeException(nameof(kind));

        public static IReadOnlyList<EntityDef> All => _all;
    }
}
