using Godot;
using System.Collections.Generic;

// Autoload singleton. Handles looping music, crossfades between contexts,
// and (v0.5.74) multi-track playlists per context with per-track
// attribution metadata for a future credits screen.
//
// ── How to add a new track ─────────────────────────────────────────────────
// 1. Drop the .ogg/.mp3 file in res://assets/audio/music/
// 2. Add a `Track(...)` entry to the appropriate context's playlist below,
//    filling in Title / Artist / License / SourceUrl. Set AttributionLine
//    explicitly only if the license requires unusual wording — otherwise
//    leave null and DefaultAttributionLine will compose it from the other
//    fields.
// 3. If the file is missing on disk at startup, MusicManager silently skips
//    it (prints one notice). The other tracks in the playlist still play.
//
// Backwards-compatible: callers still use `MusicManager.Instance?.Play(Context.Menu)`
// — only the internal storage changed from "one path per context" to
// "list of tracks per context."
public partial class MusicManager : Node
{
    public static MusicManager Instance { get; private set; } = null!;

    public enum Context
    {
        None,
        Menu,
        Peace,
        Combat,
        Ancient,
        Classical,
        Modern,
        Crisis
    }

    // v0.5.74 — within a context, how the manager advances when a track ends.
    public enum PlayMode
    {
        Loop,        // single track on infinite loop (legacy behaviour)
        Sequential,  // play playlist in order, wrap when done
        Shuffle      // pick a random different track each time
    }

    // v0.5.74 — one track in a playlist. License/attribution metadata is
    // exposed by GetCredits() so a future credits screen can render it
    // without re-parsing the file names.
    public sealed class Track
    {
        public string  Path             { get; init; } = "";
        public string  Title            { get; init; } = "";
        public string  Artist           { get; init; } = "";
        public string  License          { get; init; } = "";
        public string  SourceUrl        { get; init; } = "";
        public string? AttributionLine  { get; init; } = null;   // override; otherwise composed
    }

    // v0.5.74 — playlist config per Context. PlayMode picks how the
    // manager advances within the list when a track finishes.
    public sealed class Playlist
    {
        public PlayMode Mode { get; init; } = PlayMode.Loop;
        public List<Track> Tracks { get; init; } = new();
    }

    // ── Playlists per Context ──────────────────────────────────────────────────
    // Default playlists are the v0.5.73 single-track-per-context set so
    // nothing breaks when this MusicManager rev ships before any new
    // tracks are added to the assets folder. The Menu + Peace playlists
    // are pre-stocked with the v0.5.72 music-research candidates as
    // commented-out entries — uncomment + download the file to enable
    // each one.
    private static readonly Dictionary<Context, Playlist> Playlists = new()
    {
        [Context.Menu] = new Playlist
        {
            Mode = PlayMode.Shuffle,
            Tracks =
            {
                new Track
                {
                    Path      = "res://assets/audio/music/menu_theme.mp3",
                    Title     = "Fluffing a Duck",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
                // v0.5.74 — wired from the v0.5.72 music research. Paths
                // match what landed on disk (Incompetech serves MP3 only;
                // OpenGameArt's "Good CC0 Music" ships as WAV).
                new Track
                {
                    Path      = "res://assets/audio/music/magic_forest.mp3",
                    Title     = "Magic Forest",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
                new Track
                {
                    Path      = "res://assets/audio/music/fairytale_waltz.mp3",
                    Title     = "Fairytale Waltz",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
                new Track
                {
                    Path      = "res://assets/audio/music/peaceful_forest.wav",
                    Title     = "Peaceful Forest",
                    Artist    = "Good CC0 Music collection",
                    License   = "CC0",
                    SourceUrl = "https://opengameart.org/content/good-cc0-music",
                },
            },
        },

        [Context.Peace] = new Playlist
        {
            Mode = PlayMode.Shuffle,
            Tracks =
            {
                new Track
                {
                    Path      = "res://assets/audio/music/village_peace.mp3",
                    Title     = "Country Kitchen",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
                new Track
                {
                    // FLAC is supported natively in Godot 4.3+ (project is
                    // on 4.6 per memory). If FLAC ever fails to import,
                    // re-encode this file to ogg/mp3 — the rest of the
                    // playlist keeps playing thanks to PickAndStart's
                    // missing-file fallthrough.
                    Path      = "res://assets/audio/music/forest_exploration.flac",
                    Title     = "Forest Exploration",
                    Artist    = "OpenGameArt community",
                    License   = "CC-BY 4.0",
                    SourceUrl = "https://opengameart.org/content/forest-exploration",
                },
                new Track
                {
                    Path      = "res://assets/audio/music/audionautix_acoustic.mp3",
                    Title     = "River Meditation",
                    Artist    = "Jason Shaw (Audionautix)",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://archive.org/details/Audionautix_Acoustic-9870",
                },
                // v0.5.74 — Sam: "Switch Forest Ambience for Strijp (Reimagined)."
                // Note: FMA flags this track as AI-Generated (partially or
                // fully). The license metadata reflects that so the credits
                // screen surfaces it transparently. Artist is "Amarent"
                // (the track's album is "Anew" — easy to mis-read).
                new Track
                {
                    Path      = "res://assets/audio/music/strijp_reimagined.mp3",
                    Title     = "Strijp (Reimagined)",
                    Artist    = "Amarent",
                    License   = "CC-BY 4.0 (AI-Generated)",
                    SourceUrl = "https://freemusicarchive.org/music/amarent/anew/strijp-reimagined/",
                },
            },
        },

        [Context.Combat] = new Playlist
        {
            Mode = PlayMode.Shuffle,
            Tracks =
            {
                new Track
                {
                    Path      = "res://assets/audio/music/combat.mp3",
                    Title     = "Volatile Reaction",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
                new Track
                {
                    Path      = "res://assets/audio/music/mushroom_candy.mp3",
                    Title     = "Mushroom Candy",
                    Artist    = "BUZZPSY",
                    License   = "Pixabay (free, commercial OK, no attribution required)",
                    SourceUrl = "https://pixabay.com/music/search/mushrooms/",
                },
            },
        },

        [Context.Crisis] = new Playlist
        {
            Mode = PlayMode.Loop,
            Tracks =
            {
                new Track
                {
                    Path      = "res://assets/audio/music/crisis.mp3",
                    Title     = "Darkest Child",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
                // v0.5.74 — Killer Drones (DsonanT, FMA) was skipped from
                // the v0.5.72 candidate list. Actual licence is CC BY-NC-SA
                // (not CC-BY as research suggested); the NC clause makes
                // it unsafe for any commercial release of Sporeholm. The
                // FMA download endpoint also now redirects to login. If a
                // free-for-commercial drone/eerie ambient turns up later
                // (e.g. on OpenGameArt or a Kevin MacLeod sting), drop it
                // here.
            },
        },

        [Context.Ancient] = new Playlist
        {
            Mode = PlayMode.Loop,
            Tracks =
            {
                new Track
                {
                    Path      = "res://assets/audio/music/era_ancient.mp3",
                    Title     = "Ossuary 6 - Bones",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
            },
        },

        [Context.Classical] = new Playlist
        {
            Mode = PlayMode.Loop,
            Tracks =
            {
                new Track
                {
                    Path      = "res://assets/audio/music/era_classical.ogg",
                    Title     = "Scheming Weasel (faster)",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
            },
        },

        [Context.Modern] = new Playlist
        {
            Mode = PlayMode.Loop,
            Tracks =
            {
                new Track
                {
                    Path      = "res://assets/audio/music/era_modern.ogg",
                    Title     = "Carefree",
                    Artist    = "Kevin MacLeod",
                    License   = "CC-BY 3.0",
                    SourceUrl = "https://incompetech.com/music/royalty-free/music.html",
                },
            },
        },
    };

    // ── Runtime state ──────────────────────────────────────────────────────────
    private AudioStreamPlayer _active = null!;
    private AudioStreamPlayer _fading = null!;
    private Context _current        = Context.None;
    private int     _currentTrackIx = -1;
    // Cached looping duplicates so we only set Loop=true on each stream once.
    // Keyed by path (not by Context) so two contexts referencing the same
    // file share the cached stream object.
    private readonly Dictionary<string, AudioStream> _streamCache = new();
    private readonly RandomNumberGenerator _rng = new();

    public override void _Ready()
    {
        Instance = this;
        _rng.Randomize();
        _active = new AudioStreamPlayer { Name = "Active", Bus = "Music" };
        _fading = new AudioStreamPlayer { Name = "Fading", Bus = "Music" };
        AddChild(_active);
        AddChild(_fading);
        // v0.5.74 — when the active track ends naturally, advance to the
        // next one in the playlist (Sequential / Shuffle modes). For
        // PlayMode.Loop the AudioStream's own Loop=true handles it and
        // Finished doesn't fire.
        _active.Finished += OnActiveTrackFinished;
    }

    // v0.5.75 — fired whenever the active track changes (Play, Skip, or
    // an auto-advance after Finished). The MusicPlayer widget on the
    // main menu subscribes to refresh its "Now Playing" label without
    // having to poll every frame.
    [Signal] public delegate void TrackChangedEventHandler();

    // ── Public API ─────────────────────────────────────────────────────────────

    // v0.5.77 — default crossfade durations bumped (1.8 → 3.0 for Play,
    // 1.2 → 2.5 for Skip / auto-advance). Sam: "add a fade between
    // tracks so the change in music isn't so abrupt." Combined with
    // the always-fade-in fix in StartStreamWithCrossfade, this gives
    // each track a clean ramp instead of an instant onset.
    public void Play(Context ctx, float crossfade = 3.0f)
    {
        if (_current == ctx && _active.Playing) return;
        _current = ctx;
        PickAndStart(ctx, crossfade, advance: false);
    }

    public void Stop(float fadeTime = 1.0f)
    {
        _current = Context.None;
        _currentTrackIx = -1;
        var t = CreateTween();
        t.TweenProperty(_active, "volume_db", -60f, fadeTime);
        t.TweenCallback(Callable.From(() => _active.Stop()));
        EmitSignal(SignalName.TrackChanged);
    }

    public void SetMasterVolume(float db) => _active.VolumeDb = db;

    // v0.5.74 — skip to the next track in the current playlist (or pick a
    // new random one in Shuffle mode). No-op if the current context has
    // 0 or 1 playable tracks. Useful for a future "next track" hotkey.
    public void Skip(float crossfade = 2.5f)   // v0.5.77 — was 1.2f
    {
        if (_current == Context.None) return;
        PickAndStart(_current, crossfade, advance: true);
    }

    // v0.5.75 — pause/resume the active stream WITHOUT changing the
    // currently-selected track or restarting playback. Uses
    // AudioStreamPlayer.StreamPaused so the playback head stays in place;
    // Resume picks up at the same sample. Safe to call even when no track
    // is active — it just no-ops.
    public bool IsPaused => _active != null && _active.StreamPaused;

    public bool IsPlaying => _active != null && _active.Playing && !_active.StreamPaused;

    public void Pause()
    {
        if (_active == null || !_active.Playing) return;
        _active.StreamPaused = true;
    }

    public void Resume()
    {
        if (_active == null) return;
        if (_active.Playing && _active.StreamPaused) _active.StreamPaused = false;
        // If the stream isn't currently playing (e.g. Stop was called),
        // re-start the current context's playlist from scratch.
        else if (!_active.Playing && _current != Context.None)
            PickAndStart(_current, crossfade: 0.6f, advance: false);
    }

    // v0.5.75 — returns the Track object currently selected (whether
    // playing or paused), or null if no playlist is active.
    public Track? CurrentTrack
    {
        get
        {
            if (_current == Context.None || _currentTrackIx < 0) return null;
            if (!Playlists.TryGetValue(_current, out var playlist)) return null;
            if (_currentTrackIx >= playlist.Tracks.Count) return null;
            return playlist.Tracks[_currentTrackIx];
        }
    }

    // v0.5.74 — flat list of (Title, Artist, License, Url, AttributionLine)
    // for every UNIQUE track currently registered across all contexts.
    // Sorted by artist + title so a credits screen renders alphabetically.
    // Same track referenced by multiple contexts appears once.
    public IReadOnlyList<Track> GetCredits()
    {
        var seen = new HashSet<string>();
        var list = new List<Track>();
        foreach (var (_, playlist) in Playlists)
        {
            foreach (var t in playlist.Tracks)
            {
                if (string.IsNullOrEmpty(t.Path) || seen.Contains(t.Path)) continue;
                seen.Add(t.Path);
                list.Add(t);
            }
        }
        list.Sort((a, b) =>
        {
            int c = string.Compare(a.Artist, b.Artist, System.StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.Title, b.Title, System.StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    // Default attribution wording used by GetCredits() consumers that
    // don't supply their own. Override per-track via Track.AttributionLine.
    // Example: "Magic Forest — Kevin MacLeod (incompetech.com) — CC-BY 3.0".
    public static string DefaultAttributionLine(Track t) =>
        t.AttributionLine ??
        $"{t.Title} — {t.Artist} — {t.License}";

    // ── Track selection / playback ─────────────────────────────────────────────

    private void PickAndStart(Context ctx, float crossfade, bool advance)
    {
        if (!Playlists.TryGetValue(ctx, out var playlist) || playlist.Tracks.Count == 0)
        {
            GD.Print($"[Music] No playlist for {ctx}");
            return;
        }

        int ix = PickTrackIndex(playlist, advance);
        if (ix < 0) return;
        var track = playlist.Tracks[ix];

        if (!ResourceLoader.Exists(track.Path))
        {
            // File missing on disk — try the next track. Avoid infinite
            // loop by capping retries to playlist size.
            for (int tries = 0; tries < playlist.Tracks.Count; tries++)
            {
                ix = (ix + 1) % playlist.Tracks.Count;
                track = playlist.Tracks[ix];
                if (ResourceLoader.Exists(track.Path)) break;
            }
            if (!ResourceLoader.Exists(track.Path))
            {
                GD.Print($"[Music] No playable tracks on disk for {ctx} (looked for {playlist.Tracks.Count} entries)");
                return;
            }
        }

        _currentTrackIx = ix;
        var stream = GetLoopingStream(track.Path, loopWithinTrack: playlist.Mode == PlayMode.Loop);
        StartStreamWithCrossfade(stream, crossfade);
        GD.Print($"[Music] {ctx} → {DefaultAttributionLine(track)}");
        EmitSignal(SignalName.TrackChanged);   // v0.5.75 — for MusicPlayer widget
    }

    private int PickTrackIndex(Playlist playlist, bool advance)
    {
        int n = playlist.Tracks.Count;
        if (n == 0) return -1;
        if (n == 1) return 0;
        return playlist.Mode switch
        {
            PlayMode.Sequential => advance ? (_currentTrackIx + 1) % n
                                            : System.Math.Max(0, _currentTrackIx),
            PlayMode.Shuffle    => PickShuffleIndex(n),
            _                   => 0,   // Loop mode is single-track-equivalent
        };
    }

    // Picks any index other than the current one (so repeats only happen
    // with 1-track playlists, which short-circuit earlier).
    private int PickShuffleIndex(int n)
    {
        if (n <= 1) return 0;
        int ix;
        do { ix = _rng.RandiRange(0, n - 1); } while (ix == _currentTrackIx);
        return ix;
    }

    private void OnActiveTrackFinished()
    {
        // Only advances for Sequential / Shuffle modes — Loop mode sets the
        // stream's own Loop=true so Finished never fires.
        if (_current == Context.None) return;
        if (!Playlists.TryGetValue(_current, out var playlist)) return;
        if (playlist.Mode == PlayMode.Loop) return;
        // v0.5.77 — bumped 1.2 → 2.5 to match Skip's smoother crossfade.
        // Pairs with the fade-in-on-track-start fix below so the new
        // track ramps up cleanly even though the previous one already
        // ended (no overlap is possible — fade-out is a no-op when
        // _active stopped — but the fade-in still runs).
        PickAndStart(_current, crossfade: 2.5f, advance: true);
    }

    private void StartStreamWithCrossfade(AudioStream stream, float crossfade)
    {
        // v0.5.77 — capture playing-state BEFORE we overwrite _active.Stream
        // below; we need it to decide whether to fade-out the old track.
        bool wasPlaying = _active.Playing;

        if (wasPlaying && crossfade > 0f)
        {
            // Hand the current track off to the fading player
            _fading.Stream   = _active.Stream;
            _fading.VolumeDb = _active.VolumeDb;
            _fading.Play(_active.GetPlaybackPosition());

            // v0.5.77 — SineIn for fade-out (gentle release at the start,
            // sharper toward the end). Pairs with SineOut on the fade-in
            // below; the two curves sum to an approximately constant
            // perceived loudness during the overlap — a cleaner crossfade
            // than the v0.5.75 linear dB ramp.
            var fadeOut = CreateTween();
            fadeOut.SetTrans(Tween.TransitionType.Sine);
            fadeOut.SetEase(Tween.EaseType.In);
            fadeOut.TweenProperty(_fading, "volume_db", -60f, crossfade);
            fadeOut.TweenCallback(Callable.From(() => _fading.Stop()));
        }

        _active.Stream = stream;
        // v0.5.77 — ALWAYS start at -60 dB and fade in when crossfade > 0,
        // regardless of whether the prior stream was still playing. Pre-
        // v0.5.77 the auto-advance after Finished bypassed this (wasPlaying
        // = false at that point because the prev track just ended), so the
        // new track started at full volume instantly — the abrupt onset
        // Sam called out in v0.5.76. Now every track gets a clean ramp-up.
        _active.VolumeDb = crossfade > 0f ? -60f : 0f;
        _active.Play();

        if (crossfade > 0f)
        {
            var fadeIn = CreateTween();
            fadeIn.SetTrans(Tween.TransitionType.Sine);
            fadeIn.SetEase(Tween.EaseType.Out);
            fadeIn.TweenProperty(_active, "volume_db", 0f, crossfade);
        }
    }

    // ── Stream loading ─────────────────────────────────────────────────────────

    // For Loop mode we want the stream itself to repeat (AudioStream.Loop = true)
    // so the Finished signal never fires and we don't need to re-pick.
    // For Sequential / Shuffle modes we DISABLE looping so Finished fires
    // exactly once when the track ends, and OnActiveTrackFinished advances.
    private AudioStream GetLoopingStream(string path, bool loopWithinTrack)
    {
        // Cache key includes the loop flag because the duplicated stream
        // bakes in Loop = true or false. A single context could switch
        // modes at runtime in theory; cheap to cache both shapes.
        string cacheKey = $"{path}|{(loopWithinTrack ? "loop" : "once")}";
        if (_streamCache.TryGetValue(cacheKey, out var cached)) return cached;

        var src = GD.Load<AudioStream>(path);
        AudioStream prepared;

        // v0.5.74 — handle every stream subclass our playlists actually
        // reference (MP3 from Incompetech / Pixabay / FMA, OGG from older
        // tracks, WAV from OpenGameArt's "Good CC0 Music" pack). FLAC
        // (Forest Exploration) falls through to the else-branch — Godot
        // 4.3+ ships AudioStreamFLAC but the project compiles against
        // multiple Godot versions, so we don't take a hard reference. The
        // AudioStreamPlayer's Finished signal still fires when a FLAC
        // ends, so Sequential / Shuffle PlayModes keep advancing
        // correctly even without a stream-level loop flag.
        if (src is AudioStreamMP3 mp3)
        {
            var dup = (AudioStreamMP3)mp3.Duplicate();
            dup.Loop = loopWithinTrack;
            prepared = dup;
        }
        else if (src is AudioStreamOggVorbis ogg)
        {
            var dup = (AudioStreamOggVorbis)ogg.Duplicate();
            dup.Loop = loopWithinTrack;
            prepared = dup;
        }
        else if (src is AudioStreamWav wav)
        {
            var dup = (AudioStreamWav)wav.Duplicate();
            dup.LoopMode = loopWithinTrack
                ? AudioStreamWav.LoopModeEnum.Forward
                : AudioStreamWav.LoopModeEnum.Disabled;
            prepared = dup;
        }
        else
        {
            prepared = src;
        }

        _streamCache[cacheKey] = prepared;
        return prepared;
    }
}
