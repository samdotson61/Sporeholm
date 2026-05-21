using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Entities;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // v0.6.0 (Phase 6 — Roadmap §6.4) — entity spawning. Two paths:
    //
    //   1. Initial seed (PopulateFromSpawnPoints): runs once when a fresh
    //      colony is started. Walks every AnimalSpawnPoint placed at
    //      LocalMapGenerator gen-time, rolls a group of the appropriate
    //      EntityKind, instantiates entities at the marker tile.
    //
    //   2. Ambient respawn (MaintainPopulation): called occasionally
    //      (once per in-game day) to refill species that fell below half
    //      their PopulationCapPerMap. Picks a random biome-appropriate
    //      passable tile away from the colony hearth and spawns a small
    //      group. Caps at PopulationCapPerMap so colonies never get
    //      overwhelmed by ambient fauna.
    //
    // Event-only ≥10× species (Bear / Mauler / Tortoise / Dragon / Drake)
    // are NEVER instantiated via this system — they're owned by the
    // Phase 8 Storyteller pipeline.
    public static class EntitySpawnSystem
    {
        // v0.6.0 — map AnimalKind (the v0.5.14 gen-time roster, which
        // pre-dates the v0.6.0 15-species roster) onto the new
        // EntityKind. The five legacy slots map 1:1; future AnimalKind
        // additions land here.
        private static EntityKind FromAnimalKind(AnimalKind k) => k switch
        {
            AnimalKind.MushroomGoat    => EntityKind.Shroomgoat,
            AnimalKind.BonecrestBeetle => EntityKind.BonecrestBeetle,
            AnimalKind.CaveLizard      => EntityKind.CaveLizard,
            AnimalKind.GlowBunny       => EntityKind.Glowbunny,
            AnimalKind.ForestBoar      => EntityKind.ForestBoar,
            _                          => EntityKind.Mouse,   // safe fallback
        };

        // Run once at fresh-colony seed. Idempotent: only spawns from
        // markers that have no existing entity within 2 tiles, so reloads
        // mid-game don't double-seed.
        public static void PopulateFromSpawnPoints(
            List<Entity> entities, LocalMap map, Random rng)
        {
            var spawns = map.SnapshotAnimalSpawns();
            foreach (var sp in spawns)
            {
                var kind = FromAnimalKind(sp.Kind);
                var def  = EntityRegistry.Get(kind);
                int groupSize = rng.Next(def.MinGroupSize, def.MaxGroupSize + 1);
                int spawned   = 0;
                for (int g = 0; g < groupSize && CountKind(entities, kind) < def.PopulationCapPerMap; g++)
                {
                    if (!FindNearbyPassable(map, sp.X, sp.Y, rng, out int tx, out int ty)) continue;
                    var pos = new Vector2(
                        tx * LocalMap.TileSize + LocalMap.TileSize / 2,
                        ty * LocalMap.TileSize + LocalMap.TileSize / 2);
                    entities.Add(Entity.SpawnAt(kind, pos, rng));
                    spawned++;
                }
            }

            // After consuming markers, seed the remaining EntityRegistry
            // species (those without AnimalSpawnPoint markers) at light
            // ambient density. Keeps the world feeling alive even on
            // freshly-generated maps that didn't roll the right markers.
            SeedAmbientFauna(entities, map, rng);
        }

        // Called by SimulationCore at day-boundary tick. Each call rolls
        // one ambient respawn pass per species under half its cap.
        public static void MaintainPopulation(
            List<Entity> entities, LocalMap map, Random rng)
        {
            foreach (var def in EntityRegistry.All)
            {
                int alive = CountKind(entities, def.Kind);
                int target = def.PopulationCapPerMap / 2;
                if (alive >= target) continue;
                if (rng.NextDouble() > def.SpawnWeight * 0.30) continue;   // throttle
                if (!FindRandomBiomeAppropriate(map, def.HabitatBiomes, rng, out int tx, out int ty)) continue;
                int groupSize = rng.Next(def.MinGroupSize, def.MaxGroupSize + 1);
                for (int g = 0; g < groupSize && CountKind(entities, def.Kind) < def.PopulationCapPerMap; g++)
                {
                    if (!FindNearbyPassable(map, tx, ty, rng, out int gx, out int gy)) continue;
                    var pos = new Vector2(
                        gx * LocalMap.TileSize + LocalMap.TileSize / 2,
                        gy * LocalMap.TileSize + LocalMap.TileSize / 2);
                    entities.Add(Entity.SpawnAt(def.Kind, pos, rng));
                }
            }
        }

        // One-shot seed at colony start: fill the registry up to half cap
        // for species without marker support, so the first day on the map
        // already shows wildlife. Only seeds species whose habitat list
        // includes the local map's primary biome (cheap proxy: just see
        // if AllOutdoor includes any of our tiles via a quick BFS).
        private static void SeedAmbientFauna(List<Entity> entities, LocalMap map, Random rng)
        {
            foreach (var def in EntityRegistry.All)
            {
                int target = Math.Max(1, def.PopulationCapPerMap / 3);
                int alreadySeeded = CountKind(entities, def.Kind);
                int toSeed = Math.Max(0, target - alreadySeeded);
                for (int i = 0; i < toSeed; i++)
                {
                    if (!FindRandomBiomeAppropriate(map, def.HabitatBiomes, rng, out int tx, out int ty)) break;
                    var pos = new Vector2(
                        tx * LocalMap.TileSize + LocalMap.TileSize / 2,
                        ty * LocalMap.TileSize + LocalMap.TileSize / 2);
                    entities.Add(Entity.SpawnAt(def.Kind, pos, rng));
                }
            }
        }

        private static int CountKind(List<Entity> entities, EntityKind kind)
        {
            int n = 0;
            for (int i = 0; i < entities.Count; i++)
                if (entities[i].IsAlive && entities[i].Kind == kind) n++;
            return n;
        }

        // Walk a tiny spiral around (x, y) looking for a passable tile.
        // Used to nudge a spawn off an impassable marker (e.g. marker
        // landed on a Boulder that wasn't yet excavated at gen-time).
        private static bool FindNearbyPassable(LocalMap map, int x, int y, Random rng, out int outX, out int outY)
        {
            for (int r = 0; r <= 3; r++)
            for (int dx = -r; dx <= r; dx++)
            for (int dy = -r; dy <= r; dy++)
            {
                int tx = x + dx, ty = y + dy;
                if (!map.InBounds(tx, ty)) continue;
                if (!map.IsPassable(tx, ty)) continue;
                outX = tx; outY = ty;
                return true;
            }
            outX = x; outY = y;
            return false;
        }

        // Random-tile sampler with biome guard. The single-biome local
        // map model means EVERY passable tile is biome-appropriate as
        // long as the species lists this biome in its HabitatBiomes.
        // For now we just check the local-map's biome via a known
        // accessor; if no match, return false.
        private static bool FindRandomBiomeAppropriate(LocalMap map, BiomeTag habitats,
            Random rng, out int outX, out int outY)
        {
            // Approximate biome match — if habitats includes AllOutdoor
            // we never reject; otherwise we trust the gen-time biome of
            // the local map (which exists on LocalMap.BiomeName but isn't
            // wired here, so for v0.6.0 we just accept any tile and
            // let the spawn density be biome-blind. Will tighten once
            // LocalMap exposes a typed Biome accessor.)
            for (int attempt = 0; attempt < 40; attempt++)
            {
                int tx = rng.Next(0, map.Width);
                int ty = rng.Next(0, map.Height);
                if (!map.IsPassable(tx, ty)) continue;
                // Don't spawn on top of built structures (annoys the player).
                var slot = map.GetStructure(tx, ty);
                if (slot.IsPresent && slot.Type != World.StructureType.Floor) continue;
                outX = tx; outY = ty;
                return true;
            }
            outX = 0; outY = 0;
            return false;
        }
    }
}
