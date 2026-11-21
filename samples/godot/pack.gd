extends SceneTree

func _initialize():
	var args = OS.get_cmdline_user_args()
	if args.size() != 1:
		push_error("Supply an output .pck path after --")
		quit(1)
		return
	var packer = PCKPacker.new()
	var err = packer.pck_start(args[0])
	if err != OK:
		quit(1)
		return
	for name in ["project.godot", "main.tscn", "main.gd", "dialogue.json"]:
		err = packer.add_file("res://" + name, "res://" + name)
		if err != OK:
			quit(1)
			return
	err = packer.flush()
	quit(0 if err == OK else 1)
