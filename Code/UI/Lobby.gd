extends MenuBase

@onready var name_field: LineEdit = $VBoxContainer/NameRow/NameField
@onready var port_field: LineEdit = $VBoxContainer/PortRow/PortField
@onready var ip_field: LineEdit = $VBoxContainer/JoinRow/IPField
@onready var host_button: Button = $VBoxContainer/HostButton
@onready var join_button: Button = $VBoxContainer/JoinRow/JoinButton
@onready var back_button: Button = $VBoxContainer/BackButton
@onready var status_label: Label = $VBoxContainer/StatusLabel

const TUTORIAL_LEVEL := "uid://dpo7v6ksd0n07"
const MAIN_MENU := "uid://cnw6i1v1qt1rh"

# Blank callsign -> a random Colonial Marine handle instead of everyone
# silently being "PLAYER".
const RANDOM_CALLSIGNS := [
	"RIPLEY", "HICKS", "VASQUEZ", "HUDSON", "APONE",
	"BISHOP", "DRAKE", "FERRO", "GORMAN", "DIETRICH",
]

func _ready() -> void:
	super._ready()
	_connect_menu_button(host_button, Callable(self, "_on_host_pressed"))
	_connect_menu_button(join_button, Callable(self, "_on_join_pressed"))
	_connect_menu_button(back_button, Callable(self, "_on_back_pressed"))

	# Surface why we were bounced back here (failed connect / server gone).
	var net = get_node("/root/NetworkManager")
	if net.LastError != "":
		status_label.text = net.LastError
		net.LastError = ""

func _on_host_pressed() -> void:
	var net = get_node("/root/NetworkManager")
	net.LocalPlayerName = _resolve_name()
	var port := _resolve_port()
	var err = net.StartHost(port)
	if err == OK:
		status_label.text = "HOSTING ON PORT %d // LOADING SHIP..." % port
		_fade_out_and_change_scene(TUTORIAL_LEVEL)
	else:
		status_label.text = "FAILED TO HOST ON PORT %d (%s)" % [port, err]

func _on_join_pressed() -> void:
	var net = get_node("/root/NetworkManager")
	net.LocalPlayerName = _resolve_name()
	var address := ip_field.text.strip_edges()
	if address.is_empty():
		address = "127.0.0.1"
	var port := _resolve_port()
	# Deliberately NOT connecting here: the level scene must be loaded first
	# so its MultiplayerSpawner exists when the server's spawn packets arrive
	# (they are sent once, immediately on connect, and never re-sent).
	# PlayerSpawner completes the join once the level is in the tree.
	net.DeferJoin(address, port)
	status_label.text = "LINKING TO %s:%d..." % [address, port]
	_fade_out_and_change_scene(TUTORIAL_LEVEL)

func _resolve_port() -> int:
	var typed := port_field.text.strip_edges()
	if typed.is_empty() or not typed.is_valid_int():
		return net_default_port()
	var parsed := typed.to_int()
	return parsed if parsed > 0 and parsed <= 65535 else net_default_port()

func net_default_port() -> int:
	return get_node("/root/NetworkManager").GetDefaultPort()

func _resolve_name() -> String:
	var typed := name_field.text.strip_edges()
	if not typed.is_empty():
		return typed
	return "%s-%02d" % [RANDOM_CALLSIGNS.pick_random(), randi_range(10, 99)]

func _on_back_pressed() -> void:
	_fade_out_and_change_scene(MAIN_MENU)
