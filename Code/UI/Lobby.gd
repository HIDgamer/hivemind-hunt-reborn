extends MenuBase

@onready var name_field: LineEdit = $VBoxContainer/NameRow/NameField
@onready var ip_field: LineEdit = $VBoxContainer/JoinRow/IPField
@onready var host_button: Button = $VBoxContainer/HostButton
@onready var join_button: Button = $VBoxContainer/JoinRow/JoinButton
@onready var back_button: Button = $VBoxContainer/BackButton
@onready var status_label: Label = $VBoxContainer/StatusLabel

const TUTORIAL_LEVEL := "uid://dpo7v6ksd0n07"
const MAIN_MENU := "uid://cnw6i1v1qt1rh"

func _ready() -> void:
	super._ready()
	_connect_menu_button(host_button, Callable(self, "_on_host_pressed"))
	_connect_menu_button(join_button, Callable(self, "_on_join_pressed"))
	_connect_menu_button(back_button, Callable(self, "_on_back_pressed"))

	var net = get_node("/root/NetworkManager")
	net.ConnectionSucceeded.connect(_on_connection_succeeded)
	net.ConnectionFailed.connect(_on_connection_failed)

func _on_host_pressed() -> void:
	var net = get_node("/root/NetworkManager")
	net.LocalPlayerName = _resolve_name()
	var err = net.StartHost()
	if err == OK:
		status_label.text = "HOSTING // LOADING SHIP..."
		_fade_out_and_change_scene(TUTORIAL_LEVEL)
	else:
		status_label.text = "FAILED TO HOST (%s)" % err

func _on_join_pressed() -> void:
	var net = get_node("/root/NetworkManager")
	net.LocalPlayerName = _resolve_name()
	var address := ip_field.text.strip_edges()
	if address.is_empty():
		address = "127.0.0.1"
	status_label.text = "CONNECTING TO %s..." % address
	var err = net.StartClient(address)
	if err != OK:
		status_label.text = "FAILED TO CONNECT (%s)" % err

func _on_connection_succeeded() -> void:
	status_label.text = "CONNECTED // LOADING SHIP..."
	_fade_out_and_change_scene(TUTORIAL_LEVEL)

func _on_connection_failed() -> void:
	status_label.text = "CONNECTION FAILED"

func _resolve_name() -> String:
	var typed := name_field.text.strip_edges()
	return typed if not typed.is_empty() else "PLAYER"

func _on_back_pressed() -> void:
	_fade_out_and_change_scene(MAIN_MENU)
