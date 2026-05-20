namespace Sporeholm.Simulation.Systems
{
	// Handles age advancement logic.
	// Called from SimulationCore when a shroomp's birthday occurs (day-of-year boundary).
	// Source: Sporeholm_Entities.md §4.1; Roadmap §2.2
	public static class AgingSystem
	{
		// Maximum age in years before a shroomp's IsAlive is set false.
		// Sourced from Entities.md §1 ("~550 years") and §4.1 (LastSeason stage at 545+).
		public const int NaturalLifespan = 550;

		public static void AdvanceAge(Shroomp s) => s.AgeInYears++;

		// Returns true if the shroomp has reached or exceeded natural lifespan.
		public static bool HasExpired(Shroomp s) => s.AgeInYears >= NaturalLifespan;
	}
}
