using Godot;

// Autoload (see project.godot). Enter opens a text field; Enter again sends;
// Escape cancels. Messages relay client -> server -> everyone (a client
// can't broadcast directly — only the authority/server may originate an Rpc
// that reaches every peer, so SubmitChatServer always runs server-side and
// re-emits via BroadcastChat, which every peer — including the server
// itself, via CallLocal — receives).
public partial class ChatBox : CanvasLayer
{
	// A real cap on actual history, not just how many labels happen to be
	// alive — previously the "log" was just whatever hadn't timed out yet,
	// so there was no scrollback to speak of. 100 messages is generous for
	// a 4-player session without the label list growing unbounded.
	private const int MaxHistoryLines = 100;
	private const float MessageLifetime = 8f;
	private const float FadeOutDuration = 0.6f;
	private const float FadeInDuration = 0.15f;

	private LineEdit _input;
	private VBoxContainer _log;
	private ScrollContainer _logScroll;
	private Control _panel;

	// Whole log fades out together after inactivity (matches the old
	// per-message fade's "gets out of the way" feel) rather than each
	// message dimming to a permanent half-visible state and cluttering the
	// screen forever — the backing labels themselves stay at full opacity
	// and simply accumulate up to MaxHistoryLines, so scrollback is intact,
	// it's just hidden until something brings the log back into view.
	private float _idleSecondsLeft;
	private Tween _logFadeTween;

	public override void _Ready()
	{
		_panel = GetNode<Control>("Panel");
		_input = GetNode<LineEdit>("Panel/Input");
		_logScroll = GetNode<ScrollContainer>("Panel/LogScroll");
		_log = GetNode<VBoxContainer>("Panel/LogScroll/Log");

		_input.Visible = false;
		_input.TextSubmitted += OnTextSubmitted;

		// Nothing to show yet — start hidden instead of waiting out a full
		// MessageLifetime fade with an empty log visible.
		_logScroll.Modulate = new Color(1f, 1f, 1f, 0f);
		// Scrolling to actually read history counts as activity too — it
		// shouldn't fade away out from under someone mid-scroll.
		_logScroll.GetVScrollBar().ValueChanged += _ => RevealLog();

		// A CallDeferred from AppendLine risked firing before the VBoxContainer
		// had actually finished resizing to include the new (possibly
		// multi-line, FitContent) label, landing one message short of the
		// real bottom — Resized fires exactly when the container's true final
		// size is known, whether that's this frame or a later one, so this is
		// never stale.
		_log.Resized += ScrollToBottom;
	}

	public override void _Process(double delta)
	{
		if (_idleSecondsLeft <= 0f) return;

		_idleSecondsLeft -= (float)delta;
		if (_idleSecondsLeft <= 0f)
		{
			FadeLog(0f, FadeOutDuration);
		}
	}

	private void RevealLog()
	{
		_idleSecondsLeft = MessageLifetime;
		FadeLog(1f, FadeInDuration);
	}

	private void FadeLog(float targetAlpha, float duration)
	{
		if (_logFadeTween != null && _logFadeTween.IsValid())
		{
			_logFadeTween.Kill();
		}
		_logFadeTween = CreateTween();
		_logFadeTween.TweenProperty(_logScroll, "modulate:a", targetAlpha, duration);
	}

	// ChatBox is a global autoload with no scene-specific lifecycle of its
	// own, so without this, Enter opened a chat box that had nobody to talk
	// to and nothing sending on the main menu/lobby/settings — PlayersRoot is
	// the same "are we actually in a level" marker ResolveSenderName already
	// relies on (present in every level scene, absent from every menu scene).
	private bool IsInGameplayScene()
	{
		return GetTree().CurrentScene?.GetNodeOrNull("PlayersRoot") != null;
	}

	public override void _UnhandledInput(InputEvent @event)
	{
		if (@event is InputEventKey key && key.Pressed && !key.Echo)
		{
			if (key.Keycode == Key.Enter && !_input.Visible && IsInGameplayScene())
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
		RevealLog();
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

	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true)]
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

		while (_log.GetChildCount() > MaxHistoryLines)
		{
			_log.GetChild(0).QueueFree();
		}

		// Scroll-to-bottom itself is handled by the _log.Resized hook in
		// _Ready — adding this label always changes the VBoxContainer's
		// height, so that signal is guaranteed to fire after this.
		RevealLog();
	}

	private void ScrollToBottom()
	{
		_logScroll.ScrollVertical = (int)_logScroll.GetVScrollBar().MaxValue;
	}
}
