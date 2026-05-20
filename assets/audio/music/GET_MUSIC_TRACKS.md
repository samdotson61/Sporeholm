# Music Tracks

All tracks live in this folder as `.mp3`, `.ogg`, `.wav`, or `.flac` files.
The game runs fine without any of them — missing tracks are silently skipped.

---

## Architecture (v0.5.74+)

`MusicManager` keeps a **`Playlist`** per `Context` (Menu / Peace / Combat / Crisis / Ancient / Classical / Modern).

Each playlist has:
- A list of `Track` entries (path + title + artist + license + source URL)
- A `PlayMode`: `Loop` / `Sequential` / `Shuffle`

When the active stream finishes naturally (Sequential / Shuffle modes), `MusicManager` auto-advances with a 1.2 s crossfade. Loop-mode tracks set the underlying stream's `Loop=true` so `Finished` never fires.

License/attribution metadata on each `Track` is surfaced by `MusicManager.GetCredits()` and rendered in the `CreditsPanel` overlay reachable from the main menu — no separate credits list to maintain.

---

## Currently wired tracks (v0.5.74)

### Menu (Shuffle, 4 tracks)

| File | Track | Artist | License | Source |
|---|---|---|---|---|
| `menu_theme.mp3`     | Fluffing a Duck   | Kevin MacLeod              | CC-BY 3.0 | [Incompetech](https://incompetech.com/music/royalty-free/music.html) |
| `magic_forest.mp3`   | Magic Forest      | Kevin MacLeod              | CC-BY 3.0 | [Incompetech](https://incompetech.com/music/royalty-free/music.html) |
| `fairytale_waltz.mp3`| Fairytale Waltz   | Kevin MacLeod              | CC-BY 3.0 | [Incompetech](https://incompetech.com/music/royalty-free/music.html) |
| `peaceful_forest.wav`| Peaceful Forest   | Good CC0 Music collection  | CC0       | [OpenGameArt](https://opengameart.org/content/good-cc0-music) |

### Peace (Shuffle, 4 tracks)

| File | Track | Artist | License | Source |
|---|---|---|---|---|
| `village_peace.mp3`       | Country Kitchen      | Kevin MacLeod                | CC-BY 3.0           | [Incompetech](https://incompetech.com/music/royalty-free/music.html) |
| `forest_exploration.flac` | Forest Exploration   | OpenGameArt community        | CC-BY 4.0           | [OpenGameArt](https://opengameart.org/content/forest-exploration) |
| `audionautix_acoustic.mp3`| River Meditation     | Jason Shaw (Audionautix)     | CC-BY 3.0           | [archive.org](https://archive.org/details/Audionautix_Acoustic-9870) |
| `strijp_reimagined.mp3`   | Strijp (Reimagined)  | Amarent                      | CC-BY 4.0 (AI-Generated) | [FMA](https://freemusicarchive.org/music/amarent/anew/strijp-reimagined/) |

> ⚠️ **AI-generated note**: `Strijp (Reimagined)` is tagged by FMA as partially or fully AI-generated. The credits screen surfaces this in the License field. Remove the entry from `MusicManager.Playlists[Context.Peace]` if you don't want it shipped.

### Combat (Shuffle, 2 tracks)

| File | Track | Artist | License | Source |
|---|---|---|---|---|
| `combat.mp3`         | Volatile Reaction | Kevin MacLeod | CC-BY 3.0 | [Incompetech](https://incompetech.com/music/royalty-free/music.html) |
| `mushroom_candy.mp3` | Mushroom Candy    | BUZZPSY       | Pixabay (free, commercial OK, no attribution required) | [Pixabay](https://pixabay.com/music/search/mushrooms/) |

### Crisis / Ancient / Classical / Modern (Loop, 1 track each)

| File | Track | Artist | License | Context |
|---|---|---|---|---|
| `crisis.mp3`        | Darkest Child            | Kevin MacLeod | CC-BY 3.0 | Crisis |
| `era_ancient.mp3`   | Ossuary 6 - Bones        | Kevin MacLeod | CC-BY 3.0 | Ancient |
| `era_classical.ogg` | Scheming Weasel (faster) | Kevin MacLeod | CC-BY 3.0 | Classical |
| `era_modern.ogg`    | Carefree                 | Kevin MacLeod | CC-BY 3.0 | Modern |

---

## Skipped from the original research list

- **Killer Drones** (DsonanT, FMA) — actual licence is **CC BY-NC-SA**, not plain CC-BY. The NC clause makes it unsafe for any commercial release. FMA's download endpoint also now redirects to login. No replacement queued; the Crisis playlist runs single-track for now.

---

## Adding a new track

1. Drop the audio file in this folder (any of `.mp3`, `.ogg`, `.wav`, `.flac`).
2. Open `scripts/audio/MusicManager.cs`, find the `Playlists` dictionary, add a `new Track { … }` entry to the relevant context.
3. Restart Godot.

If you add a 2nd track to a `Loop` playlist, consider switching `Mode = PlayMode.Shuffle` so the player hears variety.

---

## Attribution

`CreditsPanel` (reachable from the main menu Credits button) calls `MusicManager.Instance.GetCredits()` and renders each track via `MusicManager.DefaultAttributionLine`:

> Magic Forest — Kevin MacLeod — CC-BY 3.0

That format covers CC-BY's "attribute the work" obligation. CC0 / Pixabay tracks don't require attribution but the same line surfaces them for transparency.

---

## Sound effects

Lives in `assets/audio/sfx/`. Not managed by MusicManager — loaded ad-hoc by whatever script needs them.

| Filename | Description | Suggested source |
|---|---|---|
| `button_click.ogg`   | Woody thud / clunk | https://freesound.org — search "wood thud" (CC0 filter) |
| `button_hover.ogg`   | Soft rustle or tick | https://freesound.org — search "soft click" |
| `era_transition.ogg` | Fanfare sting | https://freesound.org — search "fanfare short" |
| `alert.ogg`          | Alert chime | https://freesound.org — search "bell chime" |
