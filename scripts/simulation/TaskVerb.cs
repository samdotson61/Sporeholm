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
            // v0.7.0 — combat
            TaskType.Attack         => "Fighting",
            TaskType.Flee           => "Fleeing",
            TaskType.TreatPatient   => "Treating the wounded",
            TaskType.Rescue         => "Rescuing the wounded",
            // Tier 3 — idle (v0.3.43 rewrite)
            TaskType.Wander         => "Wandering",
            TaskType.Loiter         => "Loitering",
            TaskType.Observe        => "Observing",
            TaskType.Converse       => "Chatting",
            TaskType.Meditate       => "Meditating",
            TaskType.VisitFavorite  => "Visiting a favourite spot",
            TaskType.Train          => "Training",   // v0.7.2 (Phase 7)
            // Tier 0 — player override
            TaskType.PlayerOrder    => "On orders",
            TaskType.Patrol         => "Patrolling",   // v0.7.3 (N20)
            TaskType.MentalBreak    => "Breaking down",   // v0.7.3 (N8)
            TaskType.PlantCrop      => "Planting",         // v0.8.0 (Phase 8)
            TaskType.HarvestCrop    => "Harvesting crops", // v0.8.0 (Phase 8)
            TaskType.Hunt           => "Hunting",          // v0.8.1 (Phase 8)
            TaskType.Butcher        => "Butchering",       // v0.8.1 (Phase 8)
            TaskType.Tame           => "Taming",           // v0.8.2 (Phase 8)
            TaskType.None           => "Idle",
            _                       => "—",
        };
    }
}
