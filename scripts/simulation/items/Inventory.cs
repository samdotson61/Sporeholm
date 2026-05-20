using System.Collections.Generic;

namespace Sporeholm.Simulation.Items
{
    // v0.3.46 (Phase 4 core) — read-only snapshot row used by UI consumers
    // on the main thread. Built under the inventory lock so values are
    // self-consistent at the moment of snapshot.
    public readonly record struct InventoryRow(
        ItemKind   Kind,
        string     SubType,
        string     MaterialFamily,
        string     MaterialSubType,
        Quality    Quality,
        ItemState  State,
        int        Quantity,
        float      AvgCondition,
        float      DurabilityCap,
        long       AvgBirthTick);

    // v0.3.46 (Phase 4 core) — colony-wide inventory of items. Replaces
    // the float ledger (ColonyResources.Food / Stone / Wood / Magic) as
    // the source of truth; the floats stay as derived aggregates so
    // existing HUD/UI code continues to compile while the underlying
    // model is items.
    //
    // No designated stockpile zones yet — Phase 5 introduces those, and
    // Haul tasks at the same time. For now the inventory is a flat list
    // of stacks; items are added on harvest and removed on consumption
    // without any spatial location.
    //
    // Thread model: sim thread is the sole writer (Add / Consume /
    // TickDeterioration). Main thread reads via Snapshot() which copies
    // each item's display data into a flat list of InventoryRow value
    // structs under `_lock`. Reading the float totals (TotalByKind etc.)
    // also takes the lock so the count is consistent with whatever the
    // sim thread last finished writing.
    public sealed class Inventory
    {
        private readonly List<Item> _items = new();
        private readonly object _lock = new();

        public IReadOnlyList<Item> Items => _items;

        // Add an item — merges into an existing matching stack if one
        // exists and the kind is stackable; otherwise appends.
        public void Add(Item item)
        {
            if (item == null || item.Quantity <= 0) return;
            lock (_lock)
            {
                if (ItemKindMeta.IsStackable(item.Kind))
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        if (_items[i].CanStackWith(item))
                        {
                            _items[i].Absorb(item);
                            return;
                        }
                    }
                }
                _items.Add(item);
            }
        }

        // Total quantity across all items matching a top-level Kind.
        // Used by ColonyResources's compatibility float properties.
        public int TotalByKind(ItemKind kind)
        {
            int sum = 0;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                    if (_items[i].Kind == kind) sum += _items[i].Quantity;
            }
            return sum;
        }

        // Total quantity across items matching a (Kind, MaterialFamily).
        public int TotalByFamily(ItemKind kind, string materialFamily)
        {
            int sum = 0;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Kind == kind && it.Material.Family == materialFamily)
                        sum += it.Quantity;
                }
            }
            return sum;
        }

        // Total quantity across items matching (Kind, SubType).
        public int TotalBySubType(ItemKind kind, string subType)
        {
            int sum = 0;
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Kind == kind && it.SubType == subType) sum += it.Quantity;
                }
            }
            return sum;
        }

        // Main-thread snapshot of the entire inventory. Returns a flat list
        // of InventoryRow value structs that the UI can walk without
        // worrying about concurrent sim-thread writes. Allocates one
        // List per call; called at HUD refresh rate (60 Hz) so cheap.
        public InventoryRow[] Snapshot()
        {
            lock (_lock)
            {
                var rows = new InventoryRow[_items.Count];
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    rows[i] = new InventoryRow(
                        it.Kind, it.SubType,
                        it.Material.Family, it.Material.SubType,
                        it.Quality, it.State,
                        it.Quantity, it.AvgCondition, it.DurabilityCap,
                        it.AvgBirthTick);
                }
                return rows;
            }
        }

        // Find the best food stack to eat. Preference order:
        //   1. Fresh items with the highest nutrition × quality score.
        //   2. Stale items (×0.6 score).
        //   3. Spoiled items (×0.3 score) — only when `allowSpoiled` (starving).
        // The Shroomp's per-shroomp food preferences (Preferences.LikedItems)
        // bump the score by +50 % so liked foods win the tiebreak.
        // v0.5.68 — `allowSpoiled` widens the eligible set for starving pawns.
        // RimWorld parity: FoodUtility.TryFindBestFoodSourceFor falls back to
        // rotten food when the pawn's hunger crosses the Urgent threshold.
        public Item? FindBestFood(Shroomp eater, bool allowSpoiled = false)
        {
            lock (_lock)
            {
                Item? best = null;
                float bestScore = float.MinValue;
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Kind != ItemKind.Food) continue;
                    if (it.Quantity <= 0) continue;
                    if (it.State == ItemState.Spoiled && !allowSpoiled) continue;
                    if (it.IsForbidden) continue;
                    var def = ItemRegistry.Get(it.Kind, it.SubType);
                    if (def == null) continue;
                    float score = def.BaseNutrition * QualityMeta.NutritionMul(it.Quality);
                    if (it.State == ItemState.Stale) score *= 0.6f;
                    else if (it.State == ItemState.Spoiled) score *= 0.3f;
                    if (eater?.Preferences != null && eater.Preferences.LikesItem(it.SubType))
                        score *= 1.5f;
                    if (eater?.Preferences != null && eater.Preferences.DislikesItem(it.SubType))
                        score *= 0.5f;
                    if (score > bestScore) { bestScore = score; best = it; }
                }
                return best;
            }
        }

        // v0.5.22 (Phase 5E) — find the first non-empty Item stack matching
        // (Kind, SubType-exclusion-list). Used by CookSystem.Apply to
        // pluck a raw Food stack while excluding already-prepared meals.
        // Returns null if no matching stack exists. Caller can then use
        // Consume(stack, n) to deduct.
        public Item? FindFirst(ItemKind kind, params string[] excludeSubTypes)
        {
            lock (_lock)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Kind != kind) continue;
                    if (it.Quantity <= 0) continue;
                    if (System.Array.IndexOf(excludeSubTypes, it.SubType) >= 0) continue;
                    return it;
                }
                return null;
            }
        }

        // v0.5.19 (Phase 5B) — bulk consume across all stacks of a given
        // (Kind, MaterialFamily). Walks every stack, takes from the
        // smallest-Quantity stacks first (so stacks empty cleanly without
        // leaving a single residual unit behind), and stops once `amount`
        // units have been removed. Returns the actual amount removed —
        // less than `amount` when the inventory ran out.
        //
        // Used by BehaviorSystem.ApplyTaskEffect's TaskType.Build case to
        // consume Stone or Wood materials when a Crafter completes a
        // structure. The "smallest-stacks-first" ordering matches the
        // Phase-5 stockpile model where stacks live on tiles — emptying
        // small stacks first keeps the on-map item count visually clean.
        public int ConsumeByFamily(ItemKind kind, string materialFamily, int amount)
            => ConsumeByMaterial(kind, materialFamily, subType: null, amount);

        // v0.5.84t — any-family consume: matches every stack of the given
        // ItemKind regardless of MaterialFamily. Used by recipes that accept
        // "any X kind" inputs (e.g. CookMeal takes any Food — Plant, Magic,
        // future Meat, etc.). Same smallest-stack-first ordering as the
        // family-strict variant. Returns the actual units removed.
        public int ConsumeByKind(ItemKind kind, int amount)
        {
            if (amount <= 0) return 0;
            int taken = 0;
            lock (_lock)
            {
                var matches = new System.Collections.Generic.List<Item>();
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Kind != kind) continue;
                    if (it.Quantity > 0) matches.Add(it);
                }
                matches.Sort((a, b) => a.Quantity.CompareTo(b.Quantity));
                foreach (var stack in matches)
                {
                    if (taken >= amount) break;
                    int take = System.Math.Min(amount - taken, stack.Quantity);
                    stack.Quantity -= take;
                    taken += take;
                }
                _items.RemoveAll(it => it.Quantity <= 0);
            }
            return taken;
        }

        // v0.5.43 — material-strict consume. When `subType` is non-null, only
        // stacks whose `Material.SubType` matches are eligible — so a
        // "FungalWood Wall" blueprint will consume FungalWood logs
        // specifically rather than any wood. Pre-v0.5.43 build cost
        // resolved through ConsumeByFamily (Wood-family broad-match), so
        // the player's material picker only affected the rendered tint,
        // not the consumed material — Sam: "nothing using the correct
        // materials can be built." Now: blueprint Material → subType-
        // strict consume → if the colony has no logs of that exact
        // material, the build stalls (caller falls back per existing
        // material-shortfall behaviour in BehaviorSystem.Build apply).
        public int ConsumeByMaterial(ItemKind kind, string materialFamily, string? subType, int amount)
        {
            if (amount <= 0) return 0;
            int taken = 0;
            lock (_lock)
            {
                var matches = new System.Collections.Generic.List<Item>();
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it.Kind != kind) continue;
                    if (it.Material.Family != materialFamily) continue;
                    if (subType != null && it.Material.SubType != subType) continue;
                    if (it.Quantity > 0) matches.Add(it);
                }
                matches.Sort((a, b) => a.Quantity.CompareTo(b.Quantity));
                foreach (var stack in matches)
                {
                    if (taken >= amount) break;
                    int take = System.Math.Min(amount - taken, stack.Quantity);
                    stack.Quantity -= take;
                    taken += take;
                }
                _items.RemoveAll(it => it.Quantity <= 0);
            }
            return taken;
        }

        // Consume one unit (or more) from a specific stack. Returns the
        // number of units actually removed (always ≤ requested). If the
        // stack drops to zero, the entry is removed from the list.
        public int Consume(Item stack, int amount = 1)
        {
            if (stack == null || amount <= 0) return 0;
            lock (_lock)
            {
                int idx = _items.IndexOf(stack);
                if (idx < 0) return 0;
                int take = System.Math.Min(amount, stack.Quantity);
                stack.Quantity -= take;
                if (stack.Quantity <= 0) _items.RemoveAt(idx);
                return take;
            }
        }

        // For deterioration: walk every item, apply per-tick decay.
        // Returns true if any item's State changed (so callers can flag
        // a UI refresh).
        public bool TickDeterioration(long globalTick, float daysElapsed,
            float temperatureMul = 1f, float insulationMul = 1f)
        {
            bool anyStateChange = false;
            lock (_lock)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    var it = _items[i];
                    var matDef = MaterialRegistry.Get(it.Material);
                    // v0.4.0 — multi-axis decay (per Phase 4 spec):
                    //   Material × Temperature × Insulation × baseline
                    // Material is live today. Temperature and Insulation
                    // default to 1.0 (no-op) until Phase 5 roofs and Phase
                    // 10 weather feed real values into TickDay.
                    float decayPerDay = (matDef?.DecayRateMul ?? 1f) * 1.5f
                                      * temperatureMul * insulationMul;
                    it.AvgCondition -= decayPerDay * daysElapsed;
                    if (it.AvgCondition < 0) it.AvgCondition = 0;
                    var newState = ItemStateMeta.FromCondition(it.AvgCondition, it.DurabilityCap);
                    if (newState != it.State)
                    {
                        it.State = newState;
                        anyStateChange = true;
                    }
                    // Spoiled food drops out — RimWorld throws it on the floor;
                    // we just delete it for now. Spoiled non-food stays around
                    // (Broken tools, etc.).
                    if (it.Kind == ItemKind.Food && it.State == ItemState.Spoiled)
                    {
                        _items.RemoveAt(i);
                        anyStateChange = true;
                    }
                }
            }
            return anyStateChange;
        }

        public void Clear()
        {
            lock (_lock) _items.Clear();
        }
    }
}
