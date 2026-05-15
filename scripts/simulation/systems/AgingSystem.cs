namespace SmurfulationC.Simulation.Systems
{
	// Handles age advancement logic.
	// Called from SimulationCore when a smurf's birthday occurs (day-of-year boundary).
	// Source: SmurfulationC_Entities.md §4.1; Roadmap §2.2
	public static class AgingSystem
	{
		// Maximum age in years before a smurf's IsAlive is set false.
		// Sourced from Entities.md §1 ("~550 years") and §4.1 (LastSeason stage at 545+).
		public const int NaturalLifespan = 550;

		public static void AdvanceAge(Smurf s) => s.AgeInYears++;

		// Returns true if the smurf has reached or exceeded natural lifespan.
		public static bool HasExpired(Smurf s) => s.AgeInYears >= NaturalLifespan;
	}
}
