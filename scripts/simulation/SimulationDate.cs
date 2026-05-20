namespace Sporeholm.Simulation
{
	public enum Season { Spring, Summer, Autumn, Winter }

	// Tracks the in-game S.D. (Sporo Domini) calendar.
	// All sessions begin at Year 0 S.D. regardless of chosen era.
	public struct SimulationDate
	{
		// 2,500 ticks = 1 in-game hour (always, at any speed multiplier).
		public const int TicksPerHour = 2_500;

		public int Year;
		public Season Season;
		public int Day;         // 1–30
		public int Hour;        // 0–23
		public int TickOfHour;  // 0–2499

		public static SimulationDate Zero => new()
		{
			Year = 0, Season = Season.Spring, Day = 1, Hour = 6, TickOfHour = 0
		};

		// Called every simulation tick — the primary time-advancement entry point.
		public void AdvanceTick()
		{
			TickOfHour++;
			if (TickOfHour >= TicksPerHour)
			{
				TickOfHour = 0;
				AdvanceHour();
			}
		}

		public void AdvanceHour()
		{
			Hour = (Hour + 1) % 24;
			if (Hour == 0) AdvanceDay();
		}

		public void AdvanceDay()
		{
			Day++;
			if (Day > 30)
			{
				Day = 1;
				Season = (Season)(((int)Season + 1) % 4);
				if (Season == Season.Spring)
					Year++;
			}
		}

		public override string ToString() => $"Hour {Hour}  Day {Day}, {Season}, Year {Year} S.D.";
		public string ToShortString() => $"Y{Year} {Season.ToString()[..2]} D{Day} {Hour:D2}h";
	}
}
