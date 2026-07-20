using Godot;

// Floating label above a networked player's head. Reads Sam.DisplayName —
// which arrives on remote copies via MultiplayerSynchronizer just like
// NetAnimationName does — so this never needs its own networking code.
public partial class NameTag : Node2D
{
	[Export] public NodePath PlayerPath = "..";
	// Local-space offset — gets multiplied by the parent's own Scale (1.3x on
	// Sam, see below), so -70 here actually rendered ~91px above a ~62px-tall
	// character: far too high. -24 lands it just above the head.
	[Export] public Vector2 Offset = new Vector2(0, -24);

	private Sam _player;
	private Label _label;
	private Control _speakingIcon;
	private ColorRect[] _speakingBars;
	private string _lastText = "";
	private bool _wasSpeaking;
	private float _pulseTime;

	public override void _Ready()
	{
		_player = GetNode<Sam>(PlayerPath);
		_label = GetNode<Label>("Label");
		_speakingIcon = GetNode<Control>("SpeakingIcon");
		_speakingBars = new[]
		{
			GetNode<ColorRect>("SpeakingIcon/Bar1"),
			GetNode<ColorRect>("SpeakingIcon/Bar2"),
			GetNode<ColorRect>("SpeakingIcon/Bar3"),
		};
		// All three bars share a bottom edge (see NetworkPlayer.tscn) —
		// pivot from the bottom so the pulse in _Process grows/shrinks them
		// upward like a real equalizer instead of from the top-left corner.
		foreach (ColorRect bar in _speakingBars)
		{
			bar.PivotOffset = new Vector2(bar.Size.X / 2f, bar.Size.Y);
		}
		Position = Offset;

		// Sam's own scale (1.3x, see Sam.tscn) would otherwise blow the tag
		// up along with the character — cancel it so the tag stays a
		// consistent on-screen size regardless of the parent's scale.
		Vector2 parentScale = _player.Scale;
		Scale = new Vector2(
			parentScale.X != 0f ? 1f / parentScale.X : 1f,
			parentScale.Y != 0f ? 1f / parentScale.Y : 1f
		);
	}

	public override void _Process(double delta)
	{
		if (_player.DisplayName != _lastText)
		{
			_lastText = _player.DisplayName;
			_label.Text = _lastText;
		}

		if (_player.NetIsSpeaking != _wasSpeaking)
		{
			_wasSpeaking = _player.NetIsSpeaking;
			_speakingIcon.Visible = _wasSpeaking;
			_pulseTime = 0f;
		}

		// A small "equalizer" pulse on the three bars while visible, purely
		// cosmetic — reads as live/active rather than a static icon.
		if (_wasSpeaking)
		{
			_pulseTime += (float)delta * 10f;
			for (int i = 0; i < _speakingBars.Length; i++)
			{
				float wave = Mathf.Sin(_pulseTime + i * 1.7f) * 0.5f + 0.5f;
				_speakingBars[i].Scale = new Vector2(1f, Mathf.Lerp(0.5f, 1f, wave));
			}
		}

		// Player sprite flips for facing, but the tag above their head
		// shouldn't mirror with it.
		GlobalRotation = 0f;
	}
}
