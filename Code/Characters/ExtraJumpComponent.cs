using Godot;

public partial class ExtraJumpComponent : Node
{
	[Export] public int ExtraJumpsGranted { get; set; } = 1;
	[Export] public bool UnlockedInitially { get; set; } = false;

	private Sam _player;
	private bool _unlocked;

	public bool IsUnlocked => _unlocked;

	[Signal] public delegate void ExtraJumpsChangedEventHandler(int extraJumpCount);

	public override void _Ready()
	{
		if (GetParent() is Sam player)
		{
			ApplyToPlayer(player);
		}
	}

	public void ApplyToPlayer(Sam player)
	{
		_player = player;
		if (UnlockedInitially)
		{
			UnlockExtraJumps(ExtraJumpsGranted);
		}
		else
		{
			_player.SetExtraJumpCapacity(0);
		}
	}

	public void UnlockExtraJumps(int extraJumpCount = -1)
	{
		_unlocked = true;
		int grantedCount = extraJumpCount >= 0 ? extraJumpCount : ExtraJumpsGranted;
		ExtraJumpsGranted = Mathf.Max(0, grantedCount);
		_player?.SetExtraJumpCapacity(ExtraJumpsGranted);
		EmitSignal(SignalName.ExtraJumpsChanged, ExtraJumpsGranted);
	}

	public void ResetExtraJumps()
	{
		if (_unlocked)
		{
			_player?.SetExtraJumpCapacity(ExtraJumpsGranted);
		}
	}
}
