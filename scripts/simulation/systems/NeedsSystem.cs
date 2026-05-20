using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation.Systems
{
	// Applies need decay and body-part damage to all living shroomps.
	// Called in two phases from SimulationCore every SimSystemInterval ticks:
	//   1. Tick()      — need decay + starvation damage (before the vital-organ death check)
	//   2. HealTick()  — passive and Caretaker healing (after the death check)
	// Splitting the phases ensures a just-failed vital organ is never healed above zero
	// before SimulationCore has a chance to detect and process the death.
	// Source: Sporeholm_Systems.md §2; Entities.md §2; Roadmap §2.2
	public static class NeedsSystem
	{
		// v0.5.61 — RimWorld-parity gameplay decay. NeedsSystem.Tick fires
		// every 60 sim ticks (= 1 real second at 1× speed). An in-game day
		// is 24 × 2500 = 60,000 ticks = 1000 real seconds at 1× speed =
		// 1000 NeedsSystem calls per day. So a `target-per-day` of N means
		// per-call decay = N / 1000.
		//
		// Sam: "Shroomps should eat 1-3 times a day depending on traits/age,
		// they should sleep through the night (unless night owl), they
		// should need to regularly take part in recreation to fill joy."
		//
		// Pre-v0.5.61: Nutrition 12/day, Rest 10/day, Joy 5/day, Social
		// 6/day. Hunger cycle was ~8 days, rest cycle ~10 days, joy cycle
		// ~20 days. Needs effectively never bit the gameplay — shroomps
		// could go a week without sleep or food before noticing.
		//
		// Post-v0.5.61 targets (matches RimWorld decay shape):
		//   Nutrition: 100 → 0 in 1 day (80/day decay; default shroomp eats
		//              once per day at the 50-threshold). Glutton trait
		//              bumps this further (see TraitRegistry mods +
		//              personality multipliers in BaseDecay use sites).
		//   Rest: 100 → 0 in 0.9 day (110/day) — pushes shroomps to sleep
		//         nightly. Combined with the v0.5.61 night-hour sleep
		//         gating in SelectTask, shroomps sleep through the night
		//         (22-06 default, flipped for Night Owl).
		//   Joy: 100 → 0 in 2 days (50/day). Joy now bites — shroomps need
		//        recreation every 1-2 days or mood drops.
		//   Social: 100 → 0 in 3 days (33/day). Slower than Joy because
		//           v0.5.60 InteractionTracker passively fills Social
		//           during proximity to other shroomps; the decay is a
		//           floor for isolated shroomps.
		//   MagicResonance: 100 → 0 in 5 days (20/day). Mages still need
		//                   regular Attune but it's less frequent than
		//                   the daily survival cycle.
		//   Safety: 100 → 0 in 20 days (5/day). Rare-trigger gate for
		//           critical SeekSafety; rises fast under threat (raids,
		//           Phase 6 entity proximity — future).
		private static readonly Dictionary<string, float> BaseDecay = new()
		{
			["Nutrition"]      = 0.080f,   // 80/day → 1 day cycle
			["Rest"]           = 0.110f,   // 110/day → 0.9 day cycle (sleep nightly)
			["Social"]         = 0.033f,   // 33/day → 3 day cycle (interactions help)
			["MagicResonance"] = 0.020f,   // 20/day → 5 day cycle
			["Safety"]         = 0.005f,   // 5/day → 20 day cycle (critical-only)
			["Joy"]            = 0.050f,   // 50/day → 2 day cycle (regular recreation)
		};

		// Starvation body-part damage per NeedsSystem call (called ~16.7×/in-game day).
		// Stomach: 2.5/call → ~100%/2.4 days (visible early warning).
		// Liver:   1.7/call → ~100%/3.5 days (vital — triggers death check in SimulationCore).
		private const float StarvStomach = 2.5f;
		private const float StarvLiver   = 1.7f;

		// Non-vital passive heal rate per call (~30 in-game days to recover from 0%).
		// Vital parts do not heal passively; a Caretaker is required.
		// v0.5.84r — natural biological healing. Sam: "Remove the flat heal.
		// We want to implement a natural 'biological' healing factor across
		// all pawns that will heal them slowly (to allow recovering from
		// sickness or wounds)." Settled on DF-slow ~3 condition/day per
		// playtest pref. HealTick fires once per real second; at default
		// sim speed an in-game day ≈ 180 sec, so 3/180 ≈ 0.017/tick for
		// the non-vital baseline. Vital organs heal slower (the body
		// prioritises survival over repair) at ~half the rate. The
		// Caretaker-presence bonus stays as a Phase-7 stub for the real
		// Healer-tending-patient mechanic (rescue downed → carry to bed →
		// Healer treats wounds with medicine items per Roadmap §7.18).
		private const float NaturalHealNonVital  = 0.02f;   // ~3.5 cond / in-game day
		private const float NaturalHealVital     = 0.01f;   // vital heals slower
		private const float HealerPresenceBonus  = 0.02f;   // doubled when colony has any Caretaker — Phase 7 Healer system replaces with per-patient tending

		// Phase 1: need decay + starvation damage. Must run before the vital-organ death
		// check in SimulationCore so any organ that hits 0 is visible to the death check.
		public static void Tick(IReadOnlyList<Shroomp> shroomps, int foodCapacity)
		{
			int livingCount = 0;
			foreach (var s in shroomps)
				if (s.IsAlive) livingCount++;

			// Population pressure: overfull colonies degrade nutrition faster.
			float nutritionPressure = 1f;
			if (livingCount > foodCapacity)
			{
				float excess = (float)(livingCount - foodCapacity) / Math.Max(1, foodCapacity);
				nutritionPressure = 1f + excess * 0.5f;
			}

			foreach (var s in shroomps)
			{
				if (!s.IsAlive) continue;

				s.Nutrition      = Clamp(s.Nutrition      - BaseDecay["Nutrition"]      * nutritionPressure * RoleDecayMod(s, "Nutrition")      * LifeStageDecayMod(s, "Nutrition")      * TraitRegistry.GetNeedDecayMod(s, "Nutrition")      * PersonalityDecayMod(s, "Nutrition"));
				s.Rest           = Clamp(s.Rest           - BaseDecay["Rest"]           * RoleDecayMod(s, "Rest")           * LifeStageDecayMod(s, "Rest")           * TraitRegistry.GetNeedDecayMod(s, "Rest")           * PersonalityDecayMod(s, "Rest"));
				s.Social         = Clamp(s.Social         - BaseDecay["Social"]         * RoleDecayMod(s, "Social")         * LifeStageDecayMod(s, "Social")         * TraitRegistry.GetNeedDecayMod(s, "Social"));
				s.MagicResonance = Clamp(s.MagicResonance - BaseDecay["MagicResonance"] * RoleDecayMod(s, "MagicResonance") * LifeStageDecayMod(s, "MagicResonance") * TraitRegistry.GetNeedDecayMod(s, "MagicResonance"));
				s.Safety         = Clamp(s.Safety         - BaseDecay["Safety"]         * RoleDecayMod(s, "Safety")         * LifeStageDecayMod(s, "Safety")         * TraitRegistry.GetNeedDecayMod(s, "Safety"));
				// v0.4.63 (G4) — Joy decays unmodified by role/lifestage/trait
				// for now. Idle tasks restore it in BehaviorSystem; the
				// existing idle-tier weighting already biases personality
				// toward more or less idle activity, which serves as the
				// implicit role/trait mod for Joy.
				s.Joy            = Clamp(s.Joy            - BaseDecay["Joy"]);

				// Starvation: zero nutrition degrades Stomach (warning) then Filter (fatal).
				// v0.5.84q — "Liver" renamed to "Filter" in the body-part registry.
				if (s.Nutrition <= 0f)
				{
					DamagePart(s, "Stomach", StarvStomach);
					DamagePart(s, "Filter",  StarvLiver);
				}
			}
		}

		// Phase 2: passive and Caretaker healing. Called from SimulationCore AFTER the
		// vital-organ death check so a Caretaker cannot heal a just-failed organ above
		// zero before death is detected.
		public static void HealTick(IReadOnlyList<Shroomp> shroomps)
		{
			bool hasCaretaker = false;
			foreach (var s in shroomps)
				if (s.IsAlive && s.Role == "Caretaker") { hasCaretaker = true; break; }

			foreach (var s in shroomps)
			{
				if (!s.IsAlive) continue;
				HealParts(s, hasCaretaker);
			}
		}

		private static void DamagePart(Shroomp s, string part, float amount)
		{
			if (s.BodyParts.TryGetValue(part, out float cur))
				s.BodyParts[part] = Math.Clamp(cur - amount, 0f, 100f);
		}

		private static void HealParts(Shroomp s, bool hasCaretaker)
		{
			foreach (var def in BodyPartRegistry.Template)
			{
				if (!s.BodyParts.TryGetValue(def.Name, out float cond)) continue;
				if (cond >= 100f) continue;
				if (cond <= 0f)   continue;   // destroyed parts don't regenerate naturally

				// v0.5.84r — natural healing for ALL parts (vital and non-
				// vital), all the time. Vital parts heal slower; Caretaker
				// presence applies a colony-wide bonus that stands in for
				// the Phase-7 Healer-tending-patient mechanic.
				float rate = def.Vital ? NaturalHealVital : NaturalHealNonVital;
				if (hasCaretaker) rate += HealerPresenceBonus;
				s.BodyParts[def.Name] = Math.Clamp(cond + rate, 0f, 100f);
			}
		}

		// Role modifiers reflect the biological and vocational realities of each role.
		// Source: Sporeholm_Systems.md §7.1
		private static float RoleDecayMod(Shroomp s, string need) => (s.Role, need) switch
		{
			// Forager: outdoor exertion burns calories; exposure increases danger
			("Forager", "Nutrition") => 1.5f,
			("Forager", "Safety")    => 1.4f,

			// Sage: ritual practice sustains attunement; arcane strain disrupts sleep
			("Sage", "MagicResonance") => 0.4f,
			("Sage", "Rest")           => 1.3f,

			// Scholar: intellectual absorption reduces loneliness
			("Scholar", "Social")         => 0.7f,
			("Scholar", "MagicResonance") => 0.8f,

			// Guardian: trained vigilance preserves safety; physical patrols increase hunger
			("Guardian", "Safety")    => 0.3f,
			("Guardian", "Nutrition") => 1.2f,

			// Caretaker: constant community contact limits social decay
			("Caretaker", "Social")    => 0.5f,
			("Caretaker", "Nutrition") => 0.9f,

			// Crafter: physical bench work increases fatigue and caloric need
			("Crafter", "Rest")      => 1.2f,
			("Crafter", "Nutrition") => 1.1f,

			// Elder role: lower metabolism; accumulated wisdom mitigates safety anxiety
			("Elder", "Nutrition") => 0.7f,
			("Elder", "Safety")    => 0.6f,

			_ => 1.0f
		};

		// Life stage modifiers: developmental phase affects baseline need rates.
		// Source: Roadmap §2.2; Entities.md §4.1
		private static float LifeStageDecayMod(Shroomp s, string need) => (s.LifeStage, need) switch
		{
			// Sprouts: still growing; need more Rest and Social support; smaller body = less food
			(LifeStage.Sprout, "Rest")      => 1.30f,
			(LifeStage.Sprout, "Social")    => 1.20f,
			(LifeStage.Sprout, "Nutrition") => 0.85f,

			// Elders: reduced metabolism; wisdom moderates fear; but existential threat awareness rises
			(LifeStage.Elder, "Nutrition") => 0.75f,
			(LifeStage.Elder, "Safety")    => 1.20f,

			// LastSeason: nearly non-functional; reduced drives except existential safety dread
			(LifeStage.LastSeason, "Nutrition") => 0.55f,
			(LifeStage.LastSeason, "Social")    => 0.65f,
			(LifeStage.LastSeason, "Safety")    => 1.45f,

			_ => 1.0f
		};

		// v0.5.61 — personality-trait-driven decay modifiers. Stored on
		// Shroomp.Personality (HashSet<string>) rather than the biological
		// Shroomp.Traits dict. Independent of TraitRegistry.GetNeedDecayMod
		// which only handles biological traits.
		//   Glutton:    +50 % Nutrition decay → eats ~2× per day instead
		//               of 1× (matches RimWorld's HungerRate trait family)
		//   Sleepyhead: +35 % Rest decay → needs more sleep; combined
		//               with the existing 60-threshold sleep trigger,
		//               sleepyheads nap during day too
		//   Brawny:     +25 % Nutrition decay (large body, high caloric
		//               needs)
		private static float PersonalityDecayMod(Shroomp s, string need)
		{
			if (s.Personality == null || s.Personality.Count == 0) return 1f;
			float mod = 1f;
			if (need == "Nutrition")
			{
				if (s.Personality.Contains("Glutton")) mod *= 1.50f;
				if (s.Personality.Contains("Brawny"))  mod *= 1.25f;
			}
			else if (need == "Rest")
			{
				if (s.Personality.Contains("Sleepyhead")) mod *= 1.35f;
				if (s.Personality.Contains("Brawny"))     mod *= 1.15f;   // physical effort
			}
			return Math.Clamp(mod, 0.10f, 3.0f);
		}

		private static float Clamp(float v) => Math.Clamp(v, 0f, 100f);
	}
}
