# Sporeholm — Entities Reference
**Version:** Reconstructed 2026-05-09  
**Source authority:** Codebase (TraitRegistry.cs, PersonalityRegistry.cs, BodyPartRegistry.cs, SkillRegistry.cs, Shroomp.cs, BirthSystem.cs)  
**Purpose:** Complete biological, psychological, and mechanical specification for individual shroomps.

---

## Section 1: Homo mycelianus — Species Overview

Shroomps (*Homo mycelianus*) are a distinct hominid branch with several traits that deviate sharply from baseline human biology. These are not cosmetic — every system in the simulation flows from them.

| Trait | Value | Gameplay Impact |
|---|---|---|
| Height | ~3 apples (~10 cm) | Scale defines all terrain, entity threat levels, and resource interactions |
| Blood chemistry | Haemocyanin (copper-based) | Blue pigmentation; slower oxygen transport than haemoglobin |
| Natural lifespan | ~550 years | Colony operates on century timescales; elders are genuinely irreplaceable |
| Sex ratio | 1 female per 49 births (1:49) | Reproduction is rare and precious; female shroomps are demographically critical |
| Reproduction | Stork-mediated oviposition (Era 4+) | Decouples reproduction from direct biology; stork events are colony milestones |
| Magic sensitivity | Innate in most shroomps | MagicResonance is a real physiological need, not a cosmetic resource |
| Diet | Mycophagic (fungi-adapted) | Large mushrooms are the primary structural material; small mushrooms are food |

---

## Section 2: Biological Traits

**File:** `scripts/simulation/TraitRegistry.cs`  
13 traits total. Each shroomp has a penetrance value (0.0–1.0) per trait — the degree to which that trait is expressed in their phenotype. Penetrance is inherited from parents with ±0.1 variance and clamped to [0.0, 1.0].

**Decay modifier formula:**
```
traitMod = Clamp(1.0 - (penetrance × coefficient), 0.10, 3.0)
Applied multiplicatively after role and life-stage modifiers.
```

---

### 1. BluePigmentation
**Biology:** Haemocyanin-based blue pigmentation; copper replaces iron as the oxygen-carrying atom in blood.  
**Dawn Era penetrance:** 0.10–0.30  
**Need effects:** None (aesthetic and lore; drives HaemocyaninMetabolism interaction)  

---

### 2. Miniaturization
**Biology:** Post-Great Shrinking body reduction — the evolutionary event that reduced Homo mycelianus to 10 cm height.  
**Dawn Era penetrance:** 0.15–0.40  
**Need effects:** None directly; drives scale relationships with terrain and entities  

---

### 3. ExtremeLongevity
**Biology:** Highly regulated telomere maintenance; minimal cellular senescence until ~545 years.  
**Dawn Era penetrance:** 0.50–0.85  
**Need effects:** None directly; interacts with LifeStage aging thresholds  

---

### 4. MaleSexBias
**Biology:** Chromosomal skew producing 49 male births per 1 female.  
**Dawn Era penetrance:** 0.70–0.95  
**Need effects:** None; governs colony sex ratio at birth  

---

### 5. StorkOviposition
**Biology:** Resonance-gated reproductive mechanism — the Stork functions as an external reproductive intermediary that activates only when colony MagicResonance exceeds a threshold.  
**Dawn Era penetrance:** 0.00–0.10 (nearly absent in early eras)  
**Need effects:** None directly; unlocks alternative reproduction pathway when penetrance > threshold (Era 4+)  

---

### 6. MagicalAptitude
**Biology:** Innate capacity to perceive and channel ambient magical fields; manifests as the MagicResonance need.  
**Dawn Era penetrance:** 0.10–0.45  
**Need effect:** MagicResonance decay × (1.0 − penetrance × 0.35)  
*Higher penetrance → slower MagicResonance decay (more efficient attunement)*  

---

### 7. CommunalBonding
**Biology:** Hyper-social neural wiring; isolation causes measurable physiological distress.  
**Dawn Era penetrance:** 0.40–0.75  
**Need effect:** Social decay × (1.0 − penetrance × 0.20)  
*Higher penetrance → slower Social decay (community living is more sustaining)*  

---

### 8. HaemocyaninMetabolism
**Biology:** Full copper-based blood chemistry; slower but more efficient at low oxygen levels.  
**Dawn Era penetrance:** 0.60–0.90  
**Need effects:** None in current implementation; hooks for altitude/environment modifiers in later phases  

---

### 9. LowThermalTolerance
**Biology:** Small body mass means poor thermal regulation; high surface-area-to-volume ratio leads to rapid heat loss.  
**Dawn Era penetrance:** 0.30–0.65  
**Need effect:** Nutrition decay × (1.0 − penetrance × (−0.18)) = Nutrition decay × (1.0 + penetrance × 0.18)  
*Higher penetrance → FASTER Nutrition decay (body burns more calories to stay warm)*  
*Note: negative coefficient = reversed direction; this trait increases Nutrition pressure*  

---

### 10. MycophagicDependency
**Biology:** Digestive system evolved around fungal sources; large and small mushrooms are metabolically optimal.  
**Dawn Era penetrance:** 0.50–0.85  
**Need effect:** Nutrition decay × (1.0 − penetrance × 0.12)  
*Higher penetrance → slightly slower Nutrition decay (diet is well-matched)*  

---

### 11. CognitivelyPlastic
**Biology:** High neuroplasticity enabling rapid skill acquisition across the lifespan.  
**Dawn Era penetrance:** 0.30–0.65  
**Need effects:** None in current implementation; hooks for skill gain rate in Phase 3+  

---

### 12. StatureAgility
**Biology:** Small body mass gives disproportionate speed and agility relative to body size.  
**Dawn Era penetrance:** 0.35–0.70  
**Need effects:** None in current implementation; hooks for movement speed modifier in Phase 3  

---

### 13. ResonanceSensitivity
**Biology:** Heightened sensitivity to ambient magical fields; distinct from MagicalAptitude (channeling) — this is pure perception.  
**Dawn Era penetrance:** 0.05–0.30 (rare in early eras)  
**Need effect:** MagicResonance decay × (1.0 − penetrance × 0.20)  
*Higher penetrance → slower MagicResonance decay (better ambient attunement)*  

---

## Section 3: Personality Traits

**File:** `scripts/simulation/PersonalityRegistry.cs`  
25 traits total. Each shroomp is assigned 1–5 personality traits from this pool at creation, weighted by age (older shroomps have more defined personalities). Traits provide a flat `MoodModifier` added to the raw MoodScore calculation.

```
MoodScore = Clamp(MoodRaw + sum(MoodModifier for each trait), 0, 100)
```

| # | Trait | MoodModifier | Behavioral Notes (Phase 3+) |
|---|---|---|---|
| 1 | Know-It-All | −2 | Slightly dismissive; modest social friction |
| 2 | Grumpy | −6 | Persistently negative baseline |
| 3 | Accident-Prone | −6 | Random task interruptions; occasional injury risk |
| 4 | Daydreamer | −3 | Wanders off-task; lower work efficiency |
| 5 | Vain | −3 | Resists dirty work (Mining, Foraging); morale loss without certain facilities |
| 6 | Prankster | +4 | Social interactions raise nearby shroomp mood occasionally |
| 7 | Greedy Gut | −4 | Nutrition threshold for Eat task raised (triggers hunger earlier) |
| 8 | Brawny | +3 | Physical tasks completed faster; melee combat bonus |
| 9 | Sleepyhead | −4 | Rest task triggers at < 60 (vs. default < 40) |
| 10 | Gossip | +2 | Social decay slower near other shroomps |
| 11 | Introvert | +3 | Social task triggers at < 10 (vs. default < 20); comfortable with less contact |
| 12 | Optimist | +5 | Mood-driven task urgency reduced by 10; copes better under pressure |
| 13 | Pessimist | −8 | Mood-driven task urgency raised by 10; breaks earlier under pressure |
| 14 | Perfectionist | −5 | Slower but higher-quality work; frustrated by interruptions |
| 15 | Glutton | −2 | Eat task triggers at < 70 (vs. default < 50) |
| 16 | Night Owl | −3 | Peak efficiency at night; sluggish in mornings (Phase 6+ hour system) |
| 17 | Worrywart | −10 | Safety priority +20; SeekSafety triggers at < 40 (vs. default < 20) |
| 18 | Stoic | +8 | Need tasks only trigger at critical thresholds; ignores moderate warnings |
| 19 | Empath | −5 | Inherits mood modifiers from nearby Distressed/Breaking shroomps |
| 20 | Thrill-Seeker | +4 | Prefers high-risk tasks; restless during peaceful periods |
| 21 | Sarsaparilla Snob | −1 | Minor perk; SarsaparillaRoot resource gives +5 mood boost (Phase 4+) |
| 22 | Mushroom Whisperer | +2 | Foraging yield from SmallMushroom and LargeMushroom +15% (Phase 3+) |
| 23 | Cat Paranoid | −8 | Safety drops dramatically during Azrael events; panic radius larger |
| 24 | Hat Obsessed | 0 | Flavor only; removes hat causes -3 mood (event hook) |
| 25 | Three-Apples Complex | +1 | Minor; slight height-related self-consciousness; negligible mechanical impact |

### Trait Assignment by Age (at Creation)

| Life Stage | Age Range | Trait Count |
|---|---|---|
| Sprout | < 20 years | 1–2 |
| Juvenile | 20–49 years | 1–3 |
| Young Adult | 50–199 years | 2–3 |
| Adult | 200–399 years | 2–4 |
| Elder / LastSeason | 400+ years | 3–5 |

Traits are selected randomly from the full pool of 25 without replacement per shroomp.

---

## Section 4: Skills

**File:** `scripts/simulation/SkillRegistry.cs`  
16 skills across 6 domains. Skill levels range 0–20. Skills are seeded at creation based on role; they grow through use in Phase 3+.

### Skill Domains

**Survival**
| Skill | Description |
|---|---|
| Foraging | Locating and harvesting wild food and materials |
| Botany | Growing crops and tending plants |
| Mining | Excavating stone, ore, and earth |
| Athletics | Speed, stamina, and physical endurance |

**Combat**
| Skill | Description |
|---|---|
| Melee | Close-quarters fighting with weapons or fists |
| Ranged | Attacking at distance with thrown or launched projectiles |

**Crafting**
| Skill | Description |
|---|---|
| Crafting | Making tools, goods, and equipment |
| Construction | Building and repairing structures |

**Magic**
| Skill | Description |
|---|---|
| Arcane | Channeling and manipulating magic essence |
| Ritual | Performing Shroomp ceremonies and enchantments |

**Social**
| Skill | Description |
|---|---|
| Social | Building community bonds and raising morale |
| Empathy | Reading and soothing the feelings of others |
| Leadership | Inspiring and coordinating the colony |

**Knowledge**
| Skill | Description |
|---|---|
| Lore | Understanding of Shroomp history and nature |
| Research | Systematic investigation and discovery |
| Medicine | Treating wounds and illness |

### Role Skill Bonuses (at Creation — Primary Role Seeding)

| Role | Primary (+3 weight) | Secondary (+2 weight) | Tertiary (+1 weight) |
|---|---|---|---|
| Forager | Foraging | Athletics | Botany |
| Crafter | Crafting | Construction | Mining |
| Scholar | Research | Lore | Botany |
| Mage | Arcane | Ritual | Lore |
| Caretaker | Medicine | Empathy | Social |
| Guardian | Melee | Ranged | Athletics |
| Elder | Leadership | Lore | Social |

### Skill Seeding Formula

```
Budget = min(3 random draws 0–320) × (320 − floor) + floor, capped at 320
  where floor is determined by LifeStage:
    Sprout: 0, Juvenile: 20, Adult: 50, Elder: 70, LastSeason: 80

Primary skill allocation weight: 4×
All other skills: 1×
Individual skill cap: 20 points

Points distributed proportionally by weight until Budget is exhausted or all skills are capped.
```

This produces shroomps with a clear primary skill cluster matching their role, with minor secondary spread.

---

## Section 5: Life Stages

**File:** `scripts/simulation/Shroomp.cs`

| Stage | Age Range | Description |
|---|---|---|
| **Sprout** | 0–19 years | Children; low Nutrition demand, high Rest and Social needs; 1–2 personality traits; minimal skills |
| **Juvenile** | 20–49 years | Adolescents; beginning role specialization; can be assigned roles |
| **Adult** | 50–399 years | Full capability; peak productivity period; 2–4 personality traits |
| **Elder** | 400–544 years | Reduced Nutrition need; elevated Safety concern; 3–5 personality traits; Leadership bonus |
| **LastSeason** | 545–549 years | Final stage before natural death; dramatically reduced Nutrition and Social needs; 3–5 traits |

### Life Stage Effects on Needs Decay

See Systems.md §4.4 for the full modifier table.

Key patterns:
- **Sprouts** burn less Nutrition but need more Rest and Social
- **Elders** eat less but worry more (Safety elevated)
- **LastSeason** shroomps eat and socialize very little — they are introspective, focused on Legacy

---

## Section 6: Body Parts

**File:** `scripts/simulation/BodyPartRegistry.cs`  
20 body parts, each with a condition float (0–100%). Vital parts trigger colony death when their condition reaches 0%.

### Body Part Hierarchy

**Head**
- Head *(vital)* — parent
  - Brain *(vital)* — child of Head
  - Left Eye — child of Head
  - Right Eye — child of Head
  - Nose — child of Head
  - Jaw — child of Head

**Torso**
- Torso *(vital)* — parent
  - Heart *(vital)* — child of Torso
  - Left Lung — child of Torso
  - Right Lung — child of Torso
  - Liver *(vital)* — child of Torso *(primary starvation target)*
  - Stomach — child of Torso *(secondary starvation target)*

**Left Arm**
- Left Arm — parent
  - Left Hand — child

**Right Arm**
- Right Arm — parent
  - Right Hand — child

**Left Leg**
- Left Leg — parent
  - Left Foot — child

**Right Leg**
- Right Leg — parent
  - Right Foot — child

### Vital Parts Summary

| Part | Death Condition | Primary Damage Source |
|---|---|---|
| Head | 0% condition | Combat (Phase 7) |
| Brain | 0% condition | Combat (Phase 7) |
| Torso | 0% condition | Combat (Phase 7) |
| Heart | 0% condition | Combat (Phase 7) |
| Liver | 0% condition | Starvation (NeedsSystem) |

### Healing Summary

- Non-vital parts heal passively (0.1/call) and faster with Caretaker (0.3/call)
- Vital parts do NOT heal passively; Caretaker required at 0.1/call
- No part recovers from 0% before the death check fires; prevention is the only strategy

---

## Section 7: The Founding Seven

**File:** `scripts/SimulationManager.cs`

The founding colony is always these seven shroomps. Their traits, skills, and personalities are generated from their defined age and role using the standard systems.

| Name | Age | Life Stage | Role | Sex |
|---|---|---|---|---|
| Papa | 542 | LastSeason | Elder | Male |
| Brainy | 98 | Adult | Scholar | Male |
| Hefty | 75 | Adult | Guardian | Male |
| SporeMother | 22 | Juvenile | Caretaker | Female |
| Clumsy | 45 | Juvenile | Forager | Male |
| Handy | 61 | Adult | Crafter | Male |
| Grouchy | 83 | Adult | Forager | Male |

**Design notes:**
- Papa at 542 is in LastSeason — the colony's wisest and most experienced member is also the most mortal. A quiet ticking clock from the first moment.
- SporeMother is the only female, making reproduction immediately constrained. The colony cannot grow unless she survives.
- Two Foragers (Clumsy + Grouchy) give food capacity of `max(7, 2×3) = 7` — exactly at the floor.
- No Mage in the founding seven — MagicResonance needs must be met through open-air attunement until a Mage role is assigned.

---

## Section 8: Shroomp Lifecycle

```
Born (BirthSystem / Player Start)
  ↓
Sprout (0–19 years)     — low capability, high Social/Rest needs
  ↓
Juvenile (20–49 years)  — role assignment possible; skills developing
  ↓
Adult (50–399 years)    — peak productivity; reproduction eligible
  ↓
Elder (400–544 years)   — reduced physical needs; Leadership bonus; community anchor
  ↓
LastSeason (545–549 years) — twilight; introspective; very low needs decay
  ↓
Natural Death (≥ 550)   — CauseOfDeath.Natural; ShroompDied signal fired
```

**Alternate death paths:**
- Starvation → Nutrition hits 0 → Liver damaged → Liver at 0% → CauseOfDeath.Starvation
- Combat → body part damaged to 0% by entity → CauseOfDeath.Combat (Phase 7)

---

## Section 9: Sex Ratio and Reproduction Mechanics

**Sex at birth:** 1 female per 49 births. Random draw each birth event; 1/49 probability of Female, 48/49 probability of Male.

This creates extreme demographic fragility:
- A colony of 7 shroomps has 1 female (SporeMother) — 14% of population
- Her death ends reproductive capacity until another female is born
- Even with multiple females, birth probability is only 25% per season

**Stork Oviposition (Era 4+):** When the StorkOviposition biological trait reaches sufficient colony-average penetrance and colony MagicResonance exceeds a threshold, reproduction can occur without direct female participation. The Stork appears as a scripted seasonal event and delivers an egg. This is a late-era relief valve, not a Phase 1–3 mechanic.

---

## Section 10: Visual Representation

**File:** `scripts/ui/ShroompColonyView.cs`

Shroomps are drawn as procedural sprites on the `ShroompColonyView` canvas:
- **Males:** White hat, neutral skin tone
- **Females:** Pink hat, blonde hair

Visual position (`VisualShroomp.Pos`) interpolates toward `SimPos` each render frame for smooth movement. In Phase 3, `SimPos` becomes authority (driven by BehaviorSystem); currently, shroomps wander randomly within map bounds.

Click detection uses `GetGlobalMousePosition()` for camera-correct hit testing, opening the ShroompCardPanel for the clicked shroomp.
