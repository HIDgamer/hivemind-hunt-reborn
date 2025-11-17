using Godot;

public partial class AbilityPickupComponent : Area2D
{
	public enum AbilityKind
	{
		ExtraJump,
		Dash
	}

	[Export] public AbilityKind Ability { get; set; } = AbilityKind.ExtraJump;
	[Export] public int ExtraJumpCount { get; set; } = 1;
	[Export] public bool ConsumeOnPickup { get; set; } = true;
	[Export] public AudioStream PickupSound { get; set; }

	private AudioStreamPlayer2D _audioPlayer;

	[Signal] public delegate void AbilityCollectedEventHandler(Sam player, string abilityName);

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
		_audioPlayer = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");
		if (_audioPlayer == null)
		{
			_audioPlayer = new AudioStreamPlayer2D { Name = "AudioStreamPlayer2D", VolumeDb = -4f };
			AddChild(_audioPlayer);
		}

		if (PickupSound != null)
		{
			_audioPlayer.Stream = PickupSound;
		}
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is not Sam player) return;

		switch (Ability)
		{
			case AbilityKind.ExtraJump:
				player.GetNodeOrNull<ExtraJumpComponent>("ExtraJumpComponent")?.UnlockExtraJumps(ExtraJumpCount);
				break;
			case AbilityKind.Dash:
				DashComponent dash = player.GetNodeOrNull<DashComponent>("DashComponent");
				if (dash == null)
				{
					dash = new DashComponent { Name = "DashComponent" };
					player.AddChild(dash);
				}
				dash.UnlockDash();
				break;
		}

		EmitSignal(SignalName.AbilityCollected, player, Ability.ToString());

		if (ConsumeOnPickup)
		{
			Consume();
		}
	}

	private void Consume()
	{
		SetDeferred(Area2D.PropertyName.Monitoring, false);
		SetDeferred(Area2D.PropertyName.Monitorable, false);

		foreach (Node child in GetChildren())
		{
			if (child is CanvasItem canvasItem)
			{
				canvasItem.Visible = false;
			}
		}

		if (_audioPlayer?.Stream != null)
		{
			_audioPlayer.Visible = true;
			_audioPlayer.Play();
			GetTree().CreateTimer(0.35).Timeout += QueueFree;
		}
		else
		{
			QueueFree();
		}
	}
}
