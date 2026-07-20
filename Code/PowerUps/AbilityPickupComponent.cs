using Godot;

public partial class AbilityPickupComponent : Area2D
{
	public enum AbilityKind
	{
		ExtraJump,
		Dash,
		MaxHealth
	}

	[Export] public AbilityKind Ability { get; set; } = AbilityKind.ExtraJump;
	[Export] public int ExtraJumpCount { get; set; } = 1;
	[Export] public int MaxHealthIncrease { get; set; } = 1;
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

		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		// Networked: the server alone decides a pickup was collected (its
		// Area sees every player's synced body), then tells every peer to
		// grant the upgrade to THEIR local player — upgrades are squad-wide,
		// so one player grabbing dash unlocks dash for everyone, and the
		// pickup disappears for everyone at the same moment.
		if (networkManager != null && networkManager.IsClientSession) return;
		if (networkManager != null && networkManager.IsServerSession)
		{
			Rpc(MethodName.RemoteCollect);
			RemoteCollect();
			return;
		}

		// Single-player: grant directly to whoever touched it.
		GrantTo(player);
		EmitSignal(SignalName.AbilityCollected, player, Ability.ToString());
		if (ConsumeOnPickup)
		{
			Consume();
		}
	}

	[Rpc(MultiplayerApi.RpcMode.Authority)]
	private void RemoteCollect()
	{
		Sam localPlayer = FindLocalPlayer();
		if (localPlayer != null)
		{
			GrantTo(localPlayer);
			EmitSignal(SignalName.AbilityCollected, localPlayer, Ability.ToString());
		}

		if (ConsumeOnPickup)
		{
			Consume();
		}
	}

	// The copy of Sam this machine actually controls — every other player in
	// the group is a remote puppet whose components don't drive anything.
	private Sam FindLocalPlayer()
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is Sam sam && sam.IsMultiplayerAuthority())
			{
				return sam;
			}
		}
		return null;
	}

	private void GrantTo(Sam player)
	{
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
			case AbilityKind.MaxHealth:
				player.GetNodeOrNull<HealthComponent>("HealthComponent")?.IncreaseMaxHealth(MaxHealthIncrease);
				break;
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
