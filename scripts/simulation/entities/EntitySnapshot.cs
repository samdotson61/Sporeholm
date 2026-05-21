using System;
using Godot;

namespace Sporeholm.Simulation.Entities
{
    // v0.6.0 (Phase 6) — render-thread-safe snapshot of an Entity. Same
    // pattern as ShroompSnapshot: the renderer iterates a read-only list
    // of these and never touches live Entity instances. Snapshots are
    // built once per sim tick from EntitySystem's authoritative state.
    public readonly record struct EntitySnapshot(
        Guid          Id,
        EntityKind    Kind,
        Vector2       SimPos,
        Vector2       SimTarget,
        EntityState   State,
        float         Health,
        float         MaxHealth,
        // v0.6.0 — extended fields so SaveManager can persist directly
        // from the snapshot without a second pass over the live entity
        // list. These are read-only struct copies; no aliasing of mutable
        // sim state.
        float         Speed,
        float         AttackPower,
        bool          IsTamed,
        string?       TamedByName,
        Vector2       WanderHome,
        int           RandomSeed,
        int           AttackCooldownTicks,
        // v0.6.2 — simplified needs + derived mood label for the entity
        // card. MoodLabel is computed from the live Entity at snapshot
        // time (no further data needed by the card).
        float         Nutrition,
        float         Rest,
        string        MoodLabel
    )
    {
        public EntitySnapshot(Entity e) : this(
            e.Id, e.Kind, e.SimPos, e.SimTarget, e.State,
            e.Health, e.MaxHealth,
            e.Speed, e.AttackPower,
            e.IsTamed, e.TamedByName, e.WanderHome, e.RandomSeed, e.AttackCooldownTicks,
            e.Nutrition, e.Rest, e.MoodLabel)
        {
        }
    }
}
