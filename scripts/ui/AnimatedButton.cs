using Godot;

// Drop-in replacement for Button with a bounce click animation,
// hover scale, and wired SFX. Uses the western-fantasy panel style.
public partial class AnimatedButton : Button
{
	[Export] public bool PlayHoverSound { get; set; } = true;
	// Smaller font + tighter padding for toolbar/HUD buttons.
	[Export] public bool Compact { get; set; } = false;

	private Vector2 _base;
	private bool _locked;

	public override void _Ready()
	{
		_base = Scale;
		Pressed      += OnPressed;
		MouseEntered += OnHover;
		MouseExited  += OnExit;

		ApplyStyle();
	}

	// Keep pivot at the visual center so scale animations grow symmetrically
	// and don't overflow the parent container boundary on any single side.
	public override void _Notification(int what)
	{
		base._Notification(what);
		if (what == NotificationResized)
			PivotOffset = Size / 2f;
	}

	private void OnPressed()
	{
		SFXManager.Instance?.Click();
		if (_locked) return;
		_locked = true;

		var t = CreateTween()
			.SetEase(Tween.EaseType.Out)
			.SetTrans(Tween.TransitionType.Elastic);
		t.TweenProperty(this, "scale", _base * 0.88f, 0.07f)
		 .SetTrans(Tween.TransitionType.Sine);
		t.TweenProperty(this, "scale", _base * 1.04f, 0.12f);
		t.TweenProperty(this, "scale", _base,          0.10f);
		t.TweenCallback(Callable.From(() => _locked = false));
	}

	private void OnHover()
	{
		if (PlayHoverSound) SFXManager.Instance?.Hover();
		if (_locked) return;
		var t = CreateTween().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Back);
		t.TweenProperty(this, "scale", _base * 1.06f, 0.12f);
	}

	private void OnExit()
	{
		if (_locked) return;
		var t = CreateTween().SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Sine);
		t.TweenProperty(this, "scale", _base, 0.10f);
	}

	private void ApplyStyle()
	{
		float hPad    = Compact ? 10f : 28f;
		float vPad    = Compact ? 6f  : 11f;
		int   fontSize = Compact ? 13  : 26;

		var normal  = WoodStyle(new Color(0.28f, 0.17f, 0.07f), hPad, vPad);
		var hover   = WoodStyle(new Color(0.40f, 0.26f, 0.12f), hPad, vPad);
		var pressed = WoodStyle(new Color(0.18f, 0.10f, 0.04f), hPad, vPad);
		var focus   = WoodStyle(new Color(0.35f, 0.22f, 0.10f), hPad, vPad);

		focus.BorderColor = new Color(1.0f, 0.85f, 0.30f);
		focus.SetBorderWidthAll(3);

		AddThemeStyleboxOverride("normal",  normal);
		AddThemeStyleboxOverride("hover",   hover);
		AddThemeStyleboxOverride("pressed", pressed);
		AddThemeStyleboxOverride("focus",   focus);
		AddThemeStyleboxOverride("disabled", WoodStyle(new Color(0.20f, 0.14f, 0.08f), hPad, vPad));

		AddThemeColorOverride("font_color",          new Color(0.96f, 0.89f, 0.68f));
		AddThemeColorOverride("font_hover_color",    new Color(1.00f, 0.97f, 0.80f));
		AddThemeColorOverride("font_pressed_color",  new Color(0.80f, 0.72f, 0.50f));
		AddThemeColorOverride("font_disabled_color", new Color(0.50f, 0.44f, 0.34f));

		const string fontPath = "res://assets/fonts/Grobold.ttf";
		if (ResourceLoader.Exists(fontPath))
		{
			AddThemeFontOverride("font", GD.Load<FontFile>(fontPath));
			AddThemeFontSizeOverride("font_size", fontSize);
		}
	}

	private static StyleBoxFlat WoodStyle(Color bg, float hPad = 28f, float vPad = 11f)
	{
		var s = new StyleBoxFlat { BgColor = bg };
		s.SetBorderWidthAll(3);
		s.BorderColor = new Color(0.82f, 0.63f, 0.18f);
		s.SetCornerRadiusAll(8);
		s.ContentMarginLeft   = hPad;
		s.ContentMarginRight  = hPad;
		s.ContentMarginTop    = vPad;
		s.ContentMarginBottom = vPad;
		s.ShadowColor  = new Color(0f, 0f, 0f, 0.4f);
		s.ShadowSize   = 4;
		s.ShadowOffset = new Vector2(2, 3);
		return s;
	}
}
