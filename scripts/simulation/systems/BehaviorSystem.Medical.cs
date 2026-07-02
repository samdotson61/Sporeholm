using System;
using System.Collections.Generic;
using Godot;
using Sporeholm.Simulation.Items;
using Sporeholm.World;

namespace Sporeholm.Simulation.Systems
{
    // BehaviorSystem — medical pass (Phase 7, v0.7.1) — doctors tending the wounded in place.
    // One partial of the Shroomp behavior driver; the class overview and
    // architecture notes live in BehaviorSystem.cs.
    public static partial class BehaviorSystem
    {
        // ── v0.7.1 (Phase 7) — Medical pass ───────────────────────────────
        // A Doctor (or Caretaker) walks to the nearest wounded colonist and tends
        // their worst wound in place, consuming a Magic Herb Poultice if the
        // colony has one. Tending marks the wound treated (so HediffSystem heals
        // it ~6× faster), restores part condition, and clears venom. Self-
        // contained movement, like the combat pass.
        private const float TreatRangeTiles       = 1.6f;
        private const int   TreatWorkTicks        = 120;   // ~2s of tending per wound
        private const float TreatConditionRestore = 28f;   // base part condition restored per tend (× quality)

        private static bool TryHandleMedical(Shroomp s, LocalMap? map,
            IReadOnlyList<Shroomp> shroomps, ColonyResources resources, float dt)
        {
            if (!s.IsAlive || s.IsDowned || s.IsBeingCarried) return false;
            if (s.CombatTargetId != null) return false;                       // combat preempts
            if (!(JobPriorityOn(s, "Doctor") || s.Role == "Caretaker")) return false;

            var patient = FindPatient(s, shroomps, map);
            if (patient == null)
            {
                if (s.CurrentTask is { Type: TaskType.TreatPatient })
                { s.CurrentTask = null; s.TaskProgressTicks = 0; }
                return false;
            }

            if (!(s.CurrentTask is { Type: TaskType.TreatPatient }))
            {
                if (s.CurrentTask != null) ReleaseTaskClaim(s, map);
                s.TaskProgressTicks = 0;
            }
            s.CurrentTask = new BehaviorTask(TaskType.TreatPatient, patient.SimPos, 88f,
                interruptible: true, targetId: patient.Id.ToString());

            float rangePx = TreatRangeTiles * LocalMap.TileSize;
            if (s.SimPos.DistanceSquaredTo(patient.SimPos) <= rangePx * rangePx)
            {
                s.SimTarget = s.SimPos;
                s.PathWaypoints.Clear();
                s.PrevSimPos = s.SimPos;
                if (++s.TaskProgressTicks >= TreatWorkTicks)
                {
                    s.TaskProgressTicks = 0;
                    ApplyTend(s, patient, resources);
                }
            }
            else
            {
                CombatStepToward(s, patient.SimPos, map, dt);
            }
            return true;
        }

        private static bool JobPriorityOn(Shroomp s, string cat)
            => s.WorkPriorities != null && s.WorkPriorities.TryGetValue(cat, out var v) && v != 0;

        private static Shroomp? FindPatient(Shroomp doctor, IReadOnlyList<Shroomp> shroomps, LocalMap? map)
        {
            int dx = (int)(doctor.SimPos.X / LocalMap.TileSize);
            int dy = (int)(doctor.SimPos.Y / LocalMap.TileSize);
            Shroomp? best = null; float bd = float.MaxValue;
            for (int i = 0; i < shroomps.Count; i++)
            {
                var p = shroomps[i];
                if (ReferenceEquals(p, doctor) || !p.IsAlive) continue;
                if (p.IsBeingCarried) continue;   // v0.7.2 — let the rescuer get them to a bed first
                if (!NeedsTreatment(p)) continue;
                // Don't lock onto an unreachable patient (across walls / sealed
                // rooms) — the greedy treat-walk can't path there and would freeze.
                if (map != null)
                {
                    int px = (int)(p.SimPos.X / LocalMap.TileSize);
                    int py = (int)(p.SimPos.Y / LocalMap.TileSize);
                    if (!map.IsWorkReachable(dx, dy, px, py)) continue;
                }
                float d2 = doctor.SimPos.DistanceSquaredTo(p.SimPos);
                if (d2 < bd) { bd = d2; best = p; }
            }
            return best;
        }

        private static bool NeedsTreatment(Shroomp p)
        {
            if (p.IsDowned) return true;
            if (p.ComputeHealthPercent() < 60f) return true;
            for (int i = 0; i < p.Hediffs.Count; i++)
                if (!p.Hediffs[i].Tended && p.Hediffs[i].Severity > 8f) return true;
            return false;
        }

        private static void ApplyTend(Shroomp doctor, Shroomp patient, ColonyResources resources)
        {
            // Worst untended wound first (Hediff is a class — mutating sticks).
            Combat.Hediff? worst = null;
            for (int i = 0; i < patient.Hediffs.Count; i++)
            {
                var h = patient.Hediffs[i];
                if (h.Tended) continue;
                if (worst == null || h.Severity > worst.Severity) worst = h;
            }

            int docHealing = (doctor.Skills != null && doctor.Skills.TryGetValue("Healing", out var hl)) ? hl : 0;
            // Only spend a poultice when there's an actual wound to dress.
            bool medicine = worst != null && resources?.Inventory != null
                && resources.Inventory.ConsumeBySubType(ItemKind.Magic, "MagicHerbPoultice", 1) > 0;
            float quality = Mathf.Clamp(0.40f + 0.03f * docHealing + (medicine ? 0.40f : 0f), 0.20f, 1.50f);

            if (worst != null)
            {
                worst.Tended = true;
                if (patient.BodyParts.TryGetValue(worst.BodyPart, out var c))
                    patient.BodyParts[worst.BodyPart] = Mathf.Min(100f, c + TreatConditionRestore * quality);
            }
            else
            {
                RestoreWorstPart(patient, TreatConditionRestore * quality * 0.5f);
            }
            patient.Venom = Mathf.Max(0f, patient.Venom - 30f * quality);   // antivenom
            patient.RecomputeBleedRate();
            SkillRegistry.GainXp(doctor, "Healing", 25f);
        }

        private static void RestoreWorstPart(Shroomp p, float amount)
        {
            string? worstPart = null; float worstCond = 100f;
            foreach (var def in BodyPartRegistry.Template)
                if (p.BodyParts.TryGetValue(def.Name, out var c) && c < worstCond)
                { worstCond = c; worstPart = def.Name; }
            if (worstPart != null) p.BodyParts[worstPart] = Mathf.Min(100f, worstCond + amount);
        }

    }
}
