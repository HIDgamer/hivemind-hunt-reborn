extends CanvasLayer

# Autoload (see project.godot). Saffi — a low-ranking officer nominally
# assigned to talk Sam through the facility. In practice she narrates over
# death/hurt/puzzle-solving as it happens WITHOUT stopping the game: unlike
# DialogueBox.gd (modal, sets UiInputCaptured, one clip at a time by design),
# this never touches input and just posts a corner caption that fades on its
# own. A rate-limit keeps her from commenting on literally every single hit.
#
# Character voice: she's technically here to help — comms officer, supposed
# to be Sam's guide — but she's mostly using the job as an outlet. Dry,
# needling, a little unstable, clearly enjoys watching things go wrong more
# than she should. Not pure cruelty though: she's THIS close to being
# likeable, and the rare genuinely-supportive line should land as a real
# surprise rather than a contradiction — she's a person having a bad
# tour of duty, not a hostile AI wearing a friendly voice.
#
# Voice: every category has recorded VO now, including "idle" (VOICE_LINES
# below, index-matched to BARK_LINES[category]) — same "play if present"
# shape FlickeringLight.SparkSound already uses, just keyed by
# category+index instead of a single exported field. Idle's last three
# entries (Hum1/Hum2/Bonus) are wordless/off-mic asides, hence the
# bracketed "(...)" caption style instead of a normal spoken line.
#
# Every clip is run through the same intercom/PA-speaker filter
# (Sound/voice/Saffi/*.ogg — see git history for the offline DSP pass:
# ~450Hz-3.2kHz bandpass, a boxy ~1.8kHz presence peak, soft-clip drive, a
# light AGC-style compressor, an 8kHz-and-back resample for codec-style
# band-limiting, plus a faint band-limited static bed) so she reads as
# talking through a facility mic/speaker rather than being physically
# present in the room. Source wavs are deleted once converted — the .ogg is
# the only copy that ships.

const BARK_COOLDOWN: float = 4.0
const REVEAL_CHARS_PER_SECOND: float = 55.0
const HOLD_AFTER_REVEAL: float = 2.2
const FADE_DURATION: float = 0.5

const BARK_LINES := {
	"death": [
		"And she's down. Somebody write that on a whiteboard somewhere.",
		"Note for the log: subject is dead again. I've stopped being surprised.",
		"You know, most people only die once. You're really committing to the bit.",
		"...Okay, that one actually got to me a little. Get back up, alright?",
	],
	"hurt": [
		"That looked like it hurt. Good. Means you're paying attention now.",
		"Vitals dropping. I'd say pace yourself but we both know you won't.",
		"Ooh. That's gonna bruise. Wear it with pride, soldier.",
		"Hey — easy. Watch yourself out there, actually. I mean that one.",
	],
	"checkpoint": [
		"Checkpoint logged. Try not to need it, but, you know. We both know.",
		"Progress saved. Small miracles, folks.",
		"There's your marker. Don't get comfortable, it's not that kind of facility.",
		"Good work back there. I'll admit it once — don't make me say it twice.",
	],
	"plate_pressed": [
		"Ah, a button. Look at you, working the big machinery.",
		"Mechanism's live. Was that on purpose or should I be worried?",
		"Something clicked into place. Probably fine. Probably.",
	],
	"door_opened": [
		"Door's open. Try to act like you've seen one before.",
		"There you go. Try not to let it hit you on the way through.",
		"Access granted, for whatever that's worth around here.",
		"Nice. That's actually good progress — don't let it go to your head.",
	],
	# Ambient chatter with nothing else going on — fired on a long randomized
	# timer (see _idle_timer) rather than any specific event, so this pool
	# needs to be big or the "quiet moments" start feeling scripted.
	"idle": [
		"You've been standing there a while. Thinking, or just recharging?",
		"Tick tock. Not that I'm timing you. I am absolutely timing you.",
		"Fun fact: this facility has fourteen emergency exits. None of them lead anywhere good.",
		"I'd offer route suggestions, but where's the fun in that.",
		"Still there? Good. Building's less depressing with someone breathing in it.",
		"Somewhere in this building is my old office. Try not to find it, it's not flattering.",
		"You know what they never trained me for? This. Specifically this.",
		"Take your time. It's not like anything in here is on a schedule. Except everything.",
		"I could hum something to fill the silence, but you'd regret asking.",
		"Sensors show nothing dangerous nearby. For once. Don't get used to it.",
		"Between us — I don't actually know what half these rooms were originally for.",
		"You walk like someone who's never read a safety briefing. Refreshing, honestly.",
		"If you're looking for a save point, you're going the long way.",
		"I once memorized this whole facility's layout. I regret that daily.",
		"This is the part of the job where I say something encouraging. Working on it.",
		"Don't mind me. I'm just here to make sure someone logs how this goes wrong.",
		"You're doing fine, for the record. Don't let it go to your head.",
		"Comms check: still alive, still moving, still my problem. Noted.",
		"If these walls could talk, they'd mostly just scream. Moving on.",
		"I'd say 'take your time' but corporate's listening, so: hurry up. Please.",
		"Somewhere above us, someone is very calmly pretending this is all fine.",
		"You ever get the feeling this place was built by someone who hated stairs?",
		"Break's over whenever you say it's over. I'm not your supervisor. Today.",
		"...You're actually pretty good at this, you know. Don't tell anyone I said so.",
		"(hums a quiet few bars to herself — comms left open by accident)",
		"(keeps humming, softer now — you're not really supposed to be hearing this)",
		"Uh-uh. Just because I was nice, don't expect me to call you my little pogchamp or anything.",
	],
}

const IDLE_BARK_MIN_INTERVAL: float = 150.0
const IDLE_BARK_MAX_INTERVAL: float = 280.0

# Ducks whatever's in the "level_music" group (Level_00_Tutorial.tscn's
# AudioStreamPlayer is tagged this way — add the same group to any future
# level's music node to get this for free) while a bark is showing, so Saffi
# doesn't have to fight the soundtrack to be heard.
const MUSIC_DUCK_VOLUME_DB: float = -16.0
const MUSIC_DUCK_FADE_TIME: float = 0.3

# Paths only (not preload()) so a fresh file that hasn't been through the
# editor's import pass yet can't fail script parsing — loaded on demand in
# bark() instead. Index-matched to BARK_LINES[category], so line 0 plays
# with sound 0, etc.
const VOICE_LINES := {
	"death": [
		"res://Sound/voice/Saffi/Death1.ogg",
		"res://Sound/voice/Saffi/Death2.ogg",
		"res://Sound/voice/Saffi/Death3.ogg",
		"res://Sound/voice/Saffi/Death4.ogg",
	],
	"hurt": [
		"res://Sound/voice/Saffi/Hurt1.ogg",
		"res://Sound/voice/Saffi/Hurt2.ogg",
		"res://Sound/voice/Saffi/Hurt3.ogg",
		"res://Sound/voice/Saffi/Hurt4.ogg",
	],
	"checkpoint": [
		"res://Sound/voice/Saffi/Checkpoint1.ogg",
		"res://Sound/voice/Saffi/Checkpoint2.ogg",
		"res://Sound/voice/Saffi/Checkpoint3.ogg",
		"res://Sound/voice/Saffi/Checkpoint4.ogg",
	],
	"plate_pressed": [
		"res://Sound/voice/Saffi/PlatePress1.ogg",
		"res://Sound/voice/Saffi/PlatePress2.ogg",
		"res://Sound/voice/Saffi/PlatePress3.ogg",
	],
	"door_opened": [
		"res://Sound/voice/Saffi/DoorOpen1.ogg",
		"res://Sound/voice/Saffi/DoorOpen2.ogg",
		"res://Sound/voice/Saffi/DoorOpen3.ogg",
		"res://Sound/voice/Saffi/DoorOpen4.ogg",
	],
	"idle": [
		"res://Sound/voice/Saffi/Idle1.ogg",
		"res://Sound/voice/Saffi/Idle2.ogg",
		"res://Sound/voice/Saffi/Idle3.ogg",
		"res://Sound/voice/Saffi/Idle4.ogg",
		"res://Sound/voice/Saffi/Idle5.ogg",
		"res://Sound/voice/Saffi/Idle6.ogg",
		"res://Sound/voice/Saffi/Idle7.ogg",
		"res://Sound/voice/Saffi/Idle8.ogg",
		"res://Sound/voice/Saffi/Idle9.ogg",
		"res://Sound/voice/Saffi/Idle10.ogg",
		"res://Sound/voice/Saffi/Idle11.ogg",
		"res://Sound/voice/Saffi/Idle12.ogg",
		"res://Sound/voice/Saffi/Idle13.ogg",
		"res://Sound/voice/Saffi/Idle14.ogg",
		"res://Sound/voice/Saffi/Idle15.ogg",
		"res://Sound/voice/Saffi/Idle16.ogg",
		"res://Sound/voice/Saffi/Idle17.ogg",
		"res://Sound/voice/Saffi/Idle18.ogg",
		"res://Sound/voice/Saffi/Idle19.ogg",
		"res://Sound/voice/Saffi/Idle20.ogg",
		"res://Sound/voice/Saffi/Idle21.ogg",
		"res://Sound/voice/Saffi/Idle22.ogg",
		"res://Sound/voice/Saffi/Idle23.ogg",
		"res://Sound/voice/Saffi/Idle24.ogg",
		"res://Sound/voice/Saffi/Hum1.ogg",
		"res://Sound/voice/Saffi/Hum2.ogg",
		"res://Sound/voice/Saffi/Bonus.ogg",
	],
}

# Avoid immediate repeats per category — small per-key "last index" memory.
var _last_line_index: Dictionary = {}
var _cooldown_timer: float = 0.0
var _idle_timer: float = 0.0
var _hold_timer: float = 0.0
var _reveal_progress: float = 0.0
var _current_text: String = ""

# Original volume_db per music player, captured the moment it's first ducked
# so it can be restored exactly regardless of that level's own mix.
var _ducked_music_volumes: Dictionary = {}
var _music_tweens: Dictionary = {}

@onready var _panel: Panel = $Panel
@onready var _text_label: RichTextLabel = $Panel/TextLabel
@onready var _portrait: TextureRect = $Panel/PortraitFrame/Portrait
@onready var _voice: AudioStreamPlayer = $VoiceAudio
@onready var _saffi_texture: Texture2D = preload("res://Assets/Potraits/Saffi.png")

var _fade_tween: Tween


func _ready() -> void:
	_panel.visible = false
	_portrait.texture = _saffi_texture
	get_tree().node_added.connect(_on_node_added)
	call_deferred("_hook_existing_scene")
	_reset_idle_timer()


func _process(delta: float) -> void:
	if _cooldown_timer > 0.0:
		_cooldown_timer -= delta

	# Idle chatter is ambience, not a reaction to anything real — only the
	# bark authority (the server, or the only peer at all in single-player)
	# rolls the dice for it. If every client also ran its own timer, each
	# would pick barks independently and the squad would hear different
	# lines at different times instead of one shared Saffi.
	if _is_bark_authority():
		_idle_timer -= delta
		if _idle_timer <= 0.0:
			_reset_idle_timer()
			_try_idle_bark()

	if not _panel.visible:
		return

	if _reveal_progress < _current_text.length():
		_reveal_progress += delta * REVEAL_CHARS_PER_SECOND
		_text_label.visible_characters = int(min(_reveal_progress, _current_text.length()))
	elif _hold_timer > 0.0:
		_hold_timer -= delta
		if _hold_timer <= 0.0:
			_dismiss()


# --- multiplayer sync --------------------------------------------------------
# Saffi is one shared companion, not a private HUD voice — every peer should
# see/hear the exact same line at the exact same moment. Left alone, each
# peer's own randi() would pick a different line for the same event, since
# nothing about the pick itself is networked. Fix: only the bark authority
# (server, or the sole peer in single-player) ever rolls the dice; clients
# ask the server to bark on their behalf, and the actual line index is
# broadcast to everyone so the display/voice step is 100% deterministic —
# same shape as AbilityPickupComponent's "server decides, everyone plays it
# out identically" pattern.

func _get_network_manager() -> Node:
	return get_node_or_null("/root/NetworkManager")


func _is_networked() -> bool:
	var net := _get_network_manager()
	return net != null and net.IsNetworked


func _is_bark_authority() -> bool:
	var net := _get_network_manager()
	return net == null or not net.IsNetworked or net.IsServerSession


func bark(category: String) -> void:
	if not _is_networked():
		_decide_bark(category)
		return
	if _is_bark_authority():
		_decide_bark(category)
	else:
		_request_bark.rpc_id(1, category)


# Picks which line plays (only ever runs on the bark authority) and applies
# it — locally only in single-player, broadcast to the whole squad otherwise.
func _decide_bark(category: String) -> void:
	if _cooldown_timer > 0.0:
		return
	var lines: Array = BARK_LINES.get(category, [])
	if lines.is_empty():
		return

	var idx := randi() % lines.size()
	if lines.size() > 1 and idx == _last_line_index.get(category, -1):
		idx = (idx + 1) % lines.size()
	_last_line_index[category] = idx

	if _is_networked():
		_play_bark.rpc(category, idx)
	else:
		_apply_bark(category, idx)


@rpc("any_peer", "call_remote", "reliable")
func _request_bark(category: String) -> void:
	# Only ever meaningful on the server: an authority-mode peer id default
	# means clients can't call this on each other, only the server receives
	# it, which is exactly who needs to make the call.
	_decide_bark(category)


@rpc("authority", "call_local", "reliable")
func _play_bark(category: String, idx: int) -> void:
	_apply_bark(category, idx)


func _apply_bark(category: String, idx: int) -> void:
	var lines: Array = BARK_LINES.get(category, [])
	if idx < 0 or idx >= lines.size():
		return
	# Cooldown ticks regardless of local muting, so this peer's own timing
	# stays consistent with the bark-authority even while suppressing idle
	# display — only "idle" is ever muted here; death/hurt/checkpoint/plate/
	# door are real reactions, not ambience, and always show.
	_cooldown_timer = BARK_COOLDOWN
	if category == "idle":
		var settings := get_node_or_null("/root/GameSettings")
		if settings and bool(settings.IdleBarksMuted):
			return
	_show_bark(lines[idx])
	_play_voice_line(category, idx)


func _play_voice_line(category: String, idx: int) -> void:
	var paths: Array = VOICE_LINES.get(category, [])
	if idx >= paths.size():
		return
	var path: String = paths[idx]
	if path.is_empty() or not ResourceLoader.exists(path):
		return
	_voice.stream = load(path)
	_voice.play()


func _reset_idle_timer() -> void:
	_idle_timer = randf_range(IDLE_BARK_MIN_INTERVAL, IDLE_BARK_MAX_INTERVAL)


func _try_idle_bark() -> void:
	# Stay quiet during a modal NPC conversation (DialogueUI owns input then,
	# and Saffi cutting in over someone else's dialogue would read as a bug
	# rather than a character trait).
	var dialogue_ui := get_node_or_null("/root/DialogueUI")
	if dialogue_ui and bool(dialogue_ui.call("is_active")):
		return
	bark("idle")


func _show_bark(text: String) -> void:
	if _fade_tween and _fade_tween.is_running():
		_fade_tween.kill()

	_current_text = text
	_text_label.text = text
	_text_label.visible_characters = 0
	_reveal_progress = 0.0
	_hold_timer = HOLD_AFTER_REVEAL
	_panel.modulate.a = 1.0
	_panel.visible = true
	_duck_level_music()


func _duck_level_music() -> void:
	for player in get_tree().get_nodes_in_group("level_music"):
		if not is_instance_valid(player):
			continue
		if not _ducked_music_volumes.has(player):
			_ducked_music_volumes[player] = player.volume_db
		_tween_music_volume(player, MUSIC_DUCK_VOLUME_DB)


func _unduck_level_music() -> void:
	for player in _ducked_music_volumes:
		if is_instance_valid(player):
			_tween_music_volume(player, _ducked_music_volumes[player])
	_ducked_music_volumes.clear()


func _tween_music_volume(player: Node, target_db: float) -> void:
	if _music_tweens.has(player) and _music_tweens[player] and _music_tweens[player].is_valid():
		_music_tweens[player].kill()
	var tween := create_tween()
	_music_tweens[player] = tween
	tween.tween_property(player, "volume_db", target_db, MUSIC_DUCK_FADE_TIME)


func _dismiss() -> void:
	_fade_tween = create_tween()
	_fade_tween.tween_property(_panel, "modulate:a", 0.0, FADE_DURATION)
	# Keep the music down through the whole fade-out, not just the hold —
	# unducking the instant the fade starts made the music swell back in
	# while the caption was still visibly finishing.
	_fade_tween.tween_callback(func():
		_panel.visible = false
		_unduck_level_music()
	)


# --- event hooking -----------------------------------------------------------
# Same "walk the tree, connect anything with the right signal" approach
# Runner.gd already established for laser hooking (_hook_laser_signals /
# _on_node_added) — reused here so late-spawned props (a door instanced at
# runtime, say) still get picked up instead of only whatever exists at boot.

func _hook_existing_scene() -> void:
	var scene := get_tree().current_scene
	if scene == null:
		push_warning(
			"Narrator: current_scene was null on the deferred hook pass — "
			+ "hurt/death barks depend entirely on _on_node_added catching the player late."
		)
		return
	_hook_recursive(scene)

	for player in get_tree().get_nodes_in_group("Player"):
		_hook_player_health(player)


func _hook_recursive(node: Node) -> void:
	_try_hook(node)
	for child in node.get_children():
		_hook_recursive(child)


func _on_node_added(node: Node) -> void:
	_try_hook(node)
	if node.is_in_group("Player"):
		_hook_player_health(node)


func _try_hook(node: Node) -> void:
	# C# [Signal] delegates surface to GDScript UNCHANGED (PascalCase, just
	# the EventHandler suffix dropped) — NOT converted to snake_case. Confirmed
	# by dumping HealthComponent's actual get_signal_list() at runtime: custom
	# signals showed up as "TookDamage"/"Died", while the Node base class's
	# own built-in signals (body_entered, animation_finished, etc.) stayed
	# snake_case since those are engine-native, not part of the C# binding.
	if node.has_signal("PlatePressed") and not node.PlatePressed.is_connected(_on_plate_pressed):
		node.PlatePressed.connect(_on_plate_pressed)
	if node.has_signal("Activated") and not node.Activated.is_connected(_on_checkpoint_activated):
		node.Activated.connect(_on_checkpoint_activated)
	if node.has_signal("Opened") and not node.Opened.is_connected(_on_door_opened):
		node.Opened.connect(_on_door_opened)


func _hook_player_health(player: Node) -> void:
	# In multiplayer, this peer sees every player's node (local + remote
	# puppets) but should only report ITS OWN player's hits/deaths — every
	# peer already gets its own copy of this same check for its own local
	# player, so the squad-wide bark request still happens exactly once per
	# real event, just from whichever machine actually owns it.
	if not player.is_multiplayer_authority():
		return
	var health := player.get_node_or_null("HealthComponent")
	if health == null:
		push_warning(
			"Narrator: %s has no HealthComponent child — can't hook hurt/death barks." % player.name
		)
		return
	if health.has_signal("Died") and not health.Died.is_connected(_on_player_died):
		health.Died.connect(_on_player_died)
	if health.has_signal("TookDamage") and not health.TookDamage.is_connected(_on_player_hurt):
		health.TookDamage.connect(_on_player_hurt)


func _on_plate_pressed() -> void:
	bark("plate_pressed")


func _on_checkpoint_activated() -> void:
	bark("checkpoint")


func _on_door_opened() -> void:
	bark("door_opened")


func _on_player_died() -> void:
	bark("death")


func _on_player_hurt(_amount: int, _knockback: Vector2) -> void:
	bark("hurt")
