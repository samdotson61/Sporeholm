using System;
using System.Collections.Generic;

namespace Sporeholm.Simulation
{
    // v0.5.60 — RimWorld-parity general reservation system.
    //
    // Pre-v0.5.60 we had ad-hoc claim dicts scattered across multiple
    // systems: LocalMap._claims (per-tile, designations + blueprints),
    // HaulSystem._reservations (per-item haul reservation), HaulSystem
    // ._priorityHauls (per-item priority flag). Each had its own release
    // path that drifted out of sync — v0.5.59 discovered Build was
    // missing from ReleaseTaskClaim's eligible-types list, leaking
    // blueprint claims forever.
    //
    // ReservationManager is RimWorld's pattern: layered reservations
    // keyed by (target, claimant, layer). Stops the per-system claim-
    // dict proliferation. Phase 6 needs this for entity targeting
    // (multiple shroomps hunting the same boar without race conditions)
    // and Phase 7 combat (multiple Guardians attacking one Tasmanian
    // Mauler from different sides).
    //
    // v0.5.60 scope: additive only. The new manager exists alongside
    // LocalMap._claims (which keeps doing designation tile claims).
    // Build claims migrate here. Future versions can migrate the
    // other claim dicts.
    //
    // Thread safety: all public methods take the internal lock. Safe
    // to call from the sim thread (BehaviorSystem) and the main thread
    // (UI showing reservation state).
    public sealed class ReservationManager
    {
        // A reservation target is identified by a string key. We use:
        //   "tile:X,Y"          — tile reservation (build sites, work targets)
        //   "item:Guid"         — item reservation (haul targets)
        //   "shroomp:Guid"        — shroomp reservation (interactions, medical)
        // Layers let multiple kinds of reservations stack on one target:
        //   "build"             — frame reservation (the constructor)
        //   "haul"              — material haul reservation
        //   "interact"          — social interaction lock
        //   "medical"           — caretaker treatment slot
        public readonly struct Key : IEquatable<Key>
        {
            public readonly string Target;
            public readonly string Layer;
            public Key(string target, string layer) { Target = target; Layer = layer; }
            public bool Equals(Key other) => Target == other.Target && Layer == other.Layer;
            public override bool Equals(object? obj) => obj is Key k && Equals(k);
            public override int GetHashCode() => HashCode.Combine(Target, Layer);
        }

        private readonly Dictionary<Key, Guid> _holders = new();
        private readonly object _lock = new();

        // Returns true if the reservation succeeds (target+layer was free
        // OR already held by claimer). False when another claimer owns it.
        public bool Reserve(string target, string layer, Guid claimerId)
        {
            var key = new Key(target, layer);
            lock (_lock)
            {
                if (_holders.TryGetValue(key, out var owner))
                    return owner == claimerId;
                _holders[key] = claimerId;
                return true;
            }
        }

        // Release the reservation if it's held by claimerId. No-op
        // otherwise (safer than failing — abandoned reservations get
        // garbage-collected by ClearStaleForClaimant when a shroomp dies).
        public void Release(string target, string layer, Guid claimerId)
        {
            var key = new Key(target, layer);
            lock (_lock)
            {
                if (_holders.TryGetValue(key, out var owner) && owner == claimerId)
                    _holders.Remove(key);
            }
        }

        // Force-clear a reservation regardless of claimer. Used when the
        // underlying target becomes invalid — designation removed, item
        // destroyed, bed demolished — and no shroomp should be claiming it
        // any more. Different semantics from Release (which is "I'm done
        // with this") — ForceRelease is "this target no longer exists."
        public void ForceRelease(string target, string layer)
        {
            var key = new Key(target, layer);
            lock (_lock) _holders.Remove(key);
        }

        public void ForceReleaseTile(int x, int y, string layer) =>
            ForceRelease(TileKey(x, y), layer);

        public void ForceReleaseItem(System.Guid itemId, string layer) =>
            ForceRelease(ItemKey(itemId), layer);

        // Returns the claimer Id holding this (target, layer), or null
        // if unclaimed. Used by find-target methods to skip claimed
        // entries owned by other shroomps.
        public Guid? GetHolder(string target, string layer)
        {
            var key = new Key(target, layer);
            lock (_lock)
            {
                return _holders.TryGetValue(key, out var owner) ? owner : null;
            }
        }

        // Convenience: is this target+layer reserved by someone OTHER
        // than the passed claimerId? Used in find-target loops.
        public bool IsReservedByOther(string target, string layer, Guid claimerId)
        {
            var h = GetHolder(target, layer);
            return h.HasValue && h.Value != claimerId;
        }

        // Garbage-collect all reservations held by a specific claimer.
        // Called when a shroomp dies, leaves the colony, or is reset.
        // Without this, dead shroomps would hold reservations indefinitely
        // and block work assignment.
        public int ClearStaleForClaimant(Guid claimerId)
        {
            int removed = 0;
            lock (_lock)
            {
                var stale = new List<Key>();
                foreach (var kv in _holders)
                    if (kv.Value == claimerId) stale.Add(kv.Key);
                foreach (var k in stale) { _holders.Remove(k); removed++; }
            }
            return removed;
        }

        // Snapshot for save/debug. Returns a copy of every active
        // reservation. Save format can persist this so reservations
        // survive game reloads (matches RimWorld behaviour where
        // reservations are part of the save).
        public IReadOnlyList<(string Target, string Layer, Guid Claimant)> Snapshot()
        {
            lock (_lock)
            {
                var result = new List<(string, string, Guid)>(_holders.Count);
                foreach (var kv in _holders)
                    result.Add((kv.Key.Target, kv.Key.Layer, kv.Value));
                return result;
            }
        }

        public void Clear()
        {
            lock (_lock) _holders.Clear();
        }

        // ── Tile-keyed convenience helpers ─────────────────────────────────
        // Most reservations in v0.5.60 are tile-based (build sites, work
        // targets). These wrap the string-key API for common cases so
        // callers don't repeat the "tile:X,Y" formatting.
        public static string TileKey(int x, int y) => $"tile:{x},{y}";

        public bool ReserveTile(int x, int y, string layer, Guid claimerId) =>
            Reserve(TileKey(x, y), layer, claimerId);

        public void ReleaseTile(int x, int y, string layer, Guid claimerId) =>
            Release(TileKey(x, y), layer, claimerId);

        public bool IsTileReservedByOther(int x, int y, string layer, Guid claimerId) =>
            IsReservedByOther(TileKey(x, y), layer, claimerId);

        // Reservation layer names (string constants — switch to enum if
        // the list grows beyond ~10 entries).
        public const string LayerBuildFrame = "build_frame";   // the constructor finishing the frame
        public const string LayerBuildHaul  = "build_haul";    // material delivery (multi-claim allowed)
        public const string LayerInteract   = "interact";      // social-interaction lock
        public const string LayerMedical    = "medical";       // caretaker treatment slot
        // v0.5.61 — added during the old-systems migration.
        public const string LayerWork       = "work";          // tile work claims (Gather/Excavate/Chop/Cut/Build)
        public const string LayerHaul       = "haul";          // item haul reservations (replaces HaulSystem._reservations)

        // v0.5.61 — singleton-style access for static-API callers that
        // can't easily get a LocalMap reference. SimulationCore sets this
        // to the bound map's Reservations instance when the map is
        // attached. HaulSystem static methods read from it.
        // Null-safe: if no map is currently bound (game start, between
        // sessions), reservation operations no-op cleanly.
        // Mirrors RimWorld's Find.CurrentMap.reservationManager pattern
        // — RimWorld has multiple maps but only one is "current" for the
        // player at a time.
        public static ReservationManager? Active { get; set; }

        // Item-keyed convenience (Guid-based, used by HaulSystem migration).
        public static string ItemKey(System.Guid itemId) => $"item:{itemId}";

        public bool ReserveItem(System.Guid itemId, string layer, Guid claimerId) =>
            Reserve(ItemKey(itemId), layer, claimerId);

        public void ReleaseItem(System.Guid itemId, string layer, Guid claimerId) =>
            Release(ItemKey(itemId), layer, claimerId);

        public bool IsItemReservedByOther(System.Guid itemId, string layer, Guid claimerId) =>
            IsReservedByOther(ItemKey(itemId), layer, claimerId);

        public Guid? GetItemHolder(System.Guid itemId, string layer) =>
            GetHolder(ItemKey(itemId), layer);
    }
}
