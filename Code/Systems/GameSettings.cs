using Godot;

// Autoload singleton holding user-facing settings that persist across
// sessions (saved to user://settings.cfg). Anything that needs to react to
// a setting — CameraSystem's steady_cam, audio bus volume — reads from
// here rather than each scene carrying its own copy that resets on reload.
public partial class GameSettings : Node
{
	private const string SettingsPath = "user://settings.cfg";
	private const string SectionMain = "settings";

	[Signal] public delegate void SettingsChangedEventHandler();

	// Defaults match what the game already shipped with, so existing
	// players see no behavior change until they touch a setting.
	public float MasterVolumeLinear { get; private set; } = 1.0f;
	public float MusicVolumeLinear { get; private set; } = 1.0f;
	public float SFXVolumeLinear { get; private set; } = 1.0f;
	public float VoiceVolumeLinear { get; private set; } = 1.0f;
	public bool SteadyCamEnabled { get; private set; } = true;
	public bool FullscreenEnabled { get; private set; } = false;
	public Vector2I WindowResolution { get; private set; } = new Vector2I(1280, 720);
	public bool VsyncEnabled { get; private set; } = true;
	public int MaxFps { get; private set; } = 0; // 0 = uncapped
	// Local-only — never touches the network. Gated at the receiving end in
	// Narrator.gd's _apply_bark, not at the bark-authority's decision step,
	// so other players who haven't muted idle chatter still get it normally.
	public bool IdleBarksMuted { get; private set; } = false;
	// Same shape/spirit as IdleBarksMuted — a local-only gate read directly
	// at the point of use (VoiceChatManager._Process) rather than pushed
	// into any engine subsystem. Disabling this stops PushToTalk from
	// capturing/sending at all; it does not affect whether YOU can hear
	// others (that's VoiceVolumeLinear/the Voice bus mute).
	public bool VoiceChatDisabled { get; private set; } = false;
	// "Default" matches AudioServer's own default input device sentinel —
	// picks whatever the OS considers the default recording device. Only
	// meaningful once VoiceChatManager's mic player exists, but this is
	// applied unconditionally in _Ready() regardless of whether a voice
	// session is active, so the choice is already in effect the moment one
	// starts.
	public string MicDeviceName { get; private set; } = "Default";
	// Boosts/attenuates the captured mic signal itself before it's packed and
	// sent — distinct from VoiceVolumeLinear, which only scales what you HEAR
	// of other people. Goes above 1.0 (up to 2x) since a quiet mic often needs
	// real boost, not just "up to full input level". Applied directly to the
	// captured samples in VoiceChatManager, both for the outgoing PCM and for
	// the settings-menu mic-test meter, so the meter reflects what actually
	// gets sent.
	public float MicGainLinear { get; private set; } = 1.0f;

	// Recolors every CRT-shaded overlay (menus' CRTOverlay.tscn, SamHUD's
	// corner panel) — a plain index rather than a C# enum so GDScript's
	// OptionButton.item_selected (which hands back a plain int) can drive
	// SetCrtTheme directly, matching the existing MaxFps/WindowResolution
	// "array of options + selected index" pattern in this same file. Each
	// CRT-using node reads CrtThemeColors[CrtThemeIndex] itself (in _ready
	// and on SettingsChanged) rather than this class reaching into scenes
	// it has no reference to.
	public string[] CrtThemeNames { get; private set; } = { "GREEN", "PURPLE", "BLACK", "BLUE", "YELLOW" };

	public Godot.Collections.Array<Color> CrtThemeColors { get; private set; } = new Godot.Collections.Array<Color>
	{
		new Color(0.65f, 1.00f, 0.75f), // Green — original phosphor-green look
		new Color(0.75f, 0.55f, 1.00f), // Purple
		new Color(0.85f, 0.87f, 0.90f), // Black — monochrome/white-phosphor monitor
		new Color(0.55f, 0.75f, 1.00f), // Blue
		new Color(1.00f, 0.85f, 0.45f), // Yellow — classic amber terminal
	};

	public int CrtThemeIndex { get; private set; } = 0;

	// Instance properties rather than static — GDScript's cross-language
	// property binding only sees members on the Godot object instance, so a
	// static field would silently fail to resolve from SettingsPanel.gd.
	// Godot.Collections.Array<T> rather than a raw C# Vector2I[] — there is
	// no native PackedVector2IArray Variant type, so a plain C# array of
	// Vector2I doesn't get a property binding generated for it at all and
	// stays invisible to GDScript regardless of the get/set accessors.
	public Godot.Collections.Array<Vector2I> AvailableResolutions { get; private set; } = new Godot.Collections.Array<Vector2I>
	{
		new Vector2I(1280, 720),
		new Vector2I(1600, 900),
		new Vector2I(1920, 1080),
		new Vector2I(2560, 1440),
	};

	public int[] AvailableFpsCaps { get; private set; } = { 0, 30, 60, 120, 144 };

	public override void _Ready()
	{
		Load();
		ApplyMasterVolume();
		ApplyMusicVolume();
		ApplySFXVolume();
		ApplyVoiceVolume();
		ApplyMicDevice();
		ApplyFullscreen();
		ApplyResolution();
		ApplyVsync();
		ApplyMaxFps();
	}

	public void SetMasterVolume(float linear)
	{
		MasterVolumeLinear = Mathf.Clamp(linear, 0f, 1f);
		ApplyMasterVolume();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetMusicVolume(float linear)
	{
		MusicVolumeLinear = Mathf.Clamp(linear, 0f, 1f);
		ApplyMusicVolume();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetSFXVolume(float linear)
	{
		SFXVolumeLinear = Mathf.Clamp(linear, 0f, 1f);
		ApplySFXVolume();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetVoiceVolume(float linear)
	{
		VoiceVolumeLinear = Mathf.Clamp(linear, 0f, 1f);
		ApplyVoiceVolume();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetMicDevice(string deviceName)
	{
		MicDeviceName = string.IsNullOrEmpty(deviceName) ? "Default" : deviceName;
		ApplyMicDevice();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetMicGain(float linear)
	{
		MicGainLinear = Mathf.Clamp(linear, 0f, 2f);
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetIdleBarksMuted(bool muted)
	{
		IdleBarksMuted = muted;
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetVoiceChatDisabled(bool disabled)
	{
		VoiceChatDisabled = disabled;
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetCrtTheme(int index)
	{
		CrtThemeIndex = Mathf.Clamp(index, 0, CrtThemeColors.Count - 1);
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetSteadyCam(bool enabled)
	{
		SteadyCamEnabled = enabled;
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetFullscreen(bool enabled)
	{
		FullscreenEnabled = enabled;
		ApplyFullscreen();
		// Fullscreen ignores WindowResolution entirely (it uses the desktop's
		// native size), so dropping back to windowed needs the chosen
		// resolution re-applied — it was never "un-set", just moot while
		// fullscreen was on.
		ApplyResolution();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetResolution(Vector2I resolution)
	{
		WindowResolution = resolution;
		ApplyResolution();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetVsync(bool enabled)
	{
		VsyncEnabled = enabled;
		ApplyVsync();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	public void SetMaxFps(int fps)
	{
		MaxFps = fps;
		ApplyMaxFps();
		Save();
		EmitSignal(SignalName.SettingsChanged);
	}

	private void ApplyMasterVolume()
	{
		int busIndex = AudioServer.GetBusIndex("Master");
		AudioServer.SetBusVolumeDb(busIndex, Mathf.LinearToDb(Mathf.Max(MasterVolumeLinear, 0.0001f)));
		AudioServer.SetBusMute(busIndex, MasterVolumeLinear <= 0.0001f);
	}

	private void ApplyMusicVolume()
	{
		int busIndex = AudioServer.GetBusIndex("Music");
		AudioServer.SetBusVolumeDb(busIndex, Mathf.LinearToDb(Mathf.Max(MusicVolumeLinear, 0.0001f)));
		AudioServer.SetBusMute(busIndex, MusicVolumeLinear <= 0.0001f);
	}

	private void ApplySFXVolume()
	{
		int busIndex = AudioServer.GetBusIndex("SFX");
		AudioServer.SetBusVolumeDb(busIndex, Mathf.LinearToDb(Mathf.Max(SFXVolumeLinear, 0.0001f)));
		AudioServer.SetBusMute(busIndex, SFXVolumeLinear <= 0.0001f);
	}

	private void ApplyVoiceVolume()
	{
		int busIndex = AudioServer.GetBusIndex("Voice");
		AudioServer.SetBusVolumeDb(busIndex, Mathf.LinearToDb(Mathf.Max(VoiceVolumeLinear, 0.0001f)));
		AudioServer.SetBusMute(busIndex, VoiceVolumeLinear <= 0.0001f);
	}

	// A saved device name that no longer exists (unplugged mic, moved to a
	// different machine) is passed straight through — AudioServer silently
	// falls back to the actual default device rather than erroring, so this
	// never needs to validate against GetInputDeviceList() itself.
	private void ApplyMicDevice()
	{
		AudioServer.InputDevice = MicDeviceName;
	}

	private void ApplyFullscreen()
	{
		DisplayServer.WindowSetMode(FullscreenEnabled
			? DisplayServer.WindowMode.Fullscreen
			: DisplayServer.WindowMode.Windowed);
	}

	private void ApplyResolution()
	{
		if (FullscreenEnabled) return;

		DisplayServer.WindowSetSize(WindowResolution);

		Vector2I screenSize = DisplayServer.ScreenGetSize();
		DisplayServer.WindowSetPosition((screenSize - WindowResolution) / 2);
	}

	private void ApplyVsync()
	{
		DisplayServer.WindowSetVsyncMode(VsyncEnabled
			? DisplayServer.VSyncMode.Enabled
			: DisplayServer.VSyncMode.Disabled);
	}

	private void ApplyMaxFps()
	{
		Engine.MaxFps = MaxFps;
	}

	private void Load()
	{
		var config = new ConfigFile();
		if (config.Load(SettingsPath) != Error.Ok) return;

		MasterVolumeLinear = (float)config.GetValue(SectionMain, "master_volume", MasterVolumeLinear);
		MusicVolumeLinear = (float)config.GetValue(SectionMain, "music_volume", MusicVolumeLinear);
		SFXVolumeLinear = (float)config.GetValue(SectionMain, "sfx_volume", SFXVolumeLinear);
		VoiceVolumeLinear = (float)config.GetValue(SectionMain, "voice_volume", VoiceVolumeLinear);
		IdleBarksMuted = (bool)config.GetValue(SectionMain, "idle_barks_muted", IdleBarksMuted);
		VoiceChatDisabled = (bool)config.GetValue(SectionMain, "voice_chat_disabled", VoiceChatDisabled);
		MicDeviceName = (string)config.GetValue(SectionMain, "mic_device", MicDeviceName);
		MicGainLinear = (float)config.GetValue(SectionMain, "mic_gain", MicGainLinear);
		CrtThemeIndex = (int)config.GetValue(SectionMain, "crt_theme_index", CrtThemeIndex);
		SteadyCamEnabled = (bool)config.GetValue(SectionMain, "steady_cam", SteadyCamEnabled);
		FullscreenEnabled = (bool)config.GetValue(SectionMain, "fullscreen", FullscreenEnabled);
		WindowResolution = (Vector2I)config.GetValue(SectionMain, "resolution", WindowResolution);
		VsyncEnabled = (bool)config.GetValue(SectionMain, "vsync", VsyncEnabled);
		MaxFps = (int)config.GetValue(SectionMain, "max_fps", MaxFps);
	}

	private void Save()
	{
		var config = new ConfigFile();
		config.SetValue(SectionMain, "master_volume", MasterVolumeLinear);
		config.SetValue(SectionMain, "music_volume", MusicVolumeLinear);
		config.SetValue(SectionMain, "sfx_volume", SFXVolumeLinear);
		config.SetValue(SectionMain, "voice_volume", VoiceVolumeLinear);
		config.SetValue(SectionMain, "idle_barks_muted", IdleBarksMuted);
		config.SetValue(SectionMain, "voice_chat_disabled", VoiceChatDisabled);
		config.SetValue(SectionMain, "mic_device", MicDeviceName);
		config.SetValue(SectionMain, "mic_gain", MicGainLinear);
		config.SetValue(SectionMain, "crt_theme_index", CrtThemeIndex);
		config.SetValue(SectionMain, "steady_cam", SteadyCamEnabled);
		config.SetValue(SectionMain, "fullscreen", FullscreenEnabled);
		config.SetValue(SectionMain, "resolution", WindowResolution);
		config.SetValue(SectionMain, "vsync", VsyncEnabled);
		config.SetValue(SectionMain, "max_fps", MaxFps);
		config.Save(SettingsPath);
	}
}
