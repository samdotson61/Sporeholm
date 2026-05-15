using System;

namespace SmurfulationC.Simulation
{
	// Configurable tick rate controller.
	// Base rate: 1 tick = 1 in-game day = 60,000 ms real time (1 real minute).
	// All speed presets are multipliers of this base.
	public class SimulationClock
	{
		// 60 ticks per real second at 1× speed → ~16.67 ms per tick.
		public const int BaseTickIntervalMs = 17;

		private float _speedMultiplier = 1.0f;
		private bool _paused = false;
		private readonly object _lock = new();

		public float SpeedMultiplier
		{
			get { lock (_lock) return _speedMultiplier; }
			set { lock (_lock) _speedMultiplier = Math.Clamp(value, 0.01f, 200f); }
		}

		public bool Paused
		{
			get { lock (_lock) return _paused; }
			set { lock (_lock) _paused = value; }
		}

		// Returns -1 when paused; otherwise ms to sleep between ticks.
		public int CurrentTickIntervalMs
		{
			get
			{
				lock (_lock)
				{
					if (_paused) return -1;
					return Math.Max(1, (int)(BaseTickIntervalMs / _speedMultiplier));
				}
			}
		}

		// Whether the simulation is slow enough to warrant intra-day hour simulation (<=1x).
		public bool IntraDayMode
		{
			get { lock (_lock) return !_paused && _speedMultiplier <= 1.0f; }
		}
	}
}
