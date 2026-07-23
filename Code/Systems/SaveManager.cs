using Godot;
using Godot.Collections;

// Autoload — persists up to MaxSlots rotating autosaves to disk (user://saves/),
// each with a name, a timestamp, the scene + checkpoint position to resume at,
// and a small screenshot taken the moment the save happened. Triggered by
// CheckpointManager.SetCheckpoint whenever a genuinely new checkpoint is
// reached; the Load screen (MainMenu -> LoadGameMenu, GDScript) reads the slot
// list back to populate its rows and hands the chosen slot's scene path back
// to CheckpointManager via RequestRespawnOnLoad before changing scene.
//
// Slot info crosses to GDScript as a plain Dictionary rather than a C#
// struct/class — arbitrary C# types don't marshal through the scripting
// bridge, Godot's built-in Variant containers do.
public partial class SaveManager : Node
{
	public const int MaxSlots = 5;
	private const string SaveDir = "user://saves/";
	private const int ThumbnailWidth = 320;
	private const int ThumbnailHeight = 180;
	// Bump this whenever the save schema (the fields WriteSlot writes, or
	// what they mean) changes. A save written under an older version — or
	// one whose scene_path points at a level that no longer exists, e.g.
	// after a level got renamed/removed — gets treated as empty and quietly
	// deleted the next time it's read, rather than handing LoadSlot a
	// checkpoint that no longer resolves to anything and breaking the load.
	private const int SaveFormatVersion = 1;

	private struct SlotData
	{
		public bool Occupied;
		public string DisplayName;
		public string Timestamp;
		public string ScenePath;
		public Vector2 Position;
	}

	// Scene-authored checkpoints reach this through CheckpointManager, not
	// directly — keeps the "what triggers a save" decision in one place.
	public void Autosave(string scenePath, Vector2 position)
	{
		int slot = FindSlotToOverwrite();
		WriteSlot(slot, scenePath, position, $"AUTOSAVE {slot}");
	}

	// Pause menu's manual Save button — unlike Autosave, the caller (the
	// player, via the save/load menu) picks the exact slot, so an occupied
	// one is a deliberate overwrite rather than automatic rotation.
	public void ManualSave(int slot, string scenePath, Vector2 position)
	{
		WriteSlot(slot, scenePath, position, $"SAVE {slot}");
	}

	public void DeleteSlot(int slot)
	{
		string cfgPath = SlotConfigPath(slot);
		string pngPath = SlotThumbnailPath(slot);
		if (FileAccess.FileExists(cfgPath)) DirAccess.RemoveAbsolute(cfgPath);
		if (FileAccess.FileExists(pngPath)) DirAccess.RemoveAbsolute(pngPath);
	}

	public bool HasAnySave()
	{
		for (int i = 1; i <= MaxSlots; i++)
		{
			if (FileAccess.FileExists(SlotConfigPath(i))) return true;
		}
		return false;
	}

	public Array GetSlots()
	{
		var slots = new Array();
		for (int i = 1; i <= MaxSlots; i++)
		{
			SlotData data = ReadSlot(i);
			var dict = new Dictionary
			{
				["Slot"] = i,
				["Occupied"] = data.Occupied,
				["DisplayName"] = data.DisplayName ?? "",
				["Timestamp"] = data.Timestamp ?? "",
				["ScenePath"] = data.ScenePath ?? "",
				["ThumbnailPath"] = SlotThumbnailPath(i),
			};
			slots.Add(dict);
		}
		return slots;
	}

	// Populates CheckpointManager with this slot's scene/position and tells it
	// to hand that off to Sam the moment the destination scene is ready, then
	// hands the scene path back to the caller (MainMenu) to actually load —
	// SaveManager doesn't own scene transitions, MenuBase's fade/CRT flow does.
	// Returns an empty string if the slot is empty.
	public string LoadSlot(int slot)
	{
		SlotData data = ReadSlot(slot);
		if (!data.Occupied) return "";

		GetNode<CheckpointManager>("/root/CheckpointManager").RequestRespawnOnLoad(data.Position, data.ScenePath);
		return data.ScenePath;
	}

	private int FindSlotToOverwrite()
	{
		int oldestSlot = 1;
		ulong oldestTime = ulong.MaxValue;
		for (int i = 1; i <= MaxSlots; i++)
		{
			if (!FileAccess.FileExists(SlotConfigPath(i)))
			{
				return i; // empty slot always wins over overwriting a real one
			}

			var config = new ConfigFile();
			if (config.Load(SlotConfigPath(i)) != Error.Ok) continue;
			ulong savedAt = (ulong)config.GetValue("save", "unix_time", 0);
			if (savedAt < oldestTime)
			{
				oldestTime = savedAt;
				oldestSlot = i;
			}
		}
		return oldestSlot;
	}

	private void WriteSlot(int slot, string scenePath, Vector2 position, string displayName)
	{
		DirAccess.MakeDirRecursiveAbsolute(SaveDir);

		CaptureThumbnail(SlotThumbnailPath(slot));

		var config = new ConfigFile();
		config.SetValue("save", "version", SaveFormatVersion);
		config.SetValue("save", "name", displayName);
		config.SetValue("save", "timestamp", Time.GetDatetimeStringFromSystem(false).Replace("T", "  "));
		config.SetValue("save", "unix_time", Time.GetUnixTimeFromSystem());
		config.SetValue("save", "scene_path", scenePath);
		config.SetValue("save", "pos_x", position.X);
		config.SetValue("save", "pos_y", position.Y);
		config.Save(SlotConfigPath(slot));
	}

	private SlotData ReadSlot(int slot)
	{
		var data = new SlotData { Occupied = false };
		var config = new ConfigFile();
		if (config.Load(SlotConfigPath(slot)) != Error.Ok) return data;

		int version = (int)config.GetValue("save", "version", 0);
		string scenePath = (string)config.GetValue("save", "scene_path", "");
		// A save from an older schema version, or one pointing at a scene
		// that's since been renamed/removed, can't be trusted to load
		// correctly — silently wipe it instead of handing back a checkpoint
		// that'll fail partway through joining/loading.
		if (version != SaveFormatVersion || string.IsNullOrEmpty(scenePath) || !ResourceLoader.Exists(scenePath))
		{
			DeleteSlot(slot);
			return data;
		}

		data.Occupied = true;
		data.DisplayName = (string)config.GetValue("save", "name", $"AUTOSAVE {slot}");
		data.Timestamp = (string)config.GetValue("save", "timestamp", "");
		data.ScenePath = scenePath;
		data.Position = new Vector2(
			(float)(double)config.GetValue("save", "pos_x", 0.0),
			(float)(double)config.GetValue("save", "pos_y", 0.0)
		);
		return data;
	}

	// Downscaled well below full resolution — this is a load-menu thumbnail,
	// not a screenshot gallery, and 5 of them sitting in user:// as tiny PNGs
	// costs nothing to keep around indefinitely.
	private void CaptureThumbnail(string path)
	{
		Image full = GetViewport().GetTexture().GetImage();
		full.Resize(ThumbnailWidth, ThumbnailHeight, Image.Interpolation.Lanczos);
		full.SavePng(path);
	}

	private static string SlotConfigPath(int slot) => $"{SaveDir}slot_{slot}.cfg";
	private static string SlotThumbnailPath(int slot) => $"{SaveDir}slot_{slot}.png";
}
