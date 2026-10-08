"""Compile the actual LunaHook font switcher and BGI GDI wrappers, then render on a DIB.

No game files, API services, or desktop windows are accessed by this regression.
"""
import argparse
import json
import hashlib
import pathlib
import subprocess

ROOT = pathlib.Path(__file__).resolve().parents[1]
UPSTREAM = ROOT / 'tests/hook-research/source/LunaTranslator-12.0.1'
COMPILERS = ROOT / 'tests/toolchain/llvm-mingw-20260908-ucrt-x86_64/bin'

DRIVER = r'''
#define UNICODE
#define _UNICODE
#include <windows.h>
#include <cstdint>
#include <string>
#include <list>
#include <algorithm>
#include <mutex>
#include <iostream>
#include <vector>
#include <cstring>
#include <cwchar>
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

int main(int argc, char **argv) {
    settings.FontSizeRelative = std::stof(argv[1]) / 100.0f;
    int repeats = argc > 2 ? std::stoi(argv[2]) : 20;
    HDC dc = CreateCompatibleDC(nullptr);
    BITMAPINFO bi = {};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = 1024;
    bi.bmiHeader.biHeight = -256;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    uint32_t *pixels = nullptr;
    auto bitmap = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, (void**)&pixels, nullptr, 0);
    auto oldBitmap = SelectObject(dc, bitmap);
    auto font = CreateFontW(32, 0, 0, 0, FW_NORMAL, FALSE, FALSE, FALSE,
        DEFAULT_CHARSET, OUT_DEFAULT_PRECIS, CLIP_DEFAULT_PRECIS, DEFAULT_QUALITY,
        DEFAULT_PITCH, L"SimHei");
    auto oldFont = SelectObject(dc, font);
    SetBkMode(dc, TRANSPARENT);
    SetTextColor(dc, RGB(255,255,255));
    const wchar_t *text = L"中文翻译测试";
    DWORD handlesBefore = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    std::cout << "{\"percent\":" << argv[1] << ",\"samples\":[";
    for (int i = 0; i < repeats; i++) {
        std::memset(pixels, 0, 1024*256*4);
        SIZE extent = {};
        BOOL measured = Hijack::newGetTextExtentPoint32W(dc, text, 6, &extent);
        BOOL drawn = Hijack::newTextOutW(dc, 4, 4, text, 6);
        GdiFlush();
        TEXTMETRICW metric = {};
        GetTextMetricsW(dc, &metric);
        unsigned ink = 0;
        int inkHeight = 0;
        for (int y = 0; y < 256; y++) {
            bool row = false;
            for (int x = 0; x < 1024; x++) {
                if ((pixels[y*1024+x] & 0xffffff) != 0) { ink++; row = true; }
            }
            if (row) inkHeight++;
        }
        if (i) std::cout << ",";
        std::cout << "{\"height\":" << metric.tmHeight << ",\"extentWidth\":" << extent.cx
            << ",\"extentHeight\":" << extent.cy << ",\"ink\":" << ink
            << ",\"inkHeight\":" << inkHeight << ",\"ok\":" << (measured && drawn ? "true" : "false") << "}";
    }
    DWORD handlesAfter = GetGuiResources(GetCurrentProcess(), GR_GDIOBJECTS);
    std::cout << "],\"handlesAdded\":" << (handlesAfter - handlesBefore) << "}";
    SelectObject(dc, oldFont);
    SelectObject(dc, oldBitmap);
    DeleteObject(font);
    DeleteObject(bitmap);
    DeleteDC(dc);
}
'''

def function(source, marker):
    start = source.index(marker)
    brace = source.index('{', start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--source', type=pathlib.Path, default=UPSTREAM)
    parser.add_argument('--label', default='upstream')
    parser.add_argument('--fixed', action='store_true')
    parser.add_argument('--compiler-dir', type=pathlib.Path, default=COMPILERS)
    parser.add_argument('--output-dir', type=pathlib.Path)
    args = parser.parse_args()
    output = (args.output_dir or (ROOT / 'tests/artifacts/font-resize-074' / args.label)).resolve()
    output.mkdir(parents=True, exist_ok=True)
    file = args.source / 'src/NativeImpl/LunaHook/LunaHook/hijackfuns.cc'
    source = file.read_text(encoding='utf-8-sig')
    # These are production source slices, not copies of the resizing algorithm.
    sliced = source[:source.index('/** Fonts */')]
    if args.fixed:
        sliced += source[source.index('/** Fonts */'):source.index('/** Encoding */')]
    for marker in ['BOOL WINAPI Hijack::newGetTextExtentPoint32W(', 'BOOL WINAPI Hijack::newTextOutW(']:
        sliced += '\n' + function(source, marker) + '\n'
    (output / 'font-under-test.inc').write_text(sliced, encoding='utf-8')
    (output / 'hijackfuns.h').write_bytes(file.with_suffix('.h').read_bytes())
    (output / 'driver.cpp').write_text(DRIVER, encoding='utf-8')
    report = {'source': str(file), 'sha256': hashlib.sha256(file.read_bytes()).hexdigest(),
              'label': args.label, 'architectures': {}}
    for arch, triple in [('x86', 'i686'), ('x64', 'x86_64')]:
        exe = output / f'FontResize-{arch}.exe'
        command = [str(args.compiler_dir / f'{triple}-w64-mingw32-clang++.exe'), '-std=c++17',
                   '-fms-extensions', '-O2', '-static', str(output / 'driver.cpp'),
                   '-o', str(exe), '-lgdi32', '-luser32']
        subprocess.run(command, check=True, capture_output=True, text=True)
        results = []
        for percent in [70, 100, 120]:
            count = 1000 if args.fixed else (12 if percent == 120 else 20)
            result = json.loads(subprocess.check_output([str(exe), str(percent), str(count)], text=True))
            results.append(result)
            samples = result['samples']
            summary = {k: result[k] for k in ('percent', 'handlesAdded')}
            summary.update(first=samples[0], last=samples[-1], samples=len(samples))
            print(arch, json.dumps(summary, ensure_ascii=False), flush=True)
            if args.fixed:
                assert all(s == samples[0] and s['ok'] and s['inkHeight'] > 10 for s in samples)
                assert result['handlesAdded'] <= 2
        report['architectures'][arch] = results
        if args.fixed:
            scenarios = output / f'FontResizeScenarios-{arch}.exe'
            command = [str(args.compiler_dir / f'{triple}-w64-mingw32-clang++.exe'), '-std=c++17',
                       '-fms-extensions', '-O2', '-static', '-I', str(output),
                       str(pathlib.Path(__file__).with_name('HookFontResizeScenarios.cpp')),
                       '-o', str(scenarios), '-lgdi32', '-luser32']
            subprocess.run(command, check=True, capture_output=True, text=True)
            scenario_report = json.loads(subprocess.check_output([str(scenarios)], text=True))
            report.setdefault('scenarios', {})[arch] = scenario_report
            print(arch, 'scenarios', json.dumps(scenario_report), flush=True)
    (output / 'verification.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')

if __name__ == '__main__':
    main()
