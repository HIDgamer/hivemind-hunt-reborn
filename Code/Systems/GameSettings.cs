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
	public bool SteadyCamEnabled { get; private set; } = true;
	public bool FullscreenEnabled { get; private set; } = false;
	public Vector2I WindowResolution { get; private set; } = new Vector2I(1280, 720);
	public bool VsyncEnabled { get; private set; } = true;
	public int MaxFps { get; private set; } = 0; // 0 = uncapped

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
		config.SetValue(SectionMain, "steady_cam", SteadyCamEnabled);
		config.SetValue(SectionMain, "fullscreen", FullscreenEnabled);
		config.SetValue(SectionMain, "resolution", WindowResolution);
		config.SetValue(SectionMain, "vsync", VsyncEnabled);
		config.SetValue(SectionMain, "max_fps", MaxFps);
		config.Save(SettingsPath);
	}
}
