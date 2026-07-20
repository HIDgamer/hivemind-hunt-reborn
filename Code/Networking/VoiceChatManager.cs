using Godot;
using System.Collections.Generic;

// Autoload (see project.godot). Push-to-talk proximity voice chat: captures
// the local mic while PushToTalk is held, relays it client -> server ->
// everyone (mirrors ChatBox's relay shape), and hands decoded chunks off to
// the sender's own Sam node for playback — Sam owns an AudioStreamPlayer2D
// so proximity falloff/panning comes for free from Godot's normal 2D
// positional audio instead of any hand-rolled distance math here.
public partial class VoiceChatManager : Node
{
	// Fixed wire sample rate, independent of either peer's actual audio
	// device rate. Two players' machines are not guaranteed to share a
	// native mix rate (44100 vs 48000 is common) — if playback assumed
	// "whatever rate the sender captured at," a mismatch would play back
	// pitched/sped up or slowed down. Capture always decimates DOWN to this
	// constant, and playback's AudioStreamGenerator always runs at exactly
	// this rate, so the two sides never need to negotiate anything.
	public const int WireSampleRateHz = 22050;

	private const float ChunkIntervalSeconds = 0.05f;

	private AudioStreamPlayer _micPlayer;
	private AudioEffectCapture _captureEffect;
	private readonly List<byte> _pendingBytes = new();
	private float _chunkTimer;
	private int _decimationFactor = 1;
	private bool _isCapturing;
	// Settings menu "hold to test" — samples the mic and exposes a level for
	// a meter bar without transmitting anything or requiring a live session
	// (PushToTalk capture below is gated on IsNetworked; this isn't, since
	// it's just a local hardware check).
	private bool _testModeActive;

	// 0..1, smoothed (fast attack / slow release so it reads as a level
	// meter instead of flickering with every sample). Read by
	// SettingsPanel.gd while the mic test button is held.
	public float CurrentInputLevel { get; private set; }

	// Temporary while shaking out the capture/relay pipeline — flip to false
	// once voice chat is confirmed working end-to-end to quiet the console
	// back down. The one-time _Ready summary and the not-allowed notice
	// below stay regardless, since they're low-frequency and cheap.
	private const bool VerboseLogging = true;

	public override void _Ready()
	{
		int captureBusIndex = AudioServer.GetBusIndex("VoiceCapture");
		if (captureBusIndex < 0)
		{
			GD.PushWarning("VoiceChatManager: 'VoiceCapture' bus not found in the active audio bus layout — mic capture cannot work until this exists. Check default_bus_layout.tres was actually picked up (a full editor/game restart after editing it, not just re-running, may be needed).");
		}

		_captureEffect = new AudioEffectCapture();
		AudioServer.AddBusEffect(captureBusIndex, _captureEffect);

		// Not playing yet — Play()/Stop() are driven entirely by PushToTalk,
		// see _Process. The VoiceCapture bus routes normally to Master at
		// -80dB (see default_bus_layout.tres) rather than being muted with
		// no send target — an earlier version used mute+no-send instead,
		// which is untested/undocumented territory for whether Godot still
		// runs a muted bus's effect chain at all. A normally-routed bus at
		// an inaudible volume is unambiguously still fully processed, which
		// is what AudioEffectCapture actually needs to receive anything.
		_micPlayer = new AudioStreamPlayer
		{
			Bus = "VoiceCapture",
			Stream = new AudioStreamMicrophone(),
		};
		AddChild(_micPlayer);

		string[] devices = AudioServer.GetInputDeviceList();
		GD.Print($"VoiceChatManager ready. PushToTalk action exists: {InputMap.HasAction("PushToTalk")}. "
			+ $"Input devices seen by Godot: [{string.Join(", ", devices)}]. Current input device: '{AudioServer.InputDevice}'.");
		if (devices.Length == 0)
		{
			GD.PushWarning("VoiceChatManager: AudioServer reports zero input devices — check 'audio/driver/enable_input' is actually true and that an OS-level mic permission prompt wasn't silently denied.");
		}
	}

	public override void _Process(double delta)
	{
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		var settings = GetNodeOrNull<GameSettings>("/root/GameSettings");
		bool allowed = networkManager != null && networkManager.IsNetworked
			&& (settings == null || !settings.VoiceChatDisabled);

		if (Input.IsActionJustPressed("PushToTalk"))
		{
			if (allowed)
			{
				StartCapture();
			}
			else
			{
				GD.Print($"VoiceChatManager: PushToTalk pressed but blocked — IsNetworked={networkManager?.IsNetworked ?? false}, VoiceChatDisabled={settings?.VoiceChatDisabled ?? false}.");
			}
		}
		else if (Input.IsActionJustReleased("PushToTalk") || (_isCapturing && !allowed))
		{
			StopCapture();
		}

		if (!_isCapturing && !_testModeActive)
		{
			CurrentInputLevel = 0f;
			return;
		}

		PullCapturedFrames();

		if (!_isCapturing) return;

		_chunkTimer += (float)delta;
		if (_chunkTimer >= ChunkIntervalSeconds)
		{
			_chunkTimer = 0f;
			FlushChunk();
		}
	}

	// Called by SettingsPanel.gd on its mic-test button's button_down/
	// button_up — a pure local hardware check, independent of PushToTalk,
	// IsNetworked, or VoiceChatDisabled (you should be able to verify your
	// mic works even from the main menu, with nobody to talk to).
	public void SetTestModeActive(bool active)
	{
		_testModeActive = active;
		RefreshMicPlayingState();
	}

	private void StartCapture()
	{
		if (_isCapturing) return;
		_isCapturing = true;
		_pendingBytes.Clear();
		_chunkTimer = 0f;
		RefreshMicPlayingState();

		Sam localSam = GetLocalSam();
		GD.Print($"VoiceChatManager: capture started (mix rate {AudioServer.GetMixRate()}, decimation x{_decimationFactor}, local Sam found: {localSam != null}).");
		if (localSam != null) localSam.NetIsSpeaking = true;
	}

	private void StopCapture()
	{
		if (!_isCapturing) return;
		_isCapturing = false;
		// Send whatever's left so the tail end of a word isn't dropped
		// before the mic is potentially stopped below.
		FlushChunk();
		RefreshMicPlayingState();
		GD.Print("VoiceChatManager: capture stopped.");

		Sam localSam = GetLocalSam();
		if (localSam != null) localSam.NetIsSpeaking = false;
	}

	// Both real capture and the settings-menu mic test share one physical
	// AudioStreamPlayer/AudioEffectCapture — this starts/stops it based on
	// whether EITHER wants it active, rather than each flag independently
	// calling Play()/Stop() and potentially fighting each other.
	private void RefreshMicPlayingState()
	{
		bool shouldBeActive = _isCapturing || _testModeActive;
		if (shouldBeActive && !_micPlayer.Playing)
		{
			// Recomputed on every activation rather than cached once — the
			// OS/engine mix rate is stable in practice, but this is cheap
			// and removes any doubt about it going stale across a device
			// change mid-session.
			_decimationFactor = Mathf.Max(1, Mathf.RoundToInt(AudioServer.GetMixRate() / WireSampleRateHz));
			_micPlayer.Play();
		}
		else if (!shouldBeActive && _micPlayer.Playing)
		{
			_micPlayer.Stop();
			// Drop any trailing buffered audio so the next activation starts
			// clean instead of replaying stale samples from before this one
			// stopped.
			_captureEffect.ClearBuffer();
			CurrentInputLevel = 0f;
		}
	}

	private Sam GetLocalSam()
	{
		var networkManager = GetNodeOrNull<NetworkManager>("/root/NetworkManager");
		if (networkManager == null || !networkManager.IsNetworked) return null;

		Node playersRoot = GetTree().CurrentScene?.GetNodeOrNull("PlayersRoot");
		return playersRoot?.GetNodeOrNull(Multiplayer.GetUniqueId().ToString()) as Sam;
	}

	// Raw mic input captured via AudioEffectCapture sits far below full scale
	// under normal speaking volume (typical speech peaks land around -20dB
	// to -6dB, i.e. roughly 0.1-0.5 amplitude) — without a baseline boost,
	// the signal actually being sent is just quiet, and no amount of
	// receiver-side Voice bus volume (which only ever multiplies DOWN from
	// unity at 100%) can fix that after the fact. This is applied before the
	// user's own MicGainLinear (0-2x fine-tune on top), matching the ~+10 to
	// +12dB boosts this project's own SFX players already use for one-shot
	// sounds (e.g. Sam's JumpPlayer/LandPlayer volume_db).
	private const float BaseCaptureGain = 6f;

	private void PullCapturedFrames()
	{
		int available = _captureEffect.GetFramesAvailable();
		if (available <= 0) return;

		Vector2[] frames = _captureEffect.GetBuffer(available);
		float gain = BaseCaptureGain * (GetNodeOrNull<GameSettings>("/root/GameSettings")?.MicGainLinear ?? 1f);

		float peak = 0f;
		for (int i = 0; i < frames.Length; i++)
		{
			float sample = Mathf.Abs((frames[i].X + frames[i].Y) * 0.5f) * gain;
			if (sample > peak) peak = sample;
		}
		// Clamped to 1 — the meter shouldn't read past "full" just because
		// gain is cranked, even though the actual PCM samples below clip at
		// their own Int16 range independently.
		CurrentInputLevel = Mathf.Min(peak > CurrentInputLevel ? peak : Mathf.Lerp(CurrentInputLevel, peak, 0.15f), 1f);

		if (!_isCapturing) return;

		// Naive decimation (no anti-aliasing filter) — an accepted quality
		// tradeoff for v1 to keep this simple; still intelligible for voice
		// and cheap to compute every frame.
		for (int i = 0; i < frames.Length; i += _decimationFactor)
		{
			float sample = (frames[i].X + frames[i].Y) * 0.5f * gain;
			short pcm = (short)Mathf.Clamp(sample * 32767f, -32768f, 32767f);
			_pendingBytes.Add((byte)(pcm & 0xFF));
			_pendingBytes.Add((byte)((pcm >> 8) & 0xFF));
		}
	}

	private void FlushChunk()
	{
		if (_pendingBytes.Count == 0) return;
		byte[] chunk = _pendingBytes.ToArray();
		_pendingBytes.Clear();
		if (VerboseLogging) GD.Print($"VoiceChatManager: sending {chunk.Length}-byte voice chunk to server.");
		RpcId(1, MethodName.SubmitVoiceServer, chunk);
	}

	// Targets the server directly (RpcId) instead of a broadcast Rpc() like
	// ChatBox's text submit — voice sends ~20 packets/sec while held, and a
	// broadcast submit would waste bandwidth re-sending the sender's own
	// mic audio to every OTHER client too, who would just discard it since
	// they aren't the server. A client's only connection is the server
	// anyway, so RpcId(1, ...) costs a client nothing extra; it only avoids
	// needless traffic when the HOST is the one talking.
	[Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void SubmitVoiceServer(byte[] chunk)
	{
		if (!Multiplayer.IsServer()) return;

		// Correct even when the server is the one talking (a same-machine
		// RpcId(1, ...) call, not a real network hop) — same
		// GetRemoteSenderId()-during-local-invocation behavior already
		// confirmed correct for ChatBox.SubmitChatServer.
		long senderId = Multiplayer.GetRemoteSenderId();
		if (VerboseLogging) GD.Print($"VoiceChatManager: server received {chunk.Length} bytes from peer {senderId}, relaying.");
		Rpc(MethodName.BroadcastVoice, senderId, chunk);
	}

	[Rpc(MultiplayerApi.RpcMode.Authority, CallLocal = true, TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
	private void BroadcastVoice(long senderId, byte[] chunk)
	{
		// Never play back your own mic — every peer (including the sender,
		// via CallLocal) receives this broadcast.
		if (senderId == Multiplayer.GetUniqueId()) return;

		Node playersRoot = GetTree().CurrentScene?.GetNodeOrNull("PlayersRoot");
		if (playersRoot?.GetNodeOrNull(senderId.ToString()) is Sam sam)
		{
			if (VerboseLogging) GD.Print($"VoiceChatManager: dispatching {chunk.Length} bytes to Sam '{senderId}' for playback.");
			sam.ReceiveVoiceChunk(chunk);
		}
		else
		{
			GD.PushWarning($"VoiceChatManager: received a voice chunk for peer {senderId} but no Sam found under PlayersRoot — dropped.");
		}
	}
}
