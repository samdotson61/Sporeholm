using System;
using System.Collections.Generic;
using System.Linq;

namespace SmurfulationC.Simulation.Systems
{
    // v0.3.47 (Phase 4 sub-B) — wandering-in event.
    //
    // A new smurf shows up at the colony edge asking to join. Roadmap §4
    // demographic-growth spec:
    //
    //   • Base incidence — 2-4 wanderers per in-game year for small (≤ 30)
    //     colonies; tapers to 0-1 per year as the colony grows past 100;
    //     ceases past 200.
    //   • Sex ratio — every wanderer rolls against 1:49 ratio (Smurfs canon).
    //     This is the primary way a female-less founding colony eventually
    //     unlocks the birth pathway.
    //   • Random age 20-380, random role pick, random personality.
    //   • Items: one random tool item (the rest of their belongings stayed
    //     with their old colony). Tool is added to colony inventory at
    //     arrival (per-smurf inventory lands in Phase 5 alongside Haul).
    //
    // Auto-accepts for now — the player prompt with Accept / Decline lands
    // alongside the Phase 8 storyteller event system. AlertsPane gets a
    // "Wanderer joined: <name>" entry via SimulationManager → main thread
    // signal flow.
    public static class WanderingInSystem
    {
        // Per-season chance scaled by colony size. The math here is calibrated
        // so the expected per-year count (= 4 × per-season chance) is
        // ~3 for colonies ≤ 30, tapering to ~1 by 100 and 0 by 200.
        private static double SeasonChance(int alive)
        {
            if (alive <= 30) return 0.75;          // 4 × 0.75 = 3/year
            if (alive >= 200) return 0.0;
            // Linear taper between 30 and 200.
            double t = (alive - 30) / 170.0;       // 0..1
            return Math.Max(0.0, 0.75 * (1.0 - t));
        }

        // Roll a wandering-in event for this season. Returns a new Smurf
        // if one arrives, null otherwise. Caller adds to the live list.
        // v0.4.7 (bugreport B-8) — `globalTick` lets us stamp the
        // wanderer's starting-tool birth tick correctly so spoilage /
        // age work right from arrival.
        public static Smurf? TryWanderer(IReadOnlyList<Smurf> living, Random rng, int foodTotal, long globalTick = 0)
        {
            int alive = living.Count(s => s.IsAlive);
            double pSeason = SeasonChance(alive);
            if (pSeason <= 0.0) return null;
            if (rng.NextDouble() > pSeason) return null;

            // Food gate — colonies on the brink shouldn't get a new mouth.
            // Wanderers prefer fed colonies; if reserves are below half the
            // capacity floor, they walk past. Skipped for nascent colonies
            // (≤ 5 smurfs) so the founding can still attract a female.
            int floor = Math.Max(1, alive) * 15;   // 15 food/smurf = "fed"
            if (alive > 5 && foodTotal < floor) return null;

            // 1:49 female ratio (canonical Smurfs distribution).
            var sex = rng.Next(49) == 0 ? Sex.Female : Sex.Male;

            int age = 20 + rng.Next(360);   // 20–379
            // Random role excluding Unassigned (the wanderer arrives with
            // a vocation).
            string[] roles = { "Forager", "Crafter", "Scholar", "Mage",
                               "Caretaker", "Guardian" };
            string role = roles[rng.Next(roles.Length)];

            var existingNames = living.Select(s => s.Name);
            string name = SmurfNameGenerator.Generate(existingNames, rng, sex);

            var s = new Smurf
            {
                Name              = name,
                AgeInYears        = age,
                Sex               = sex,
                Role              = role,
                BirthdayDayOfYear = rng.Next(0, 120),
                Nutrition         = 65f,    // arrived with a bit of road wear
                Rest              = 55f,
                Social            = 70f,
                MagicResonance    = 70f,
                Safety            = 60f,
            };

            TraitRegistry.AssignDawnEraTraits(s, rng);
            SkillRegistry.Distribute(s, rng);
            s.Personality = PersonalityRegistry.Assign(rng, age);
            s.BodyParts   = BodyPartRegistry.CreateHealthy();
            s.Preferences = PreferenceRegistry.Assign(rng, s.Personality);
            WorkPriorityDefaults.ApplyRoleDefaults(s);
            s.Handedness  = HandednessMeta.Roll(rng);

            // Position near an existing colony member so the wanderer
            // doesn't appear at (0,0). Picks a random alive smurf as the
            // arrival anchor; the in-game effect is "walked in from the
            // edge of the visible group."
            var anyAlive = living.FirstOrDefault(o => o.IsAlive);
            if (anyAlive != null)
            {
                float ox = (float)((rng.NextDouble() - 0.5) * 96);
                float oy = (float)((rng.NextDouble() - 0.5) * 96);
                s.SimPos = anyAlive.SimPos + new Godot.Vector2(ox, oy);
                s.SimTarget = s.SimPos;
                s.SimSpeed = anyAlive.SimSpeed;
            }

            // v0.4.7 (bugreport B-8) — wanderers arrive with one
            // role-appropriate tool 60% of the time. Spec: "wanderers
            // arrive with one random tool item if any (the rest of
            // their belongings stay with their old colony)." Tool is
            // rolled at Crude or Normal quality (a fresh-from-home
            // hand-me-down, not a Masterwork heirloom) and goes
            // straight into the smurf's dominant hand.
            if (rng.NextDouble() < 0.60)
            {
                string toolSub = role switch
                {
                    "Crafter"   => "Hammer",
                    "Forager"   => "Basket",
                    "Mage"      => "Focus",
                    "Caretaker" => "Basket",
                    "Guardian"  => rng.Next(2) == 0 ? "Hammer" : "Sickle",
                    "Scholar"   => "Focus",
                    "Elder"     => "Focus",
                    _           => "Basket",
                };
                var tool = SmurfulationC.Simulation.Items.ItemFactory.Create(
                    SmurfulationC.Simulation.Items.ItemKind.Tool, toolSub,
                    material: null, rng, globalTick, skillLevel: 0, quantity: 1);
                tool.OwnerSmurfId = s.Id;
                s.Equipment[HandednessMeta.DominantHand(s.Handedness)] = tool;
            }

            return s;
        }
    }
}
