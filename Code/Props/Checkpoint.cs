using Godot;

// A touch-activated checkpoint. Registers its position with the
// project-wide CheckpointManager autoload so a death or a fall into a
// boundary volume respawns here instead of reloading the whole level.
// No dedicated art exists yet — the light glow and spark particles are
// the visual until real checkpoint art is made (same approach already
// used for SparkConduit).
public partial class Checkpoint : Area2D
{
	[Export] public AudioStream ActivateSound;
	[Export] public Color InactiveColor = new Color(0.5f, 0.5f, 0.5f, 1f);
	[Export] public Color ActiveColor = new Color(0.3f, 0.9f, 1f, 1f);

	private PointLight2D _light;
	private CpuParticles2D _activateParticles;
	private AudioStreamPlayer2D _audioPlayer;
	private bool _activated = false;

	// Fired once, the first time this checkpoint is actually reached (not on
	// every re-entry) — Narrator.gd hooks this to bark a line the moment a
	// checkpoint is hit, same event other systems only had polling access to
	// before (CheckpointManager itself has no signals, only state).
	[Signal] public delegate void ActivatedEventHandler();

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
		_light = GetNodeOrNull<PointLight2D>("PointLight2D");
		_activateParticles = GetNodeOrNull<CpuParticles2D>("ActivateParticles");
		_audioPlayer = GetNodeOrNull<AudioStreamPlayer2D>("AudioStreamPlayer2D");

		if (_light != null)
		{
			_light.Color = InactiveColor;
			_light.Energy = 0.4f;
		}

		// If this is already the stored checkpoint (e.g. re-entering this
		// scene after respawning elsewhere), show it as active right away.
		var manager = GetNodeOrNull<CheckpointManager>("/root/CheckpointManager");
		if (manager != null
			&& manager.HasCheckpointForScene(GetTree().CurrentScene.SceneFilePath)
			&& manager.CheckpointPosition == GlobalPosition)
		{
			SetActivatedVisual();
		}
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is not Sam) return;

		var manager = GetNode<CheckpointManager>("/root/CheckpointManager");
		manager.SetCheckpoint(GlobalPosition, GetTree().CurrentScene.SceneFilePath);
		HealLocalPlayer();

		if (_activated) return;

		SetActivatedVisual();
		_activateParticles?.Restart();
		if (_audioPlayer != null && ActivateSound != null)
		{
			_audioPlayer.Stream = ActivateSound;
			_audioPlayer.Play();
		}
		EmitSignal(SignalName.Activated);
	}

	// Heals every player, not just whoever physically touched the checkpoint.
	// Remote players' bodies are still physically present here (position is
	// copied straight onto GlobalPosition each frame — see Sam.cs's
	// ApplyRemoteVisualState), so this Area2D's overlap check fires locally
	// on EVERY peer whenever ANY player crosses it, local or remote. Each
	// peer just heals its own local player in response — no RPC needed, the
	// per-peer local heals add up to the whole squad getting healed at once.
	private void HealLocalPlayer()
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is Sam sam && (!sam.IsNetworked || sam.IsMultiplayerAuthority()))
			{
				sam.GetNodeOrNull<HealthComponent>("HealthComponent")?.Revive();
				break;
			}
		}
	}

	private void SetActivatedVisual()
	{
		_activated = true;
		if (_light != null)
		{
			_light.Color = ActiveColor;
			_light.Energy = 1.2f;
		}
	}
}
