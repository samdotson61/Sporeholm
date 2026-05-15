using System.Collections.Generic;

namespace SmurfulationC.Simulation.Items
{
    // v0.4.33 — biographical sidecar carried by `Item` instances of kind
    // `Corpse`. When a smurf dies, `SimulationCore.DropCorpseGear` builds
    // one of these from a `SmurfSnapshot` and attaches it via
    // `Item.CorpseInfo`. The Item itself handles decay (AvgCondition fall
    // → eventual removal) and on-ground rendering through the standard
    // dropped-item pipeline; this record carries the static attributes
    // the unit-info hover surfaces ("Sloppy — Adult Male Forager, died
    // of Starvation 2 days ago").
    //
    // Equipment they were wearing at death is dropped as separate items
    // on the same tile (DF / RimWorld convention) so the player can loot
    // them without picking up the body. We do NOT serialise full Traits /
    // BodyParts / Skills dictionaries onto the corpse — too heavy for a
    // disposable artefact. Personality + handedness are kept because
    // they're cheap and surface naturally in the hover label.
    public sealed record CorpseData(
        string                Name,
        int                   AgeYears,
        Sex                   Sex,
        string                Role,
        CauseOfDeath          Cause,
        long                  DeathTick,
        IReadOnlyList<string> Personality,
        Handedness            Handedness
    );
}
