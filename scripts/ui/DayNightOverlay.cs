using Godot;

// v0.5.58 — RimWorld-parity visual day/night cycle.
//
// Owns a CanvasModulate node and tints the entire world canvas (terrain,
// vegetation, structures, items, shroomps) based on the in-game hour read
// from SimulationManager.GetCurrentDate(). UI layers (CanvasLayer 10/20)
// are unaffected — CanvasModulate only modulates nodes in the same canvas,
// and the HUD / panels live on their own CanvasLayer.
//
// RimWorld reference (wiki blocked WebFetch; design pulled from public
// knowledge of the game). RimWorld:
//   • 24 in-game hours per day
//   • Sun rises ~06:00, peaks ~12:00, sets ~18:00
//   • Smooth color transitions through dawn (warm pink/orange) and dusk
//     (golden) so the player perceives time passing without abrupt jumps
//   • Night is darkened blue-tinted but readable — the player still needs
//     to see what their pawns are doing during nocturnal raids
//
// Our schedule (SimulationDate.Hour 0-23 + TickOfHour for smooth interpolation):
//   00-05  : Night            (dark cool blue)
//   05-07  : Dawn transition  (Night → DawnPeak warm orange)
//   07-08  : Dawn finish      (DawnPeak → Day full white)
//   08-17  : Day              (no tint)
//   17-18  : Golden hour      (Day → DuskPeak orange)
//   18-20  : Dusk transition  (DuskPeak → Night)
//   20-24  : Night            (dark cool blue)
//
// The Sim runs at any speed multiplier and pauses; CanvasModulate just
// reads the current date each frame and applies the matching tint.
//
// Behavior systems (Phase 6 entity AI, Phase 7 combat visibility, Phase 9
// crop growth gating) will read the same hour-of-day directly from
// SimulationDate when those phases land — this overlay is the visual layer
// only, not a coupled source of truth.
public partial class DayNightOverlay : Node
{
	// Keyframe colors. Tuned for readable contrast at night while still
	// reading as "dark."
	private static readonly Color Night    = new(0.30f, 0.35f, 0.55f);   // dark cool blue
	private static readonly Color DawnPeak = new(0.85f, 0.65f, 0.55f);   // warm orange/pink
	private static readonly Color Day      = new(1.00f, 1.00f, 1.00f);   // full daylight
	private static readonly Color DuskPeak = new(0.95f, 0.65f, 0.50f);   // golden hour

	private CanvasModulate _modulate = null!;

	// Injected by GameController after construction. Same pattern as the
	// other Sim-reading overlays (StructureOverlay / ItemDropOverlay).
	public Sporeholm.SimulationManager Sim { get; set; } = null!;

	public override void _Ready()
	{
		_modulate = new CanvasModulate { Name = "DayNightModulate", Color = Day };
		AddChild(_modulate);
	}

	public override void _Process(double delta)
	{
		if (Sim == null) return;
		var date = Sim.GetCurrentDate();
		// Continuous hour 0-24 from Hour + TickOfHour fraction. Lets the
		// tint cross-fade smoothly across the keyframe boundaries instead
		// of snapping at the top of every hour.
		float hf = date.Hour + (float)date.TickOfHour / Sporeholm.Simulation.SimulationDate.TicksPerHour;
		_modulate.Color = TintForHour(hf);
	}

	// Pure function — externally testable. Returns the canvas tint for any
	// continuous in-game hour (0.0–24.0). Outside callers (a future Phase 6
	// entity AI deciding whether to spawn nocturnal predators, etc.) can
	// reuse this without instantiating the overlay.
	public static Color TintForHour(float hf)
	{
		// Wrap into [0, 24) defensively. SimulationDate.Hour is already
		// 0-23 so this is belt-and-suspenders.
		if (hf < 0f) hf += 24f;
		if (hf >= 24f) hf -= 24f;

		if (hf < 5f)  return Night;
		if (hf < 7f)  return Night.Lerp(DawnPeak, (hf - 5f) / 2f);
		if (hf < 8f)  return DawnPeak.Lerp(Day,    hf - 7f);
		if (hf < 17f) return Day;
		if (hf < 18f) return Day.Lerp(DuskPeak,    hf - 17f);
		if (hf < 20f) return DuskPeak.Lerp(Night, (hf - 18f) / 2f);
		return Night;
	}

	// v0.5.58 — convenience for behavior systems: is this hour considered
	// "night" for spawn / sleep / visibility gating? Threshold matches the
	// schedule above: full Night state runs 20-05.
	public static bool IsNightHour(int hour) => hour < 5 || hour >= 20;
}
