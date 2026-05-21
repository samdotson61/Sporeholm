namespace Sporeholm.Simulation
{
    // v0.3.46 — shared "what is this shroomp doing?" verb table. Lives next
    // to BehaviorTask.cs so any new TaskType added here gets a verb added
    // in one place rather than fanning out across the UI. Two existing
    // consumers as of v0.3.46: ShroompRosterPanel's Activity column and
    // ShroompCardPanel's "Name — Activity" header.
    public static class TaskVerb
    {
        public static string Of(TaskType t) => t switch
        {
            // Tier 1 — critical needs
            TaskType.Eat            => "Eating",
            TaskType.Sleep          => "Sleeping",
            TaskType.Socialize      => "Socialising",
            TaskType.Attune         => "Attuning",
            TaskType.SeekSafety     => "Seeking safety",
            TaskType.Heal           => "Healing",
            // Tier 2 — role + designation work
            TaskType.GatherFood     => "Gathering food",
            TaskType.GatherMaterial => "Excavating",
            TaskType.ChopWood       => "Chopping wood",
            TaskType.CutVegetation  => "Cutting plants",
            // v0.4.0 — Phase-5-deferred stubs. Verbs included so the
            // roster column doesn't read "—" if a future system happens
            // to assign one of these task types early.
            TaskType.Haul           => "Hauling",
            TaskType.Cook           => "Cooking",
            TaskType.DoBill         => "Crafting",   // v0.5.84s — Phase 5.5 bills
            TaskType.Build          => "Building",
            TaskType.BuildHaul      => "Hauling materials",   // v0.5.60
            TaskType.Demolish       => "Demolishing",   // v0.6.2 — demolish-as-task
            TaskType.Research       => "Researching",
            TaskType.Guard          => "Guarding",
            // Tier 3 — idle (v0.3.43 rewrite)
            TaskType.Wander         => "Wandering",
            TaskType.Loiter         => "Loitering",
            TaskType.Observe        => "Observing",
            TaskType.Converse       => "Chatting",
            TaskType.Meditate       => "Meditating",
            TaskType.VisitFavorite  => "Visiting a favourite spot",
            // Tier 0 — player override
            TaskType.PlayerOrder    => "On orders",
            TaskType.None           => "Idle",
            _                       => "—",
        };
    }
}
