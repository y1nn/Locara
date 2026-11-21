extends Control

func _ready():
	var file = FileAccess.open("res://dialogue.json", FileAccess.READ)
	var dialogue = JSON.parse_string(file.get_as_text())
	var heading = Label.new()
	heading.text = "LOCARA OBSERVATORY"
	heading.position = Vector2(60, 65)
	heading.add_theme_font_size_override("font_size", 22)
	heading.modulate = Color(0.45, 0.85, 0.72)
	add_child(heading)
	var welcome = display_text(dialogue["welcome"], Vector2(60, 145), Vector2(840, 95), 38)
	var detail = display_text(dialogue["detail"], Vector2(205, 275), Vector2(695, 220), 30)
	var args = OS.get_cmdline_user_args()
	if "--verify-ar" not in args and "--visual-check" not in args:
		return
	if welcome.text != "مرحباً بك في مرصد لوكارا":
		fail_check("Arabic pack verification failed")
		return
	print("LOCARA_ARABIC_PACK_OK: " + welcome.text)
	var capture = args.find("--visual-check")
	if capture != -1:
		if capture + 1 >= args.size() or DisplayServer.get_name() == "headless":
			fail_check("Visual check requires a rendered window and an output PNG path")
			return
		await get_tree().process_frame
		if not detail.text.contains("2026") or detail.get_line_count() < 2 or detail.get_visible_line_count() != detail.get_line_count():
			fail_check("Mixed-direction or wrapping sample does not fit")
			return
		if detail.position.x + detail.size.x > get_viewport_rect().end.x:
			fail_check("Detail label exceeds the right edge of the viewport")
			return
		await RenderingServer.frame_post_draw
		var frame = get_viewport().get_texture().get_image()
		if frame.save_png(args[capture + 1]) != OK:
			fail_check("Could not save the rendered frame")
			return
		print("LOCARA_ARABIC_RENDER_OK: %d visible detail lines" % detail.get_visible_line_count())
	get_tree().quit(0)

func display_text(text: String, origin: Vector2, area: Vector2, font_size: int) -> Label:
	var label = Label.new()
	label.text = text
	label.position = origin
	label.autowrap_mode = TextServer.AUTOWRAP_WORD_SMART
	label.horizontal_alignment = HORIZONTAL_ALIGNMENT_RIGHT
	label.text_direction = Control.TEXT_DIRECTION_AUTO
	label.add_theme_font_size_override("font_size", font_size)
	add_child(label)
	label.size = area
	return label

func fail_check(message: String) -> void:
	push_error(message)
	get_tree().quit(1)
