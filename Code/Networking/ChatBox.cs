using Godot;

// Autoload (see project.godot). Enter opens a text field; Enter again sends;
// Escape cancels. Messages relay client -> server -> everyone (a client
// can't broadcast directly — only the authority/server may originate an Rpc
// that reaches every peer, so SubmitChatServer always runs server-side and
// re-emits via BroadcastChat, which every peer — including the server
// itself, via CallLocal — receives).
public partial class ChatBox : CanvasLayer
{
	private const int MaxLogLines = 8;
	private const float MessageLifetime = 8f;

	private LineEdit _input;
	private VBoxContainer _log;
	private Control _panel;

	public override void _Ready()
	{
		_panel = GetNode<Control>("Panel");
		_input = GetNode<LineEdit>("Panel/Input");
		_log = GetNode<VBoxContainer>("Panel/Log");

		_input.Visible = false;
		_input.TextSubmitted += OnTextSubmitted;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.Enter && !_input.Visible)
			{
				OpenInput();
				GetViewport().SetInputAsHandled();
			}
			else if (key.Keycode == Key.Escape && _input.Visible)
			{
				CloseInput();
				GetViewport().SetInputAsHandled();
			}
		}
	}

	private void OpenInput()
	{
		_input.Visible = true;
		_input.Text = "";
		_input.GrabFocus();
		SetLocalInputCaptured(true);
	}

	private void CloseInput()
	{
		_input.Visible = false;
		_input.ReleaseFocus();
		SetLocalInputCaptured(false);
	}

	private void SetLocalInputCaptured(bool captured)
	{
		foreach (Node node in GetTree().GetNodesInGroup("Player"))
		{
			if (node is Sam sam && (!sam.IsNetworked || sam.IsMultiplayerAuthority()))
			{
				sam.UiInputCaptured = captured;
			}
		}
	}

	private void OnTextSubmitted(string text)
	{
		text = text.Trim();
		CloseInput();
		if (text.Length == 0) return;

		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		bool networked = networkManager != null && (networkManager.IsNetworked || networkManager.HasPendingJoin);

		if (!networked)
		{
			// Offline: just echo locally so the UI is testable without a session.
			AppendLine(networkManager?.LocalPlayerName ?? "PLAYER", text);
			return;
		}

		Rpc(MethodName.SubmitChatServer, text);
	}

	[Rpc(MultiplayerApi.RpcMode.AnyPeer)]
	private void SubmitChatServer(string text)
	{
		if (!Multiplayer.IsServer()) return;

		string senderName = ResolveSenderName(Multiplayer.GetRemoteSenderId());
		Rpc(MethodName.BroadcastChat, senderName, text);
	}

	// The sender's own DisplayName is authoritative on their own Sam node —
	// looked up by peer id (node Name, see PlayerSpawner) rather than
	// trusting a name string in the chat packet itself.
	private string ResolveSenderName(long senderId)
	{
		Node playersRoot = GetTree().CurrentScene?.GetNodeOrNull("PlayersRoot");
		Node playerNode = playersRoot?.GetNodeOrNull(senderId.ToString());
		if (playerNode is Sam sam && !string.IsNullOrEmpty(sam.DisplayName))
		{
			return sam.DisplayName;
		}
		return $"PLAYER-{senderId}";
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true)]
	private void BroadcastChat(string senderName, string text)
	{
		AppendLine(senderName, text);
	}

	// Chat text is untrusted (typed by any peer) and rendered through a
	// BBCode-enabled label — without this, a message containing "[" could
	// inject formatting tags (or, more maliciously, [url]/[img]) into every
	// other player's chat log.
	private static string EscapeBbcode(string text) => text.Replace("[", "[lb]");

	private void AppendLine(string senderName, string text)
	{
		var label = new RichTextLabel
		{
			BbcodeEnabled = true,
			FitContent = true,
			ScrollActive = false,
			MouseFilter = Control.MouseFilterEnum.Ignore,
		};
		label.AppendText($"[color=#8cffb0]{senderName.ToUpper()}:[/color] {EscapeBbcode(text)}");
		_log.AddChild(label);

		while (_log.GetChildCount() > MaxLogLines)
		{
			_log.GetChild(0).QueueFree();
		}

		Tween tween = CreateTween();
		tween.TweenInterval(MessageLifetime);
		tween.TweenProperty(label, "modulate:a", 0f, 0.6f);
		tween.TweenCallback(Callable.From(() =>
		{
			if (IsInstanceValid(label)) label.QueueFree();
		}));
	}
}
