using Godot;
using System.Collections.Generic;

public partial class PressurePlateComponent : Area2D
{
	// Existing exported properties
	[Export] public string RequiredGroup { get; set; } = "pushable";
	[Export] public bool InvertOutput { get; set; } = false;

	[Export] public NodePath SpritePath { get; set; }
	[Export] public NodePath TargetNodePath { get; set; }
	[Export] public AudioStream ClickSound { get; set; }

	private readonly HashSet<Node2D> _pressingBodies = new();
	private bool _isPressed = false;
	private CpuParticles2D _dustParticles;
	private Timer _timer;
	private Sprite2D _sprite;
	private AudioStreamPlayer2D _clickAudio;

	[Signal] public delegate void PressureChangedEventHandler(bool pressed);
	[Signal] public delegate void PlatePressedEventHandler();
	[Signal] public delegate void PlateReleasedEventHandler();

	public override void _Ready()
	{
		_dustParticles = GetNodeOrNull<CpuParticles2D>("DustParticles");
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;

		// Retrieve optional timer (named "PlateTimer") and configure it
		_timer = GetNodeOrNull<Timer>("PlateTimer");
		if (_timer != null)
		{
			_timer.OneShot = true;
			_timer.WaitTime = 1.0f; // 1 second timeout for release
			_timer.Timeout += OnReleaseTimerTimeout;
		}

		// Retrieve optional sprite for visual feedback
		if (SpritePath != null && !SpritePath.IsEmpty)
		{
			_sprite = GetNodeOrNull<Sprite2D>(SpritePath);
		}

		_clickAudio = GetNodeOrNull<AudioStreamPlayer2D>("ClickAudio");
	}

	private void OnBodyEntered(Node2D body)
	{
		if (!BodyCanPress(body)) return;

		_pressingBodies.Add(body);
		UpdatePressedState();
	}

	private void OnBodyExited(Node2D body)
	{
		if (_pressingBodies.Remove(body))
		{
			UpdatePressedState();
		}
	}

	private bool BodyCanPress(Node2D body)
	{
		return body != null && (string.IsNullOrEmpty(RequiredGroup) || body.IsInGroup(RequiredGroup));
	}

	private void UpdatePressedState()
	{
		bool nextPressed = _pressingBodies.Count > 0;
		if (nextPressed == _isPressed) return;

		_isPressed = nextPressed;
		bool output = InvertOutput ? !_isPressed : _isPressed;
		EmitSignal(SignalName.PressureChanged, output);

		if (_clickAudio != null && ClickSound != null)
		{
			_clickAudio.Stream = ClickSound;
			_clickAudio.Play();
		}

		if (_isPressed)
		{
			EmitSignal(SignalName.PlatePressed);
			// Cancel any pending release timer
			_timer?.Stop();
			// Set sprite region X to 16.0 for visual feedback
			SetSpriteRegionX(16.0f);
			// Activate the target node if applicable
			ActivateTarget(true);
		}
		else
		{
			EmitSignal(SignalName.PlateReleased);
			// Start the release timer (1 second) to revert sprite and deactivate target
			_timer?.Start();
		}

		if (_dustParticles != null)
		{
			_dustParticles.Emitting = false;
			_dustParticles.Restart();
			_dustParticles.Emitting = true;
		}
	}

	// Called when the release timer finishes
	private void OnReleaseTimerTimeout()
	{
		// Revert sprite region X to 0.0 and deactivate target
		SetSpriteRegionX(0.0f);
		ActivateTarget(false);
	}

	// Helper to set the X component of the Sprite2D's region rect
	private void SetSpriteRegionX(float xValue)
	{
		if (_sprite == null) return;
		var rect = _sprite.RegionRect;
		rect.Position = new Vector2(xValue, rect.Position.Y);
		_sprite.RegionRect = rect;
	}

	// Activates or deactivates the selected target node.
	// If the node implements a method named "Powered" we call it with the boolean state.
	private void ActivateTarget(bool active)
	{
		if (TargetNodePath == null || TargetNodePath.IsEmpty) return;
		var target = GetNodeOrNull<Node>(TargetNodePath);
		if (target == null) return;
		if (target.HasMethod("Powered"))
		{
			// Call the method with the desired state
			target.Call("Powered", active);
		}
	}
}
