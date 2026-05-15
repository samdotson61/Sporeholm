using Godot;

namespace SmurfulationC.Simulation
{
    // Roadmap §3.2 task type enumeration. Tier 1 (critical needs) maps to Eat /
    // Sleep / Socialize / Attune / SeekSafety; Tier 2 (role tasks) maps to
    // GatherFood / GatherMaterial / Build / Research / Heal / Guard; Tier 3
    // (idle) is Wander. PlayerOrder is a Tier 0 override slot.
    public enum TaskType
    {
        None,
        // Tier 1 — critical needs
        Eat, Sleep, Socialize, Attune, SeekSafety,
        // Tier 2 — role tasks
        GatherFood, GatherMaterial, Build, Research, Heal, Guard,
        // v0.3.38 — new Tier 2 tasks for the Chop Wood / Cut Plants orders.
        // ChopWood targets wood-yielding shrooms (LargeMushroom variants);
        // CutVegetation targets any veg without yielding a resource.
        ChopWood, CutVegetation,
        // v0.4.0 — Phase-5-deferred task stubs. Haul moves items from
        // on-tile drop sites to stockpile zones; Cook combines raw
        // ingredients into prepared meals at a Kitchen building. Both
        // are wired through the SelectTask / ApplyTaskEffect pipeline
        // and route through `TaskVerb.Of` / `WorkPriorityDefaults` so
        // the Jobs tab already exposes them — they just no-op on
        // arrival until the prerequisite buildings exist.
        Haul, Cook,
        // Tier 3 — idle. Wander still exists as the "go somewhere new"
        // baseline; v0.3.43 adds five more idle behaviours so smurfs stop
        // jittering in place between commands:
        //   • Loiter  — drift a tile or two and stand for a while.
        //   • Observe — pick a nearby spot of interest, stand and watch.
        //   • Converse — walk over to another smurf and chat (also boosts Social).
        //   • Meditate — Mages, Daydreamers — stand and boost MagicResonance.
        //   • VisitFavorite — head to a remembered favourite spot.
        Wander, Loiter, Observe, Converse, Meditate, VisitFavorite,
        // Tier 0 — player override
        PlayerOrder,
    }

    // Roadmap §3.2 behavior task carried by each Smurf. Target is in *pixel*
    // space (LocalMap tile size × tile coordinate + half tile) so it's
    // directly comparable with Smurf.SimPos without per-frame conversion.
    //
    // v0.3.36 — converted from a `sealed class` to `readonly record struct`.
    // BehaviorSystem allocated a fresh BehaviorTask every time SelectTask
    // returned (~1 alloc per smurf per tick with v0.3.23's "Wander always
    // re-evaluates"). At 20 smurfs that's ~1200 allocs/sec; at the planned
    // 1000-smurf scale it would be 60k allocs/sec — catastrophic gen-0 GC
    // pressure. As a value-type struct the task lives on the smurf's
    // memory inline; SelectTask returns a struct copy with no heap traffic.
    //
    // `Smurf.CurrentTask` is now `BehaviorTask?` (Nullable<BehaviorTask>).
    // Null checks still work; field reads go through `.Value` or pattern
    // matching (see BehaviorSystem.cs).
    public readonly record struct BehaviorTask
    {
        public TaskType Type           { get; init; }
        public Vector2  Target         { get; init; }
        public float    Priority       { get; init; }
        public bool     IsPlayerOrder  { get; init; }
        public bool     Interruptible  { get; init; }
        public int      TargetTileX    { get; init; }
        public int      TargetTileY    { get; init; }
        public string?  TargetId       { get; init; }
        // v0.3.43 — ticks the smurf should linger at the target after arrival
        // before re-evaluating to a new task. Zero (the default) preserves
        // the prior immediate-re-evaluation behaviour for work tasks.
        // Tier-3 idle tasks set non-zero values via the NewXxx constructors.
        public int      ArrivalLinger  { get; init; }

        // Constructor signature preserved from the v0.3.0 class API so every
        // existing call site (`new BehaviorTask(type, target, priority)`)
        // compiles unchanged. Default parameters provide the missing
        // tail args.
        public BehaviorTask(TaskType type, Vector2 target, float priority,
            bool isPlayerOrder = false, bool interruptible = true,
            int tileX = -1, int tileY = -1, string? targetId = null,
            int arrivalLinger = 0)
        {
            Type          = type;
            Target        = target;
            Priority      = priority;
            IsPlayerOrder = isPlayerOrder;
            Interruptible = interruptible;
            TargetTileX   = tileX;
            TargetTileY   = tileY;
            TargetId      = targetId;
            ArrivalLinger = arrivalLinger;
        }

        public static BehaviorTask Idle => new(TaskType.None, Vector2.Zero, 0f);
    }
}
