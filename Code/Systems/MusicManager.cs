using Godot;

// Autoload (see project.godot). Each level/menu authors its own fixed
// ambient AudioStreamPlayer (autoplay=true, in the "level_music" group) —
// MusicManager doesn't choose or own that track, it only ducks it during
// combat and crossfades in a combat/boss track on top, via
// EnterCombat(track)/ExitCombat(). See EnemyBase.gd's is_boss/boss_music/
// _set_boss_combat_active for what actually calls EnterCombat/ExitCombat —
// no manually-placed trigger volume required just for the music to switch.
public partial class MusicManager : Node
{
	[Export] public float CrossfadeDuration = 1.2f;
	[Export] public float AmbientDuckDb = -18f;
	private const float CombatSilentDb = -80f;

	private AudioStreamPlayer _combatPlayer;
	private AudioStreamPlayer _ambientPlayer;
	private float _ambientBaseVolumeDb;
	private Tween _tween;
	private bool _inCombat;

	public override void _Ready()
	{
		_combatPlayer = new AudioStreamPlayer { Bus = "Music", VolumeDb = CombatSilentDb };
		AddChild(_combatPlayer);
	}

	// Safe to call every frame combat is active (e.g. from an enemy's own
	// aggro tick) — it only actually (re)starts the crossfade the first
	// time, or if the track itself changes (a different boss taking over).
	public void EnterCombat(AudioStream track)
	{
		if (track == null) return;
		if (_inCombat && _combatPlayer.Stream == track && _combatPlayer.Playing) return;

		bool freshEntry = !_inCombat;
		_inCombat = true;
		FindAmbientPlayer();

		if (_combatPlayer.Stream != track)
		{
			// None of this project's music assets are imported with loop
			// enabled (every .ogg.import here has loop=false — they're meant
			// to play once through), which is fine for a level's own ambient
			// loop track (it's typically authored with parameters/looping
			// on the AudioStreamPlayer itself) but a boss track needs to
			// hold for however long the fight lasts, so it's force-looped
			// here instead of relying on Finished to restart it (which would
			// have an audible gap/click at every replay).
			if (track is AudioStreamOggVorbis ogg) ogg.Loop = true;
			else if (track is AudioStreamMP3 mp3) mp3.Loop = true;
			else if (track is AudioStreamWav wav) wav.LoopMode = AudioStreamWav.LoopModeEnum.Forward;

			_combatPlayer.Stream = track;
		}
		if (!_combatPlayer.Playing)
		{
			_combatPlayer.Play();
		}

		if (!freshEntry) return; // already mid-crossfade or fully faded in, nothing left to animate

		_tween?.Kill();
		_tween = CreateTween().SetParallel();
		_tween.TweenProperty(_combatPlayer, "volume_db", 0f, CrossfadeDuration);
		if (_ambientPlayer != null)
		{
			_tween.TweenProperty(_ambientPlayer, "volume_db", _ambientBaseVolumeDb + AmbientDuckDb, CrossfadeDuration);
		}
	}

	public void ExitCombat()
	{
		if (!_inCombat) return;
		_inCombat = false;

		_tween?.Kill();
		_tween = CreateTween().SetParallel();
		_tween.TweenProperty(_combatPlayer, "volume_db", CombatSilentDb, CrossfadeDuration);
		if (_ambientPlayer != null)
		{
			_tween.TweenProperty(_ambientPlayer, "volume_db", _ambientBaseVolumeDb, CrossfadeDuration);
		}
		_tween.Chain().TweenCallback(Callable.From(() => _combatPlayer.Stop()));
	}

	// Re-resolved lazily rather than cached across level loads — the
	// previous level's ambient player is freed on scene change, and
	// IsInstanceValid catches that so the next EnterCombat just finds
	// whichever one exists in the newly-loaded level instead of silently
	// ducking a corpse reference forever.
	private void FindAmbientPlayer()
	{
		if (_ambientPlayer != null && IsInstanceValid(_ambientPlayer)) return;

		_ambientPlayer = null;
		foreach (Node node in GetTree().GetNodesInGroup("level_music"))
		{
			if (node is AudioStreamPlayer player)
			{
				_ambientPlayer = player;
				_ambientBaseVolumeDb = player.VolumeDb;
				return;
			}
		}
	}
}
