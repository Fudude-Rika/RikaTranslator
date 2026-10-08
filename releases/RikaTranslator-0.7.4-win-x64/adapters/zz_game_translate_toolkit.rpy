# Game Translate Toolkit 0.1 — original Ren'Py 7/8 adapter (MIT).
# Per-game localhost token only; never an upstream API key.
# Official hooks: config.replace_text / Text.update / invoke_in_main_thread.
init 999 python hide:
    import json
    import threading
    import weakref
    import time
    import os
    import io
    import sys
    try:
        import builtins as _gtt_builtins
    except ImportError:
        import __builtin__ as _gtt_builtins
    try:
        import queue
        from urllib.request import Request, urlopen
    except ImportError:
        import Queue as queue
        from urllib2 import Request, urlopen

    class _GttState(_gtt_builtins.object):
        def __init__(self, settings):
            self.settings = settings
            # Ren'Py rewrites literal containers and aliases builtins in script
            # code for rollback. Background workers must use Python's actual
            # native containers, outside the engine's mutation/rollback log.
            self.cache = _gtt_builtins.dict()
            self.order = _gtt_builtins.list()
            self.pending = _gtt_builtins.set()
            self.failed = _gtt_builtins.dict()
            self.queue = queue.Queue(256)
            self.live = weakref.WeakSet()
            self.lock = threading.RLock()
            self.last_warning = 0

        def request(self, text):
            if not text or len(text) > 8192 or not any(c.isalpha() for c in text):
                return text
            # No speculative model calls during Ren'Py prediction or engine init.
            if renpy.game.context().init_phase or renpy.predicting():
                return text
            with self.lock:
                if text in self.cache:
                    return self.cache[text]
                if text in self.pending or self.failed.get(text, 0) > time.time():
                    return text
                try:
                    self.queue.put_nowait(text)
                    self.pending.add(text)
                except queue.Full:
                    pass
            return text

        def refresh(self):
            # Called in the main thread. Tag splitting stays in Ren'Py itself;
            # only actual visible text is translated by config.replace_text.
            for displayable in _gtt_builtins.list(self.live):
                try:
                    displayable.dirty = True
                    displayable.kill_layout()
                    renpy.redraw(displayable, 0)
                except Exception:
                    pass
            renpy.restart_interaction()

        def warning(self):
            if time.time() - self.last_warning > 60:
                self.last_warning = time.time()
                renpy.log("[Game Translate Toolkit] Local bridge unavailable or translation incomplete; original text retained. See toolkit logs.")

        def worker(self):
            while True:
                source = self.queue.get()
                translated = None
                for attempt in range(2):
                    try:
                        request_data = _gtt_builtins.dict((("token", self.settings["token"]), ("gameId", self.settings["gameId"]),
                            ("text", source), ("from", self.settings.get("from", "auto")),
                            ("to", self.settings.get("to", "zh-CN"))))
                        body = json.dumps(request_data, ensure_ascii=False).encode("utf-8")
                        req = Request(self.settings["endpoint"], body, _gtt_builtins.dict((("Content-Type", "application/json"),)))
                        response = urlopen(req, timeout=65)
                        try:
                            data = json.loads(response.read(262144).decode("utf-8"))
                        finally:
                            response.close()
                        translated = data.get("text")
                        if translated and translated != source:
                            break
                    except Exception:
                        pass
                    translated = None
                    if attempt == 0:
                        time.sleep(1)
                with self.lock:
                    self.pending.discard(source)
                    if translated:
                        self.cache[source] = translated
                        self.order.append(source)
                        while len(self.order) > 5000:
                            self.cache.pop(self.order.pop(0), None)
                    else:
                        self.failed[source] = time.time() + 60
                try:
                    if hasattr(renpy, "invoke_in_main_thread"):
                        renpy.invoke_in_main_thread(self.refresh if translated else self.warning)
                    elif translated:
                        # Older Ren'Py 7 exposes the thread-safe restart hook.
                        # Text.per_interact below performs actual redraw work.
                        renpy.restart_interaction()
                    elif time.time() - self.last_warning > 60:
                        self.last_warning = time.time()
                        sys.stderr.write("[Game Translate Toolkit] Local translation bridge unavailable; original text retained.\n")
                except Exception:
                    pass
                self.queue.task_done()

    _gtt_config_path = os.path.join(config.gamedir, "GameTranslateToolkit", "bridge.json")
    try:
        with io.open(_gtt_config_path, "r", encoding="utf-8") as _gtt_file:
            _gtt_settings = json.load(_gtt_file)
        _gtt_state = _GttState(_gtt_settings)
        # A module attribute is outside save/rollback store state. The adapter
        # never writes game saves, persistent data, or original script resources.
        renpy._game_translate_toolkit = _gtt_state
        _gtt_old_filter = config.replace_text
        def _gtt_filter(text, _state=_gtt_state, _old=_gtt_old_filter):
            if _old is not None:
                text = _old(text)
            return _state.request(text)
        config.replace_text = _gtt_filter
        _gtt_text_class = renpy.text.text.Text
        _gtt_old_update = _gtt_text_class.update
        def _gtt_update(self, *args, **kwargs):
            try:
                _gtt_state.live.add(self)
            except TypeError:
                pass
            return _gtt_old_update(self, *args, **kwargs)
        _gtt_text_class.update = _gtt_update
        _gtt_old_per_interact = _gtt_text_class.per_interact
        def _gtt_per_interact(self, *args, **kwargs):
            # A predicted Text can reuse its original layout when first shown.
            # Invalidate it at the actual interaction so prediction never
            # suppresses extraction of the currently visible text.
            if not renpy.game.context().init_phase and not renpy.predicting():
                self.dirty = True
                self.kill_layout()
            return _gtt_old_per_interact(self, *args, **kwargs)
        _gtt_text_class.per_interact = _gtt_per_interact
        _gtt_font = _gtt_settings.get("fontFile", "GameTranslateToolkit/NotoSansCJKsc-Regular.otf")
        if _gtt_settings.get("fontMode", "auto") != "original" and renpy.loadable(_gtt_font):
            for _gtt_style in ("default", "say_dialogue", "say_label", "choice_button_text", "button_text", "label_text", "input"):
                try:
                    getattr(style, _gtt_style).font = _gtt_font
                except Exception:
                    pass
        for _gtt_n in range(3):
            _gtt_thread = threading.Thread(target=_gtt_state.worker)
            _gtt_thread.daemon = True
            _gtt_thread.start()
        renpy.log("[Game Translate Toolkit] Ren'Py adapter loaded; originals appear immediately, translations refresh asynchronously.")
    except Exception:
        renpy.log("[Game Translate Toolkit] Adapter initialization failed; original game behavior retained. See toolkit logs.")
