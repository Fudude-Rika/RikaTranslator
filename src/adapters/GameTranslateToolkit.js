/* Game Translate Toolkit 0.1 — original RPG Maker MV/MZ runtime adapter.
 * Installed as the last enabled plugin. No upstream API key is stored here.
 * MIT license; https://github.com/rpgtkoolmv/corescript is the hook reference.
 */
(function () {
    "use strict";
    if (window.GameTranslateToolkitAdapter) return;
    var p = PluginManager.parameters("GameTranslateToolkit");
    if (!p.Endpoint || !p.Token || !p.GameId) return;
    var cache = Object.create(null), pending = Object.create(null), failures = Object.create(null);
    var jobs = [], active = 0, lastWarning = 0, cacheKeys = [], applied = 0;
    var statusSession = Date.now().toString(36) + "-" + Math.random().toString(36).slice(2);
    var maxParallel = 3, maxQueued = 256, maxCache = 5000;
    var controls = /(?:\\(?:[A-Za-z]+(?:\[[^\]\r\n]*\])?|[\\{}$.|!><^])|\x1b(?:[A-Za-z]+(?:\[[^\]\r\n]*\])?|[{}$.|!><^])|%\d*\$?[sdiuf]|\{[^{}\r\n]*\})/g;

    function usable(text) {
        return typeof text === "string" && text.length > 0 && text.length <= 8192 && /[A-Za-z\u00c0-\uffff]/.test(text.replace(controls, ""));
    }
    function safe(source, translated) {
        if (typeof translated !== "string" || !translated.trim()) return false;
        return JSON.stringify(source.match(controls) || []) === JSON.stringify(translated.match(controls) || []);
    }
    function warn() {
        if (Date.now() - lastWarning > 60000) {
            lastWarning = Date.now();
            console.warn("[Game Translate Toolkit] 本地翻译未完成，保留原文。请查看工具的运行日志。");
        }
    }
    function remember(source, translated) {
        if (!Object.prototype.hasOwnProperty.call(cache, source)) cacheKeys.push(source);
        cache[source] = translated;
        while (cacheKeys.length > maxCache) delete cache[cacheKeys.shift()];
    }
    function pump() {
        while (active < maxParallel && jobs.length) run(jobs.shift());
    }
    function run(job) {
        active++;
        var xhr = new XMLHttpRequest(), finished = false;
        function done(error, translated) {
            if (finished) return;
            finished = true; active--;
            if (!error && safe(job.text, translated) && translated !== job.text) {
                remember(job.text, translated);
                var listeners = pending[job.text] || [];
                delete pending[job.text]; delete failures[job.text];
                listeners.forEach(function (listener) { try { listener(translated); } catch (_) {} });
            } else if (job.attempt < 1) {
                job.attempt++; jobs.push(job);
            } else {
                delete pending[job.text]; failures[job.text] = Date.now() + 60000;
                warn();
            }
            setTimeout(pump, 0);
        }
        try {
            xhr.open("POST", p.Endpoint, true);
            xhr.timeout = 65000;
            xhr.setRequestHeader("Content-Type", "application/json");
            xhr.onload = function () {
                try {
                    if (xhr.status < 200 || xhr.status >= 300) return done(true);
                    done(false, JSON.parse(xhr.responseText).text);
                } catch (_) { done(true); }
            };
            xhr.onerror = xhr.ontimeout = xhr.onabort = function () { done(true); };
            xhr.send(JSON.stringify({ token: p.Token, gameId: p.GameId, text: job.text,
                from: p.SourceLanguage || "auto", to: p.TargetLanguage || "zh-CN" }));
        } catch (_) { done(true); }
    }
    function request(text, listener) {
        if (!usable(text)) return;
        if (Object.prototype.hasOwnProperty.call(cache, text)) {
            if (listener) listener(cache[text]);
            return;
        }
        if (failures[text] > Date.now()) return;
        if (pending[text]) {
            if (listener && pending[text].length < 32) pending[text].push(listener);
            return;
        }
        if (jobs.length + active >= maxQueued) return;
        pending[text] = listener ? [listener] : [];
        jobs.push({ text: text, attempt: 0 }); pump();
    }
    function translate(text) { return typeof text === "string" && cache[text] ? cache[text] : text; }
    function refresh(owner) {
        if (!owner || !owner.contents || !owner.contents.context || owner._gttRefreshing) return;
        // A dialogue uses its own guarded callback, never a generic window refresh.
        if (typeof Window_Message !== "undefined" && owner instanceof Window_Message) return;
        if (typeof owner.refresh === "function") {
            owner._gttRefreshing = true;
            try {
                if (typeof Window_ChoiceList !== "undefined" && owner instanceof Window_ChoiceList && typeof owner.updatePlacement === "function") owner.updatePlacement();
                owner.refresh();
            } finally { owner._gttRefreshing = false; }
        }
    }

    var originalDrawText = Window_Base.prototype.drawText;
    Window_Base.prototype.drawText = function (text) {
        var owner = this, args = Array.prototype.slice.call(arguments);
        args[0] = translate(text);
        if (args[0] === text) request(text, function () { refresh(owner); });
        var drawn = originalDrawText.apply(this, args);
        if (args[0] !== text) applied++;
        return drawn;
    };
    var originalDrawTextEx = Window_Base.prototype.drawTextEx;
    Window_Base.prototype.drawTextEx = function (text) {
        var owner = this, args = Array.prototype.slice.call(arguments);
        args[0] = translate(text);
        if (args[0] === text) request(text, function () { refresh(owner); });
        var drawn = originalDrawTextEx.apply(this, args);
        if (args[0] !== text) applied++;
        return drawn;
    };
    var originalTextWidth = Window_Base.prototype.textWidth;
    Window_Base.prototype.textWidth = function (text) { return originalTextWidth.call(this, translate(text)); };

    function renderMessage(owner, translated) {
        if (!owner.contents) return;
        var escapeHandler = owner.processEscapeCharacter;
        // Redraw formatting without replaying message waits, pauses or gold-window actions.
        owner.processEscapeCharacter = function (code, state) {
            if (/^[\$\.\|!><\^]$/.test(code)) return;
            if (code === "S") { this.obtainEscapeParam(state); return; }
            return escapeHandler.call(this, code, state);
        };
        try {
            owner.contents.clear();
            owner.resetFontSettings();
            var face = $gameMessage.faceName();
            if (face && typeof owner.drawFace === "function") owner.drawFace(face, $gameMessage.faceIndex(), 0, 0);
            var x = typeof owner.newLineX === "function" ? owner.newLineX({ rtl: false }) : (face ? 168 : 0);
            originalDrawTextEx.call(owner, translated, x, 0, owner.contents.width - x);
            applied++;
            owner._textState = null;
            owner._waitCount = 0;
            var activeChoice = owner._choiceWindow && owner._choiceWindow.active;
            if (!activeChoice && typeof owner.startInput === "function" && owner.startInput()) owner.pause = false;
            else if (!activeChoice) owner.pause = true;
        } finally { owner.processEscapeCharacter = escapeHandler; }
    }

    var originalStartMessage = Window_Message.prototype.startMessage;
    Window_Message.prototype.startMessage = function () {
        var owner = this, text = $gameMessage.allText(), seq = (this._gttSeq || 0) + 1;
        this._gttSeq = seq; this._gttSource = text;
        var translated = translate(text), originalTexts = $gameMessage._texts;
        if (translated !== text) $gameMessage._texts = [translated];
        try { originalStartMessage.apply(this, arguments); }
        finally { $gameMessage._texts = originalTexts; }
        if (translated === text) request(text, function (result) {
            if (owner._gttSeq !== seq || owner._gttSource !== text || $gameMessage.allText() !== text) return;
            if (!owner.isOpen() && !owner.isOpening()) return;
            renderMessage(owner, result);
        });
        var choices = $gameMessage.choices ? $gameMessage.choices() : [];
        choices.forEach(function (choice) { request(choice, function () { refresh(owner._choiceWindow); }); });
    };
    var originalTerminateMessage = Window_Message.prototype.terminateMessage;
    Window_Message.prototype.terminateMessage = function () {
        this._gttSeq = (this._gttSeq || 0) + 1; this._gttSource = null;
        return originalTerminateMessage.apply(this, arguments);
    };

    // The installer embeds only local font preferences; no upstream credentials.
    var fontOptions = /*__GTT_FONT_SETTINGS__*/ { mode: "auto", file: "GameTranslateToolkitCJK.otf" };
    var fontName = "GameTranslateToolkitCJK";
    if (fontOptions.mode !== "original") {
    if (typeof FontManager !== "undefined" && typeof FontManager.load === "function") FontManager.load(fontName, fontOptions.file);
    else if (typeof Graphics.loadFont === "function") Graphics.loadFont(fontName, "fonts/" + fontOptions.file);
    if (typeof Window_Base.prototype.standardFontFace === "function") {
        var originalFontFace = Window_Base.prototype.standardFontFace;
        Window_Base.prototype.standardFontFace = function () { return fontName + ", " + originalFontFace.apply(this, arguments) + ", Microsoft YaHei, sans-serif"; };
    }
    if (typeof Game_System !== "undefined" && typeof Game_System.prototype.mainFontFace === "function") {
        var originalMainFont = Game_System.prototype.mainFontFace;
        Game_System.prototype.mainFontFace = function () { return fontName + ", " + originalMainFont.apply(this, arguments) + ", Microsoft YaHei, sans-serif"; };
    }
    }
    function reportStatus() {
        try {
            var fontStatus = fontOptions.mode === "original" ? "original" : "unknown";
            if (fontOptions.mode !== "original" && typeof document !== "undefined" && document.fonts && typeof document.fonts.check === "function") fontStatus = document.fonts.check("16px " + fontName) ? "loaded" : "unavailable";
            var xhr = new XMLHttpRequest();
            xhr.open("POST", p.Endpoint.replace(/\/bridge\/translate\/?$/, "/bridge/status"), true);
            xhr.timeout = 3000; xhr.setRequestHeader("Content-Type", "application/json");
            xhr.send(JSON.stringify({ gameId: p.GameId, token: p.Token, session: statusSession, applied: applied, fontStatus: fontStatus }));
        } catch (_) {}
    }
    // Heartbeats contain counters only. They do not send text or assert pixel/glyph correctness.
    if (typeof setInterval === "function") setInterval(reportStatus, 5000);
    window.GameTranslateToolkitAdapter = { version: "0.2.0", translate: translate, request: request,
        stats: function () { return { cached: cacheKeys.length, queued: jobs.length, active: active, applied: applied }; } };
    console.info("[Game Translate Toolkit] RPG Maker MV/MZ 适配器已加载，原文先显示，译文异步更新。");
})();
