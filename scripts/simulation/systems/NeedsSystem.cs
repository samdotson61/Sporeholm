using System;
using System.Collections.Generic;

namespace SmurfulationC.Simulation.Systems
{
	// Applies need decay and body-part damage to all living smurfs.
	// Called in two phases from SimulationCore every SimSystemInterval ticks:
	//   1. Tick()      — need decay + starvation damage (before the vital-organ death check)
	//   2. HealTick()  — passive and Caretaker healing (after the death check)
	// Splitting the phases ensures a just-failed vital organ is never healed above zero
	// before SimulationCore has a chance to detect and process the death.
	// Source: SmurfulationC_Systems.md §2; Entities.md §2; Roadmap §2.2
	public static class NeedsSystem
	{
		// Biological baseline decay per systems call (= target-per-day / 1000).
		// Per-day targets calibrated for a ~3-day hunger cycle, ~4-day rest cycle
		// from a starting value of 80 down to the Stressed threshold at 40.
		private static readonly Dictionary<string, float> BaseDecay = new()
		{
			["Nutrition"]      = 0.012f,
			["Rest"]           = 0.010f,
			["Social"]         = 0.006f,
			["MagicResonance"] = 0.004f,
			["Safety"]         = 0.001f,
			// v0.4.63 (G4) — Joy decays slower than Social. Calibrated so
			// a smurf doing nothing but work for ~5 in-game days drifts
			// from 100 → 40 (Stressed-tier) and starts firing low-Joy
			// thoughts. Existing idle tasks restore Joy in
			// BehaviorSystem.ApplyTaskEffect.
			["Joy"]            = 0.005f,
		};

		// Starvation body-part damage per NeedsSystem call (called ~16.7×/in-game day).
		// Stomach: 2.5/call → ~100%/2.4 days (visible early warning).
		// Liver:   1.7/call → ~100%/3.5 days (vital — triggers death check in SimulationCore).
		private const float StarvStomach = 2.5f;
		private const float StarvLiver   = 1.7f;

		// Non-vital passive heal rate per call (~30 in-game days to recover from 0%).
		// Vital parts do not heal passively; a Caretaker is required.
		private const float PassiveHealNonVital  = 0.1f;
		private const float CaretakerHealNonVital = 0.3f;
		private const float CaretakerHealVital    = 0.1f;

		// Phase 1: need decay + starvation damage. Must run before the vital-organ death
		// check in SimulationCore so any organ that hits 0 is visible to the death check.
		public static void Tick(IReadOnlyList<Smurf> smurfs, int foodCapacity)
		{
			int livingCount = 0;
			foreach (var s in smurfs)
				if (s.IsAlive) livingCount++;

			// Population pressure: overfull colonies degrade nutrition faster.
			float nutritionPressure = 1f;
			if (livingCount > foodCapacity)
			{
				float excess = (float)(livingCount - foodCapacity) / Math.Max(1, foodCapacity);
				nutritionPressure = 1f + excess * 0.5f;
			}

			foreach (var s in smurfs)
			{
				if (!s.IsAlive) continue;

				s.Nutrition      = Clamp(s.Nutrition      - BaseDecay["Nutrition"]      * nutritionPressure * RoleDecayMod(s, "Nutrition")      * LifeStageDecayMod(s, "Nutrition")      * TraitRegistry.GetNeedDecayMod(s, "Nutrition"));
				s.Rest           = Clamp(s.Rest           - BaseDecay["Rest"]           * RoleDecayMod(s, "Rest")           * LifeStageDecayMod(s, "Rest")           * TraitRegistry.GetNeedDecayMod(s, "Rest"));
				s.Social         = Clamp(s.Social         - BaseDecay["Social"]         * RoleDecayMod(s, "Social")         * LifeStageDecayMod(s, "Social")         * TraitRegistry.GetNeedDecayMod(s, "Social"));
				s.MagicResonance = Clamp(s.MagicResonance - BaseDecay["MagicResonance"] * RoleDecayMod(s, "MagicResonance") * LifeStageDecayMod(s, "MagicResonance") * TraitRegistry.GetNeedDecayMod(s, "MagicResonance"));
				s.Safety         = Clamp(s.Safety         - BaseDecay["Safety"]         * RoleDecayMod(s, "Safety")         * LifeStageDecayMod(s, "Safety")         * TraitRegistry.GetNeedDecayMod(s, "Safety"));
				// v0.4.63 (G4) — Joy decays unmodified by role/lifestage/trait
				// for now. Idle tasks restore it in BehaviorSystem; the
				// existing idle-tier weighting already biases personality
				// toward more or less idle activity, which serves as the
				// implicit role/trait mod for Joy.
				s.Joy            = Clamp(s.Joy            - BaseDecay["Joy"]);

				// Starvation: zero nutrition degrades Stomach (warning) then Liver (fatal).
				if (s.Nutrition <= 0f)
				{
					DamagePart(s, "Stomach", StarvStomach);
					DamagePart(s, "Liver",   StarvLiver);
				}
			}
		}

		// Phase 2: passive and Caretaker healing. Called from SimulationCore AFTER the
		// vital-organ death check so a Caretaker cannot heal a just-failed organ above
		// zero before death is detected.
		public static void HealTick(IReadOnlyList<Smurf> smurfs)
		{
			bool hasCaretaker = false;
			foreach (var s in smurfs)
				if (s.IsAlive && s.Role == "Caretaker") { hasCaretaker = true; break; }

			foreach (var s in smurfs)
			{
				if (!s.IsAlive) continue;
				HealParts(s, hasCaretaker);
			}
		}

		private static void DamagePart(Smurf s, string part, float amount)
		{
			if (s.BodyParts.TryGetValue(part, out float cur))
				s.BodyParts[part] = Math.Clamp(cur - amount, 0f, 100f);
		}

		private static void HealParts(Smurf s, bool hasCaretaker)
		{
			foreach (var def in BodyPartRegistry.Template)
			{
				if (!s.BodyParts.TryGetValue(def.Name, out float cond)) continue;
				if (cond >= 100f) continue;

				float rate = def.Vital
					? (hasCaretaker ? CaretakerHealVital    : 0f)
					: (hasCaretaker ? CaretakerHealNonVital : PassiveHealNonVital);

				if (rate > 0f)
					s.BodyParts[def.Name] = Math.Clamp(cond + rate, 0f, 100f);
			}
		}

		// Role modifiers reflect the biological and vocational realities of each role.
		// Source: SmurfulationC_Systems.md §7.1
		private static float RoleDecayMod(Smurf s, string need) => (s.Role, need) switch
		{
			// Forager: outdoor exertion burns calories; exposure increases danger
			("Forager", "Nutrition") => 1.5f,
			("Forager", "Safety")    => 1.4f,

			// Mage: ritual practice sustains attunement; arcane strain disrupts sleep
			("Mage", "MagicResonance") => 0.4f,
			("Mage", "Rest")           => 1.3f,

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
		private static float LifeStageDecayMod(Smurf s, string need) => (s.LifeStage, need) switch
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

		private static float Clamp(float v) => Math.Clamp(v, 0f, 100f);
	}
}
