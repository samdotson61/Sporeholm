namespace Sporeholm.Simulation.Items
{
    // v0.4.4 — DF-style equipment slot taxonomy, mapped 1:1 to the
    // external `BodyPartRegistry` parts that a shroomp can actually wear
    // gear on. Phase 7 combat adds layered armor (under/over) per slot;
    // for v0.4.4 each slot holds a single item.
    //
    // Hand slots are special — they hold tools, weapons, and (Phase 7)
    // shields. The shroomp's `Handedness` trait sets the default for
    // auto-equip; the off-hand stays free for dual-wielding /
    // shield-carry when combat lands.
    public enum EquipSlot : byte
    {
        None,
        Head,
        Torso,
        LeftArm,
        RightArm,
        LeftHand,
        RightHand,
        LeftLeg,
        RightLeg,
        LeftFoot,
        RightFoot,
    }

    public static class EquipSlotMeta
    {
        // Body-part name (matches BodyPartRegistry.Template entries) for
        // damage routing and wear-and-tear cross-referencing. Phase 7
        // combat reads this to decide which slot a hit can damage.
        // v0.5.84q — Head → Cap, Torso → Stalk per the body-part
        // mushroom-rename pass. EquipSlot enum identifiers are kept
        // (Head, Torso) so the internal Equipment-dict keys stay
        // stable across the rename; only the body-part STRING the
        // damage router reads is updated.
        public static string BodyPart(EquipSlot s) => s switch
        {
            EquipSlot.Head      => "Cap",
            EquipSlot.Torso     => "Stalk",
            EquipSlot.LeftArm   => "Left Arm",
            EquipSlot.RightArm  => "Right Arm",
            EquipSlot.LeftHand  => "Left Hand",
            EquipSlot.RightHand => "Right Hand",
            EquipSlot.LeftLeg   => "Left Leg",
            EquipSlot.RightLeg  => "Right Leg",
            EquipSlot.LeftFoot  => "Left Foot",
            EquipSlot.RightFoot => "Right Foot",
            _                   => "",
        };

        // v0.5.84q — Display labels for the player-facing inventory /
        // shroomp-card. Head shows as "Cap" + Torso as "Stalk" so the
        // equipment slot reads consistently with the body-part rename.
        public static string Display(EquipSlot s) => s switch
        {
            EquipSlot.Head      => "Cap",
            EquipSlot.Torso     => "Stalk",
            EquipSlot.LeftArm   => "Left Arm",
            EquipSlot.RightArm  => "Right Arm",
            EquipSlot.LeftHand  => "Left Hand",
            EquipSlot.RightHand => "Right Hand",
            EquipSlot.LeftLeg   => "Left Leg",
            EquipSlot.RightLeg  => "Right Leg",
            EquipSlot.LeftFoot  => "Left Foot",
            EquipSlot.RightFoot => "Right Foot",
            _                   => s.ToString(),
        };

        // Slots a player can equip directly (excludes None). Iteration
        // order matches the head-to-toe order used in the unit card +
        // shroomp renderer.
        public static readonly EquipSlot[] All =
        {
            EquipSlot.Head,
            EquipSlot.Torso,
            EquipSlot.LeftArm,  EquipSlot.RightArm,
            EquipSlot.LeftHand, EquipSlot.RightHand,
            EquipSlot.LeftLeg,  EquipSlot.RightLeg,
            EquipSlot.LeftFoot, EquipSlot.RightFoot,
        };

        // Body-class an item is built for. `Hand` items can equip into
        // either LeftHand or RightHand (Handedness picks the default);
        // `Foot` items go into either foot slot; pair items get one slot
        // each through repeated equip until combat introduces paired-
        // armor mechanics in Phase 7.
        public enum BodyClass : byte
        {
            None, Head, Torso, Arm, Hand, Leg, Foot,
        }

        public static EquipSlot[] SlotsFor(BodyClass cls) => cls switch
        {
            BodyClass.Head  => new[] { EquipSlot.Head },
            BodyClass.Torso => new[] { EquipSlot.Torso },
            BodyClass.Arm   => new[] { EquipSlot.LeftArm,  EquipSlot.RightArm },
            BodyClass.Hand  => new[] { EquipSlot.LeftHand, EquipSlot.RightHand },
            BodyClass.Leg   => new[] { EquipSlot.LeftLeg,  EquipSlot.RightLeg },
            BodyClass.Foot  => new[] { EquipSlot.LeftFoot, EquipSlot.RightFoot },
            _               => System.Array.Empty<EquipSlot>(),
        };
    }
}
