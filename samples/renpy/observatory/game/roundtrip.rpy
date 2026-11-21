testcase arabic_roundtrip:
    run Language("arabic")
    run Start()
    assert screen "say" timeout 10.0
    assert eval "مرحباً يا نور." in renpy.get_displayable("say", "what").get_all_text()
    assert eval "{b}المرصد{/b}" in renpy.get_displayable("say", "what").get_all_text()
    pause 0.6
    screenshot "arabic-dialogue.png"
    advance until screen "choice"
    assert "افتح القبة"
    assert "انتظر عند المقراب"
    pause 0.2
    screenshot "arabic-choice.png"
    click "افتح القبة"
    assert screen "say" timeout 10.0
    assert eval "فتحت القبة." in renpy.get_displayable("say", "what").get_all_text() timeout 10.0
    exit
