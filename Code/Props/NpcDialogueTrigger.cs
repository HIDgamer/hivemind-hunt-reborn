using Godot;
using Godot.Collections;

// Area2D placed on any NPC/terminal prop. While Sam is in range, pressing
// Interact opens DialogueUI (autoload) with this node's configured Lines.
// Each entry in Lines is a Dictionary — {"speaker": "...", "text": "..."} —
// editable directly in the Inspector as an Array of Dictionaries; Portrait
// is shared by every line here (set per-trigger, not per-line) since most
// NPCs don't switch faces mid-conversation.
public partial class NpcDialogueTrigger : Area2D
{
	[Export] public Texture2D Portrait;
	[Export] public string SpeakerName = "UNKNOWN";
	[Export] public Array<string> Lines = new();
	// Re-opens dialogue every time you interact rather than only once.
	[Export] public bool Repeatable = true;

	private bool _playerInRange;
	private bool _hasSpoken;
	private Node2D _playerInRangeNode;
	// The Z press that advances the final line and the Z press that opens a
	// fresh conversation are read by two different consumers: DialogueBox
	// closes on the event (_unhandled_input), this polls IsActionJustPressed
	// in _Process. Godot's "input handled" flag only stops event
	// propagation — it does nothing to the separate polling state — so
	// IsActionJustPressed was still true later the same frame and instantly
	// reopened whatever had just closed. Require an actual release before a
	// new press counts, so the single press that ends dialogue can never
	// also be read as the press that starts the next one.
	private bool _waitingForRelease;

	public override void _Ready()
	{
		BodyEntered += OnBodyEntered;
		BodyExited += OnBodyExited;
	}

	public override void _Process(double delta)
	{
		if (_waitingForRelease)
		{
			if (!Input.IsActionPressed("Interact"))
			{
				_waitingForRelease = false;
			}
			return;
		}

		if (!_playerInRange || _playerInRangeNode == null) return;
		if (!Repeatable && _hasSpoken) return;

		var dialogueUi = GetNodeOrNull<CanvasLayer>("/root/DialogueUI");
		if (dialogueUi == null || (bool)dialogueUi.Call("is_active")) return;

		if (Input.IsActionJustPressed("Interact"))
		{
			OpenDialogue(dialogueUi);
			_waitingForRelease = true;
		}
	}

	private void OpenDialogue(CanvasLayer dialogueUi)
	{
		var lines = new Array();
		foreach (string text in Lines)
		{
			lines.Add(new Dictionary
			{
				{ "speaker", SpeakerName },
				{ "portrait", Portrait },
				{ "text", text },
			});
		}
		dialogueUi.Call("start_dialogue", lines, _playerInRangeNode);
		_hasSpoken = true;
	}

	private void OnBodyEntered(Node2D body)
	{
		if (body is not Sam sam) return;
		if (sam.IsNetworked && !sam.IsMultiplayerAuthority()) return;
		_playerInRange = true;
		_playerInRangeNode = sam;
	}

	private void OnBodyExited(Node2D body)
	{
		if (body != _playerInRangeNode) return;
		_playerInRange = false;
		_playerInRangeNode = null;
	}
}
