// Native GDI regressions for the actual LunaHook source, not a reimplementation.
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <cstdint>
#include <cstddef>
#include <string>
#include <list>
#include <algorithm>
#include <mutex>
#include <iostream>
#include <vector>
#include <cstring>
#include <cwchar>
#include <thread>
#include <atomic>
#include "hijackfuns.h"

struct FontSettings {
    float FontSizeRelative = 1.0f;
    wchar_t fontFamily[100] = L"SimHei";
    bool fontCharSetEnabled = false;
    uint8_t fontCharSet = 0;
};
FontSettings settings;
FontSettings *commonsharedmem = &settings;
#include "font-under-test.inc"

struct Surface {
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP bitmap;
    HGDIOBJ oldBitmap;
    HGDIOBJ oldFont;
    HFONT font;
    LOGFONTW original = {};
    uint32_t *pixels = nullptr;

    Surface(int height = 32, int width = 0, int api = 0) {
        BITMAPINFO bi = {};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = 1024;
        bi.bmiHeader.biHeight = -256;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bitmap = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, (void**)&pixels, nullptr, 0);
        oldBitmap = SelectObject(dc, bitmap);
        original.lfHeight = height;
        original.lfWidth = width;
        original.lfWeight = FW_NORMAL;
        original.lfCharSet = DEFAULT_CHARSET;
        wcscpy_s(original.lfFaceName, L"SimHei");
        LOGFONTA ansi = {};
        memcpy(&ansi, &original, offsetof(LOGFONTA, lfFaceName));
        strcpy_s(ansi.lfFaceName, "SimHei");
        switch (api) {
        case 1: font = Hijack::newCreateFontIndirectW(&original); break;
        case 2: font = Hijack::newCreateFontIndirectA(&ansi); break;
        case 3: font = Hijack::newCreateFontW(height, width, 0, 0, FW_NORMAL, 0, 0, 0,
                        DEFAULT_CHARSET, 0, 0, 0, 0, L"SimHei"); break;
        case 4: font = Hijack::newCreateFontA(height, width, 0, 0, FW_NORMAL, 0, 0, 0,
                        DEFAULT_CHARSET, 0, 0, 0, 0, "SimHei"); break;
        default: font = CreateFontIndirectW(&original); break;
        }
        oldFont = SelectObject(dc, font);
        SetBkMode(dc, TRANSPARENT);
        SetTextColor(dc, RGB(255,255,255));
    }
    ~Surface() {
        SelectObject(dc, oldFont);
        SelectObject(dc, oldBitmap);
        DeleteObject(font);
        DeleteObject(bitmap);
        DeleteDC(dc);
    }
    bool draw(int expectedHeight, int expectedWidth, bool checkFamily = true) {
        memset(pixels, 0, 1024*256*4);
        SIZE extent = {};
        if (!Hijack::newGetTextExtentPoint32W(dc, L"中文翻译测试", 6, &extent) ||
            !Hijack::newTextOutW(dc, 4, 4, L"中文翻译测试", 6)) return false;
        GdiFlush();
        LOGFONTW selected = {};
        auto handle = GetCurrentObject(dc, OBJ_FONT);
        if (GetObjectW(handle, sizeof(selected), &selected) != sizeof(selected)) return false;
        TEXTMETRICW metrics = {};
        GetTextMetricsW(dc, &metrics);
        unsigned ink = 0;
        for (int i = 0; i < 1024*256; i++) if (pixels[i] & 0xffffff) ink++;
        const auto family = settings.fontFamily[0] ? settings.fontFamily : original.lfFaceName;
        WORD indices[6] = {};
        auto indexed = GetGlyphIndicesW(dc, L"中文翻译测试", 6, indices, GGI_MARK_NONEXISTING_GLYPHS);
        bool glyphs = indexed != GDI_ERROR;
        for (auto index : indices) if (index == 0xffff) glyphs = false;
        return selected.lfHeight == expectedHeight && selected.lfWidth == expectedWidth &&
            selected.lfWeight == original.lfWeight &&
            (!checkFamily || wcscmp(selected.lfFaceName, family) == 0) &&
            extent.cy == metrics.tmHeight && glyphs && ink > 100;
    }
};

int main() {
    int checks = 0;
    auto expect = [&checks](bool result, const char *name) {
        if (!result) {
            std::cerr << "FAILED: " << name << std::endl;
            ExitProcess(1);
        }
        checks++;
    };
    // Creation hooks and drawing hooks may both be active in the same engine.
    for (int api = 0; api <= 4; api++) {
        for (int originalHeight : {32, -32}) {
            settings.FontSizeRelative = 0.7f;
            wcscpy_s(settings.fontFamily, L"SimHei");
            Surface surface(originalHeight, 16, api);
            for (int percent : {70, 120, 50, 200, 100, 70, 100}) {
                settings.FontSizeRelative = percent / 100.0f;
                int height = (int)(originalHeight * settings.FontSizeRelative);
                int width = (int)(16 * settings.FontSizeRelative);
                bool stable = true;
                for (int i = 0; i < 200; i++) stable &= surface.draw(height, width);
                expect(stable, "Creation plus drawing and live ratio changes");
            }
        }
    }
    // When integer scaling rounds different baselines to one height, cached
    // replacements must retain each original baseline when the ratio changes.
    settings.FontSizeRelative = 0.7f;
    Surface first(30), second(31);
    expect(first.draw(21, 0) && second.draw(21, 0), "Rounded heights");
    settings.FontSizeRelative = 1.2f;
    expect(first.draw(36, 0) && second.draw(37, 0), "Distinct original baselines");

    // Family changes (including a Chinese Windows family name) must not
    // multiply the previous size, and clearing customization must restore it.
    settings.FontSizeRelative = 0.7f;
    Surface changing;
    for (const wchar_t *family : {L"SimHei", L"Microsoft YaHei UI", L"黑体", L"", L"SimHei"}) {
        wcscpy_s(settings.fontFamily, family);
        expect(changing.draw(22, 0), "Live font family changes");
    }
    settings.FontSizeRelative = 1.0f;
    settings.fontFamily[0] = 0;
    expect(changing.draw(32, 0), "Restore original font at 100 percent");

    // Delete a generated font only after deselection, as an engine reset does.
    wcscpy_s(settings.fontFamily, L"SimHei");
    settings.FontSizeRelative = 0.7f;
    Surface reset(39);
    expect(reset.draw(27, 0), "Initial reset font");
    auto replacement = GetCurrentObject(reset.dc, OBJ_FONT);
    SelectObject(reset.dc, reset.font);
    expect(DeleteObject(replacement) != 0, "Delete deselected cached font");
    expect(reset.draw(27, 0), "Ignore invalid cached font handle");

    // Long family names are safely bounded before touching LOGFONTW storage.
    for (auto &character : settings.fontFamily) character = L'A';
    settings.fontFamily[99] = 0;
    expect(reset.draw(27, 0, false), "Bounded font family copying");
    wcscpy_s(settings.fontFamily, L"SimHei");

    // Independent game drawing threads can use the shared cache safely.
    std::atomic<bool> concurrentOk = true;
    std::vector<std::thread> threads;
    for (int thread = 0; thread < 8; thread++)
        threads.emplace_back([&concurrentOk] {
            Surface surface(35);
            for (int i = 0; i < 200; i++)
                if (!surface.draw(24, 0)) concurrentOk = false;
        });
    for (auto &thread : threads) thread.join();
    expect(concurrentOk, "Concurrent draw calls");

    // Cached fonts do not accumulate one GDI object per redraw.
    Surface steady;
    expect(steady.draw(22, 0), "Warm font cache");
    auto before = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    bool stable = true;
    for (int i = 0; i < 1000; i++) stable &= steady.draw(22, 0);
    auto added = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS) - before;
    expect(stable && added == 0, "No growing GDI object count");
    std::cout << "{\"checks\":" << checks << ",\"passed\":true,\"steadyGdiObjectsAdded\":" << added << "}";
}
