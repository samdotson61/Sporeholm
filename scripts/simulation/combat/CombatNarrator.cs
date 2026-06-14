namespace Sporeholm.Simulation.Combat
{
    // v0.7.0 (Phase 7 §7.9) — template-driven combat narrative. Turns a
    // resolved exchange into one message-log sentence. Strings only, no data.
    public static class CombatNarrator
    {
        public static string Describe(string attacker, in CombatProfile weapon,
            CombatWound wound, string part, string defender, CombatOutcome outcome)
        {
            string w = string.IsNullOrEmpty(weapon.WeaponName)
                ? "weapon" : weapon.WeaponName.ToLowerInvariant();

            switch (outcome)
            {
                case CombatOutcome.Miss:
                    return $"{attacker} swings their {w} at {defender} but misses.";
                case CombatOutcome.Dodge:
                    return $"{defender} dodges {attacker}'s {w}.";
                case CombatOutcome.Block:
                    return $"{defender} blocks {attacker}'s {w}.";
            }

            string verb = Verb(weapon.Type);
            string effect = WoundEffect(wound);

            if (outcome == CombatOutcome.Kill)
                return $"{attacker} {verb} {defender}'s {part} with their {w}, landing a killing blow!";

            if (outcome == CombatOutcome.Crit)
                return effect.Length > 0
                    ? $"Critical! {attacker} {verb} {defender}'s {part} with their {w}, {effect}!"
                    : $"Critical! {attacker} {verb} {defender}'s {part} with their {w}!";

            // ordinary hit
            return effect.Length > 0
                ? $"{attacker} {verb} {defender}'s {part} with their {w}, {effect}."
                : $"{attacker} {verb} {defender}'s {part} with their {w}.";
        }

        private static string Verb(WeaponType t) => t switch
        {
            WeaponType.Edged    => "slashes",
            WeaponType.Blunt    => "bashes",
            WeaponType.Piercing => "stabs",
            WeaponType.Ranged   => "strikes",
            WeaponType.Magical  => "sears",
            _                   => "strikes",
        };

        private static string WoundEffect(CombatWound w) => w switch
        {
            CombatWound.Cut        => "opening a deep gash",
            CombatWound.Fracture   => "fracturing the joint",
            CombatWound.Puncture   => "punching deep",
            CombatWound.Sever      => "severing it",
            CombatWound.Mangle     => "mangling it",
            CombatWound.Concussion => "rattling their senses",
            _                      => "",
        };
    }
}
