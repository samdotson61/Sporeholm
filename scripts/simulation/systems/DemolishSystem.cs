using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // v0.6.2 — Demolish-as-task. Pre-v0.6.2 the Demolish designation tool
    // instantly cleared a structure and refunded 50% (a flat rate). v0.6.2
    // turns it into a paintable Build-style task:
    //
    //   1. Player paints a built structure with the Demolish tool. The
    //      Demolish dispatch in SimulationManager sets MarkedForDemolition
    //      = true on the StructureSlot (instead of insta-clearing). A red
    //      X overlay appears via StructureOverlay.
    //   2. BehaviorSystem.SelectTask routes Construct-priority shroomps to
    //      DemolishSystem.SelectTarget when no other Build work is queued.
    //   3. The selected shroomp walks to the marked tile and on arrival
    //      DemolishSystem.Apply runs per-tick. Each tick adds Construction-
    //      skill-driven work units to DemolitionProgress.
    //   4. When DemolitionProgress reaches BuildProgressTarget, the tile
    //      clears + the refund items drop on the tile. Refund quantity is
    //      `BuildMaterialCost(type) × refundFraction(constructionSkill)`,
    //      where refundFraction goes 0.20 at lvl 0 → 0.60 at lvl 20 (linear).
    //      Construction XP is granted on completion (60 XP — half a Build
    //      completion's 120 XP since demolition is the easier inverse work).
    //
    // Reachability is gated at SelectTarget (mirrors BillSystem v0.5.83
    // pattern); demolish jobs on unreachable tiles never get assigned.
    public static class DemolishSystem
    {
        // Linear refund curve: 0.20 at skill 0 → 0.60 at skill 20. Matches
        // the spec: "small percentage (20%-60% based on construction skill)".
        public static float RefundFraction(int constructionSkill)
        {
            int clamped = System.Math.Clamp(constructionSkill, 0, 20);
            return 0.20f + clamped * 0.02f;
        }

        // XP granted on completing one demolition.
        public const int DemolitionXp = 60;

        // Scan the map for the nearest MarkedForDemolition built structure
        // the shroomp can reach. Linear walk; acceptable at typical scales.
        public static BehaviorTask? SelectTarget(Shroomp s, LocalMap? map, ColonyResources r)
        {
            if (map == null) return null;
            int sx = (int)(s.SimPos.X / LocalMap.TileSize);
            int sy = (int)(s.SimPos.Y / LocalMap.TileSize);
            int bestDist = int.MaxValue;
            (int X, int Y)? winner = null;
            for (int y = 0; y < map.Height; y++)
            for (int x = 0; x < map.Width;  x++)
            {
                var slot = map.GetStructure(x, y);
                if (!slot.MarkedForDemolition || !slot.IsBuilt) continue;
                int dx = x - sx, dy = y - sy;
                int d = dx * dx + dy * dy;
                if (d >= bestDist) continue;
                // Reachability gate — don't bother walking the structure
                // tile if A* can't get the shroomp there.
                if (!map.AreReachable(sx, sy, x, y)) continue;
                bestDist = d;
                winner = (x, y);
            }
            if (!winner.HasValue) return null;
            var (tx, ty) = winner.Value;
            var px = new Vector2(
                tx * LocalMap.TileSize + LocalMap.TileSize * 0.5f,
                ty * LocalMap.TileSize + LocalMap.TileSize * 0.5f);
            return new BehaviorTask(TaskType.Demolish, px, 55f,
                interruptible: true,
                tileX: tx, tileY: ty);
        }

        // Per-tick work accumulation. When DemolitionProgress reaches the
        // BuildProgressTarget the structure clears + refund drops. If the
        // tile no longer has a marked structure (player cancelled / another
        // demolisher beat us / the structure mutated), the task drops.
        public static void Apply(Shroomp s, BehaviorTask t, LocalMap? map, ColonyResources r)
        {
            if (map == null) return;
            if (t.TargetTileX < 0 || t.TargetTileY < 0) { s.CurrentTask = null; return; }

            var slot = map.GetStructure(t.TargetTileX, t.TargetTileY);
            if (!slot.MarkedForDemolition || !slot.IsBuilt)
            {
                s.CurrentTask = null;
                return;
            }

            // Construction skill drives demolition speed. Mirrors the
            // construction speed curve used by Build (per BehaviorSystem
            // line 4670 BuildProgress increment) — at lvl 0 ≈ 3, lvl 8 ≈
            // 10, lvl 20 ≈ 20 per tick. Demolition is the easier inverse
            // work so we use the same per-tick rate; a 600-target demolish
            // takes ~ 30s @ lvl 0, ~ 5s @ lvl 20.
            int skill = s.Skills.TryGetValue("Construction", out int v) ? v : 0;
            int advance = System.Math.Max(1, 3 + skill);   // 3 + skill: lvl 0 = 3, lvl 20 = 23
            int newProg = slot.DemolitionProgress + advance;
            s.TaskDidWork = true;

            if (newProg < StructureSlot.BuildProgressTarget)
            {
                slot.DemolitionProgress = (ushort)newProg;
                map.SetStructure(t.TargetTileX, t.TargetTileY, slot);
                return;
            }

            // ── Complete the demolition ──
            // Refund items: BuildMaterialCost × RefundFraction(skill).
            int baseCost = StructureSlot.BuildMaterialCost(slot.Type);
            float refundFrac = RefundFraction(skill);
            int refundAmount = System.Math.Max(1, (int)System.Math.Round(baseCost * refundFrac));
            SimulationManager.DropRefundOrCredit(map, t.TargetTileX, t.TargetTileY,
                slot.Material, refundAmount);
            // Clear the structure tile. If the demolished structure was a
            // wall or door, the RoomDetector room flood-fill stays valid
            // because the next room query auto-rebuilds.
            map.SetStructure(t.TargetTileX, t.TargetTileY, StructureSlot.Empty);
            // Construction XP. Half a Build's reward since demolish is the
            // inverse-work (no quality roll, no material decisions).
            SkillRegistry.GainXp(s, "Construction", DemolitionXp);
            s.TaskProgressTicks = 0;
            s.CurrentTask = null;
        }
    }
}
