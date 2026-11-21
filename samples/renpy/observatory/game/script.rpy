define config.name = "Locara Observatory"
define config.save_directory = "locara-observatory-sample"
define config.has_autosave = False
define config.has_quicksave = False
define config.default_language = None
define guide = Character("المرشد", who_color="#73dfc4")
default visitor = "نور"

init python:
    gui.init(960, 540)

image observatory = Solid("#0e1a27")

screen say(who, what):
    window:
        id "window"
        xalign 0.5
        yalign 0.84
        xsize 810
        ysize 155
        background Solid("#192c3c")
        padding (25, 18)
        vbox:
            xfill True
            spacing 12
            if who:
                text who id "who" xalign 1.0 size 26
            text what id "what" xalign 1.0 size 31

screen choice(items):
    vbox:
        align (0.5, 0.5)
        spacing 14
        for item in items:
            textbutton item.caption action item.action xalign 0.5 text_size 29

label start:
    scene observatory
    guide "Welcome, [visitor]. The {b}observatory{/b} is awake." with dissolve
    menu:
        "Open the dome":
            guide "You opened the dome."
        "Wait by the telescope":
            guide "You stayed by the telescope."
    return
