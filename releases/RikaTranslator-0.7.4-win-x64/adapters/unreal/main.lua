-- Game Translate Toolkit: UE4SS UMG adapter. No HTTP, upstream credentials, or source text logging.
local config = require("config")
local root = "./ue4ss/Mods/GameTranslateToolkit/"
local requests, responses = root .. "ipc/requests/", root .. "ipc/responses/"
local session = tostring(os.time()) .. "-" .. tostring(math.random(100000, 999999999))
local cache, outputs, pending, retryAt, hooks = {}, {}, {}, {}, {}
local nextId, pendingCount, ticks, scanning, applying = 0, 0, 0, false, false
local fallbackFont
local fontMode = config.fontMode or "auto"
local fontPath = config.fontPath or "/Engine/EngineFonts/Roboto.Roboto"
local useFont = fontMode == "custom" or (fontMode == "auto" and config.target and config.target:sub(1, 2) == "zh")
local state = { loaded = true, major = 0, minor = 0, scans = 0, textBlocks = 0,
    richTextBlocks = 0, visible = 0, requested = 0, completed = 0, applied = 0,
    verified = 0, failed = 0, hooks = 0, heartbeat = 0, fontOverrides = 0, fontMissing = 0 }

local function log(value) print("[GameTranslateToolkit] " .. value .. "\n") end
local function valid(object)
    local ok, value = pcall(function() return object and object:IsValid() end)
    return ok and value
end
local function textOf(object)
    local ok, value = pcall(function() return object:GetText():ToString() end)
    return ok and type(value) == "string" and value or nil
end
local function hex(value) return (value:gsub(".", function(c) return string.format("%02X", string.byte(c)) end)) end
local function unhex(value)
    if #value == 0 or #value > 120000 or #value % 2 ~= 0 or value:find("[^0-9a-fA-F]") then return nil end
    return (value:gsub("..", function(pair) return string.char(tonumber(pair, 16)) end))
end

local function visible(object)
    local current = object
    for depth = 1, 48 do
        if not valid(current) then return false end
        local ok, shown = pcall(function() return current:IsVisible() end)
        if ok and not shown then return false end
        local opacityOk, opacity = pcall(function() return current:GetRenderOpacity() end)
        if opacityOk and type(opacity) == "number" and opacity < 0.001 then return false end
        local viewOk, inView = pcall(function() return current:IsInViewport() end)
        if viewOk and inView then return true end
        local parentOk, parent = pcall(function() return current:GetParent() end)
        if parentOk and valid(parent) then
            local switchOk, active = pcall(function() return parent:GetActiveWidget() end)
            if switchOk and (not valid(active) or active:GetFullName() ~= current:GetFullName()) then return false end
            current = parent
        else
            -- Nested UserWidgets need not be in the viewport themselves. Their parents/owners can be.
            local outerOk, outer = pcall(function() return current:GetOuter() end)
            if not outerOk then return false end
            current = outer
        end
    end
    return false
end

local function eligible(source)
    if not source or #source == 0 or #source > 7500 or outputs[source] then return false end
    if not source:find("[%a\128-\255]") then return false end
    if config.source == "ja" and not source:find("[\128-\255]") then return false end
    return true
end

local function request(source)
    if not eligible(source) or cache[source] or pending[source] or pendingCount >= 16 then return end
    if retryAt[source] and retryAt[source] > os.time() then return end
    nextId = nextId + 1
    local id = session .. "-" .. tostring(nextId)
    local file = io.open(requests .. id .. ".tmp", "wb")
    if not file then retryAt[source] = os.time() + 10; return end
    file:write(config.token .. "\n" .. hex(source)); file:close()
    if not os.rename(requests .. id .. ".tmp", requests .. id .. ".req") then
        os.remove(requests .. id .. ".tmp"); retryAt[source] = os.time() + 10; return
    end
    pending[source] = { id = id, at = os.time() }
    pendingCount = pendingCount + 1
    state.requested = state.requested + 1
end

local function consume()
    for source, job in pairs(pending) do
        local file = io.open(responses .. job.id .. ".res", "rb")
        if file then
            local data = file:read(120004); file:close()
            local translated = data and data:sub(1, 2) == "O\n" and unhex(data:sub(3)) or nil
            if translated then cache[source] = translated; outputs[translated] = true; state.completed = state.completed + 1
            else retryAt[source] = os.time() + 30; state.failed = state.failed + 1 end
            os.remove(responses .. job.id .. ".res")
            pending[source] = nil; pendingCount = pendingCount - 1
        elseif os.time() - job.at > 180 then
            os.remove(requests .. job.id .. ".req")
            retryAt[source] = os.time() + 30
            pending[source] = nil; pendingCount = pendingCount - 1; state.failed = state.failed + 1
        end
    end
end

local function applyFont(object)
    if not useFont or not valid(fallbackFont) then return end
    pcall(function()
        local ok, font = pcall(function() return object.Font end)
        local rich = not ok or font == nil
        if rich then font = object.DefaultTextStyle.Font end
        local typeface = config.fontTypeface or "Regular"
        local nameOk, name = pcall(function() return font.TypefaceFontName:ToString() end)
        if not nameOk then name = tostring(font.TypefaceFontName) end
        if valid(font.FontObject) and font.FontObject:GetFullName() == fallbackFont:GetFullName() and name == typeface then return end
        font.FontObject = fallbackFont
        font.TypefaceFontName = FName(typeface)
        if rich then object:SetDefaultFont(font) else object:SetFont(font) end
        state.fontOverrides = state.fontOverrides + 1
    end)
end

local function apply(object, source)
    local translated = cache[source]
    if not translated or translated == source or applying or not valid(object) or textOf(object) ~= source then return end
    applying = true
    applyFont(object)
    local ok = pcall(function() object:SetText(FText(translated)) end)
    applying = false
    if ok then
        state.applied = state.applied + 1
        if textOf(object) == translated then state.verified = state.verified + 1 end
    else state.failed = state.failed + 1 end
end

local function scan(class, field)
    local ok, objects = pcall(function() return FindAllOf(class) end)
    if not ok or type(objects) ~= "table" then return end
    state[field] = #objects
    for _, object in ipairs(objects) do
        if valid(object) and visible(object) then
            state.visible = state.visible + 1
            local source = textOf(object)
            if source and outputs[source] then applyFont(object)
            elseif source and cache[source] then apply(object, source)
            elseif source then request(source) end
        end
    end
end

local function tryHook(path)
    if hooks[path] then return end
    local found, fn = pcall(function() return StaticFindObject(path) end)
    if not found or not valid(fn) then return end
    local ok = pcall(function()
        RegisterHook(path, function(context, value)
            if applying or scanning then return end
            pcall(function()
                local object = context:get()
                if not valid(object) or not visible(object) then return end
                local source = value:get():ToString()
                if cache[source] then
                    applyFont(object)
                    value:set(FText(cache[source]))
                else request(source) end
            end)
        end)
    end)
    if ok then hooks[path] = true; state.hooks = state.hooks + 1 end
end

local function saveStatus()
    state.heartbeat = os.time()
    local parts = {}
    for key, value in pairs(state) do parts[#parts + 1] = string.format('"%s":%s', key, tostring(value)) end
    local file = io.open(root .. "status.json.tmp", "wb")
    if file then
        file:write("{" .. table.concat(parts, ",") .. "}"); file:close()
        os.remove(root .. "status.json"); os.rename(root .. "status.json.tmp", root .. "status.json")
    end
end

pcall(function() state.major = UnrealVersion.GetMajor(); state.minor = UnrealVersion.GetMinor() end)
log(string.format("loaded_engine_%d_%d", state.major, state.minor))
saveStatus()
local handle = LoopInGameThreadWithDelay(250, function()
    local ok = pcall(function()
        consume(); ticks = ticks + 1
        if ticks % 4 == 0 then
            if useFont and not valid(fallbackFont) then
                local fonts = FindAllOf("Font")
                for _, font in ipairs(fonts or {}) do
                    if valid(font) then
                        local fullName = font:GetFullName()
                        if fullName == fontPath or fullName == "Font " .. fontPath then fallbackFont = font; break end
                    end
                end
                state.fontMissing = valid(fallbackFont) and 0 or 1
            end
            tryHook("/Script/UMG.TextBlock:SetText"); tryHook("/Script/UMG.RichTextBlock:SetText")
            scanning = true; state.visible = 0; state.scans = state.scans + 1
            scan("TextBlock", "textBlocks"); scan("RichTextBlock", "richTextBlocks")
            scanning = false; saveStatus()
        end
    end)
    if not ok then scanning = false; applying = false; state.failed = state.failed + 1 end
end)
ModRef.OnUnload = function() if handle then pcall(function() CancelDelayedAction(handle) end) end end
