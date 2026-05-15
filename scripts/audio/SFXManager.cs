using Godot;
using System.Collections.Generic;

// Autoload singleton. Fire-and-forget sound effects.
public partial class SFXManager : Node
{
    public static SFXManager Instance { get; private set; } = null!;

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["btn_click"] = "res://assets/audio/sfx/button_click.ogg",
        ["btn_hover"] = "res://assets/audio/sfx/button_hover.ogg",
        ["era_sting"]  = "res://assets/audio/sfx/era_transition.ogg",
        ["alert"]      = "res://assets/audio/sfx/alert.ogg",
    };

    private readonly Dictionary<string, AudioStream> _cache = new();

    public override void _Ready()
    {
        Instance = this;
        foreach (var (key, path) in Paths)
        {
            if (ResourceLoader.Exists(path))
                _cache[key] = GD.Load<AudioStream>(path);
        }
    }

    public void Play(string key, float volumeDb = 0f)
    {
        if (!_cache.TryGetValue(key, out var stream)) return;
        var p = new AudioStreamPlayer
        {
            Stream = stream,
            VolumeDb = volumeDb,
            Bus = "SFX"
        };
        AddChild(p);
        p.Play();
        p.Finished += p.QueueFree;
    }

    public void Click() => Play("btn_click");
    public void Hover() => Play("btn_hover", -8f);
}
