using Godot;
using System.Collections.Generic;

// Autoload singleton. Handles looping music and crossfades between contexts.
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

    private static readonly Dictionary<Context, string> Tracks = new()
    {
        [Context.Menu]      = "res://assets/audio/music/menu_theme.mp3",
        [Context.Peace]     = "res://assets/audio/music/village_peace.mp3",
        [Context.Combat]    = "res://assets/audio/music/combat.mp3",
        [Context.Ancient]   = "res://assets/audio/music/era_ancient.mp3",
        [Context.Classical] = "res://assets/audio/music/era_classical.ogg",
        [Context.Modern]    = "res://assets/audio/music/era_modern.ogg",
        [Context.Crisis]    = "res://assets/audio/music/crisis.mp3",
    };

    private AudioStreamPlayer _active = null!;
    private AudioStreamPlayer _fading = null!;
    private Context _current = Context.None;

    // Cached looping duplicates so we only duplicate each stream once
    private readonly Dictionary<Context, AudioStream> _loopCache = new();

    public override void _Ready()
    {
        Instance = this;
        _active = new AudioStreamPlayer { Name = "Active", Bus = "Music" };
        _fading = new AudioStreamPlayer { Name = "Fading", Bus = "Music" };
        AddChild(_active);
        AddChild(_fading);
    }

    public void Play(Context ctx, float crossfade = 1.8f)
    {
        if (_current == ctx && _active.Playing) return;
        _current = ctx;

        if (!Tracks.TryGetValue(ctx, out var path) || !ResourceLoader.Exists(path))
        {
            GD.Print($"[Music] Missing track for {ctx}: {path}");
            return;
        }

        var stream = GetLoopingStream(ctx, path);

        if (_active.Playing && crossfade > 0f)
        {
            // Hand the current track off to the fading player
            _fading.Stream = _active.Stream;
            _fading.VolumeDb = _active.VolumeDb;
            _fading.Play(_active.GetPlaybackPosition());

            var fadeOut = CreateTween();
            fadeOut.TweenProperty(_fading, "volume_db", -60f, crossfade);
            fadeOut.TweenCallback(Callable.From(() => _fading.Stop()));
        }

        _active.Stream = stream;
        _active.VolumeDb = crossfade > 0f && _active.Playing ? -60f : 0f;
        _active.Play();

        if (crossfade > 0f)
        {
            var fadeIn = CreateTween();
            fadeIn.TweenProperty(_active, "volume_db", 0f, crossfade);
        }
    }

    public void Stop(float fadeTime = 1.0f)
    {
        _current = Context.None;
        var t = CreateTween();
        t.TweenProperty(_active, "volume_db", -60f, fadeTime);
        t.TweenCallback(Callable.From(() => _active.Stop()));
    }

    public void SetMasterVolume(float db) => _active.VolumeDb = db;

    // ── Stream loading ─────────────────────────────────────────────────────────

    private AudioStream GetLoopingStream(Context ctx, string path)
    {
        if (_loopCache.TryGetValue(ctx, out var cached)) return cached;

        var src = GD.Load<AudioStream>(path);
        AudioStream looping;

        if (src is AudioStreamMP3 mp3)
        {
            var dup = (AudioStreamMP3)mp3.Duplicate();
            dup.Loop = true;
            looping = dup;
        }
        else if (src is AudioStreamOggVorbis ogg)
        {
            var dup = (AudioStreamOggVorbis)ogg.Duplicate();
            dup.Loop = true;
            looping = dup;
        }
        else
        {
            looping = src;
        }

        _loopCache[ctx] = looping;
        return looping;
    }
}
