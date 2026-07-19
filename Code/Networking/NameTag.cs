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
	private string _lastText = "";

	public override void _Ready()
	{
		_player = GetNode<Sam>(PlayerPath);
		_label = GetNode<Label>("Label");
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

		// Player sprite flips for facing, but the tag above their head
		// shouldn't mirror with it.
		GlobalRotation = 0f;
	}
}
