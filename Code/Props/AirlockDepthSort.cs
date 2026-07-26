using Godot;
using System.Collections.Generic;

// The "walk behind a door frame with real depth" trick, but for a side-view
// platformer rather than the top-down game Y-sort normally works for — a
// platformer's vertical screen position doesn't correlate with in-front-of/
// behind-the-frame the way looking straight down does, so plain Y-sort on
// the door's sprite doesn't solve this.
//
// Instead: remember which side of the door's centerline a body was on the
// moment it entered this trigger zone. As long as it stays on that same
// side (approaching, or standing right at the threshold) it draws in FRONT
// of the door frame; once it's crossed all the way through to the far side
// it draws BEHIND the frame — matching how a real doorway with physical
// depth reads from either direction you approach it, symmetric for someone
// entering from the east or the west. Works per-body independently, so two
// players (or a player and an enemy) mid-crossing from opposite directions
// at once still each get the correct answer.
public partial class AirlockDepthSort : Node2D
{
	[Export] public int FrontZIndex = 6;
	// The background TileMapLayer renders at z_index -2 (see
	// Level_00_Tutorial.tscn). The door frame sprite itself has no z_index
	// override (0), so -6 put a "behind the door" body behind the
	// background too, not just behind the frame — it would vanish entirely
	// instead of just ducking behind the door. -1 sits behind the frame but
	// still in front of the background.
	[Export] public int BehindZIndex = -1;
	// The flip used to commit the instant Mathf.Sign of the body's offset
	// from the door's centerline flipped, i.e. exactly at the point of
	// maximum visual overlap with the door frame sprite — reads as Sam
	// popping/disappearing for a frame, and a single frame of X-jitter right
	// at center could even flip-flop the ZIndex back and forth. Requiring
	// the body to be decisively FlipDeadzoneX past center before the flip
	// commits delays it until she's already partially behind the frame from
	// the crossing motion itself, and makes the commit direction-stable.
	[Export] public float FlipDeadzoneX = 8f;

	private Area2D _area;

	private class TrackedBody
	{
		public int OriginalZIndex;
		public int EnterSide;
		public int CommittedSide;
	}

	private readonly Dictionary<Node2D, TrackedBody> _tracked = new();

	public override void _Ready()
	{
		_area = GetNode<Area2D>("Area2D");
		_area.BodyEntered += OnBodyEntered;
		_area.BodyExited += OnBodyExited;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is not CharacterBody2D) return;
		if (_tracked.ContainsKey(body)) return;

		int side = Mathf.Sign(body.GlobalPosition.X - GlobalPosition.X);
		if (side == 0) side = 1;
		_tracked[body] = new TrackedBody { OriginalZIndex = body.ZIndex, EnterSide = side, CommittedSide = side };
	}

	private void OnBodyExited(Node2D body)
	{
		if (_tracked.TryGetValue(body, out TrackedBody tracked))
		{
			body.ZIndex = tracked.OriginalZIndex;
			_tracked.Remove(body);
		}
	}

	public override void _PhysicsProcess(double delta)
	{
		if (_tracked.Count == 0) return;

		List<Node2D> stale = null;
		foreach (KeyValuePair<Node2D, TrackedBody> pair in _tracked)
		{
			Node2D body = pair.Key;
			TrackedBody tracked = pair.Value;
			if (!IsInstanceValid(body))
			{
				(stale ??= new List<Node2D>()).Add(body);
				continue;
			}

			float offsetX = body.GlobalPosition.X - GlobalPosition.X;
			int instantSide = Mathf.Sign(offsetX);
			if (instantSide == 0) instantSide = tracked.CommittedSide;

			if (instantSide != tracked.CommittedSide && Mathf.Abs(offsetX) > FlipDeadzoneX)
			{
				tracked.CommittedSide = instantSide;
			}

			body.ZIndex = tracked.CommittedSide == tracked.EnterSide ? FrontZIndex : BehindZIndex;
		}

		if (stale != null)
		{
			foreach (Node2D body in stale) _tracked.Remove(body);
		}
	}
}
