// Rika Translator font resizing fix, 2026-10-08.
// Derived from LunaTranslator v12.0.1; retain the upstream GPL license.
#pragma intrinsic(_ReturnAddress)

// Disable only for debugging purpose
// #define HIJACK_GDI_FONT
// #define HIJACK_GDI_TEXT

#define DEF_FUN(_f) Hijack::_f##_fun_t Hijack::old##_f = ::_f;
DEF_FUN(CreateFontA)
DEF_FUN(CreateFontW)
DEF_FUN(CreateFontIndirectA)
DEF_FUN(CreateFontIndirectW)
DEF_FUN(GetGlyphOutlineA)
DEF_FUN(GetGlyphOutlineW)
DEF_FUN(GetTextExtentPoint32A)
DEF_FUN(GetTextExtentPoint32W)
DEF_FUN(GetTextExtentExPointA)
DEF_FUN(GetTextExtentExPointW)
DEF_FUN(GetCharABCWidthsA)
DEF_FUN(GetCharABCWidthsW)
DEF_FUN(TextOutA)
DEF_FUN(TextOutW)
DEF_FUN(ExtTextOutA)
DEF_FUN(ExtTextOutW)
DEF_FUN(DrawTextA)
DEF_FUN(DrawTextW)
DEF_FUN(DrawTextExA)
DEF_FUN(DrawTextExW)
DEF_FUN(CharNextA)
// DEF_FUN(CharNextW)
// DEF_FUN(CharNextExA)
// DEF_FUN(CharNextExW)
DEF_FUN(CharPrevA)
// DEF_FUN(CharPrevW)
DEF_FUN(MultiByteToWideChar)
DEF_FUN(WideCharToMultiByte)
#undef DEF_FUN

/** Helper */
namespace
{
  inline const wchar_t *maybe_disabled_fontFamily()
  {
    return Hijack::Disable_Font_Switch ? L"" : commonsharedmem->fontFamily;
  }
}
namespace
{ // unnamed
  void ScaleFont(int &lfHeight, int &lfWidth)
  {
    if (commonsharedmem->FontSizeRelative == 1.)
      return;
    auto scale = commonsharedmem->FontSizeRelative;
    if (lfHeight != 0)
    {
      bool _ = lfHeight > 0;
      lfHeight = (LONG)(lfHeight * scale);

      if (lfHeight == 0)
      {
        lfHeight = _ ? 1 : -1;
      }
    }

    if (lfWidth != 0)
    {
      bool _ = lfWidth > 0;
      lfWidth = (LONG)(lfWidth * scale);
      if (lfWidth == 0)
      {
        lfWidth = _ ? 1 : -1;
      }
    }
  }
  template <typename T>
  void ScaleFont(T *plogf)
  {
    if (commonsharedmem->FontSizeRelative == 1.)
      return;
    auto scale = commonsharedmem->FontSizeRelative;
    if (plogf->lfHeight != 0)
    {
      bool _ = plogf->lfHeight > 0;
      plogf->lfHeight = (LONG)(plogf->lfHeight * scale);

      if (plogf->lfHeight == 0)
      {
        plogf->lfHeight = _ ? 1 : -1;
      }
    }

    if (plogf->lfWidth != 0)
    {
      bool _ = plogf->lfWidth > 0;
      plogf->lfWidth = (LONG)(plogf->lfWidth * scale);
      if (plogf->lfWidth == 0)
      {
        plogf->lfWidth = _ ? 1 : -1;
      }
    }
  }
  UINT8 systemCharSet()
  {
    enum CodePage
    {
      NullCodePage = 0,
      Utf8CodePage = 65001, // UTF-8
      Utf16CodePage = 1200, // UTF-16
      SjisCodePage = 932,   // SHIFT-JIS
      GbkCodePage = 936,    // GB2312
      KscCodePage = 949,    // EUC-KR
      Big5CodePage = 950,   // BIG5
      TisCodePage = 874,    // TIS-620
      Koi8CodePage = 866    // KOI8-R
    };
    auto systemCodePage = ::GetACP();
    switch (systemCodePage)
    {
    case TisCodePage:
      return THAI_CHARSET;
    case Koi8CodePage:
      return RUSSIAN_CHARSET;
    case SjisCodePage:
      return SHIFTJIS_CHARSET;
    case GbkCodePage:
      return GB2312_CHARSET;
    case Big5CodePage:
      return CHINESEBIG5_CHARSET;

    case KscCodePage:
      return HANGUL_CHARSET;
    case 1361:
      return JOHAB_CHARSET; // alternative Korean character set

    case 1250:
      return EASTEUROPE_CHARSET;
    case 1251:
      return RUSSIAN_CHARSET; // cyrillic
    case 1253:
      return GREEK_CHARSET;
    case 1254:
      return TURKISH_CHARSET;

    case 862:
      return HEBREW_CHARSET; // obsolete
    case 1255:
      return HEBREW_CHARSET;

    case 1256:
      return ARABIC_CHARSET;
    case 1257:
      return BALTIC_CHARSET;
    case 1258:
      return VIETNAMESE_CHARSET;

    // default: return DEFAULT_CHARSET;
    default:
      return 0;
    }
  }
  void customizeLogFontA(LOGFONTA *lplf)
  {

    if (commonsharedmem->fontCharSetEnabled)
    {
      auto charSet = commonsharedmem->fontCharSet;
      if (!charSet)
        charSet = systemCharSet();
      if (charSet)
        lplf->lfCharSet = charSet;
    }
    /*
    if (s->fontWeight)
      lplf->lfWeight = s->fontWeight;
    if (s->isFontScaled()) {
      lplf->lfWidth *= s->fontScale;
      lplf->lfHeight *= s->fontScale;
    }
    */
  }

  void customizeLogFontW(LOGFONTW *lplf)
  {
    customizeLogFontA((LOGFONTA *)lplf);

    std::wstring s = maybe_disabled_fontFamily();
    if (!s.empty())
    {
      wcsncpy_s(lplf->lfFaceName, LF_FACESIZE, s.c_str(), _TRUNCATE);
    }
  }

  // LogFont manager

  class LogFontManager
  {
    struct FontEntry
    {
      HFONT handle;
      LOGFONTW actual;
      LOGFONTW requested;
      LOGFONTW baseline;
    };
    std::list<FontEntry> fonts_;
    std::mutex mutex_;

    void discardStale()
    {
      for (auto it = fonts_.begin(); it != fonts_.end();)
      {
        LOGFONTW actual = {};
        if (::GetObjectW(it->handle, sizeof(actual), &actual) != sizeof(actual) ||
            !eq(actual, it->actual))
          it = fonts_.erase(it);
        else
          ++it;
      }
    }

  public:
    static bool eq(const LOGFONTW &x, const LOGFONTW &y);
    LOGFONTW baseline(HFONT handle, const LOGFONTW &actual);
    HFONT getOrCreate(const LOGFONTW &requested, const LOGFONTW &baseline);
    void remember(HFONT handle, const LOGFONTW &requested, const LOGFONTW &baseline);
  };

  bool LogFontManager::eq(const LOGFONTW &x, const LOGFONTW &y)
  { // I assume there is no padding
    return ::wcscmp(x.lfFaceName, y.lfFaceName) == 0 && ::memcmp(&x, &y, sizeof(x) - sizeof(x.lfFaceName)) == 0;
  }

  LOGFONTW LogFontManager::baseline(HFONT handle, const LOGFONTW &actual)
  {
    std::lock_guard<std::mutex> lock(mutex_);
    for (auto it = fonts_.begin(); it != fonts_.end(); ++it)
      if (it->handle == handle)
      {
        // Games may delete our replacement font when resetting their renderer.
        // Do not treat a stale/reused GDI handle as a replacement font.
        if (eq(it->actual, actual))
          return it->baseline;
        fonts_.erase(it);
        break;
      }
    return actual;
  }

  HFONT LogFontManager::getOrCreate(const LOGFONTW &requested, const LOGFONTW &baseline)
  {
    std::lock_guard<std::mutex> lock(mutex_);
    discardStale();
    for (const auto &entry : fonts_)
    {
      // Baseline is part of the key: distinct original fonts can round to
      // the same scaled height, but must not share a different baseline.
      if (eq(entry.requested, requested) && eq(entry.baseline, baseline))
        return entry.handle;
    }
    HFONT handle = Hijack::oldCreateFontIndirectW(&requested);
    LOGFONTW actual = {};
    if (handle && ::GetObjectW(handle, sizeof(actual), &actual) == sizeof(actual))
      fonts_.push_back({handle, actual, requested, baseline});
    return handle;
  }

  void LogFontManager::remember(HFONT handle, const LOGFONTW &requested, const LOGFONTW &baseline)
  {
    LOGFONTW actual = {};
    if (!handle || ::GetObjectW(handle, sizeof(actual), &actual) != sizeof(actual))
      return;
    std::lock_guard<std::mutex> lock(mutex_);
    discardStale();
    fonts_.remove_if([handle](const FontEntry &entry) { return entry.handle == handle; });
    fonts_.push_back({handle, actual, requested, baseline});
  }

  LogFontManager fontManager;

  // GDI font switcher

  class DCFontSwitcher
  {
    HDC hdc_;

  public:
    explicit DCFontSwitcher(HDC hdc); // pass 0 to disable this class
    ~DCFontSwitcher();
  };

  DCFontSwitcher::~DCFontSwitcher()
  {
    // Keep the upstream persistent selection for Mogeko Castle compatibility.
    // Baseline tracking makes repeated measure/draw calls idempotent without
    // restoring a potentially deleted font or deleting a font still in use.
  }
  bool isFontCustomized()
  {
    return commonsharedmem->fontCharSetEnabled || wcslen(maybe_disabled_fontFamily());
  }
  bool isFontSizeCustomized()
  {
    return commonsharedmem->FontSizeRelative != 1;
  }
  DCFontSwitcher::DCFontSwitcher(HDC hdc)
      : hdc_(hdc)
  {
    if (!hdc_)
      return;
    /*
  auto p = HijackHelper::instance();
  if (!p)
    return;
  auto s = p->settings();
  if (!s->deviceContextFontEnabled || !s->isFontCustomized())
    return;
*/
    auto currentFont = (HFONT)::GetCurrentObject(hdc_, OBJ_FONT);
    LOGFONTW current = {};
    if (!currentFont || ::GetObjectW(currentFont, sizeof(current), &current) != sizeof(current))
      return;

    // The selected font may be the result of the previous measure/draw call.
    // Scale its original LOGFONT, never the already scaled TEXTMETRIC height.
    auto baseline = fontManager.baseline(currentFont, current);
    LOGFONTW lf = baseline;
    customizeLogFontW(&lf);
    ScaleFont(&lf);
    if (LogFontManager::eq(lf, current))
      return;
    auto newFont = fontManager.getOrCreate(lf, baseline);
    if (newFont)
      ::SelectObject(hdc_, newFont);
  }

} // unnamed namespace

/** Fonts */

// http://forums.codeguru.com/showthread.php?500522-Need-clarification-about-CreateFontIndirect
// The font creation functions will never fail
HFONT WINAPI Hijack::newCreateFontIndirectA(const LOGFONTA *lplf)
{
  if (lplf && (isFontCustomized() || isFontSizeCustomized()))
  {
    LOGFONTW lf = {};
    memcpy(&lf, lplf, offsetof(LOGFONTA, lfFaceName));
    const auto length = strnlen(lplf->lfFaceName, LF_FACESIZE);
    oldMultiByteToWideChar(CP_ACP, 0, lplf->lfFaceName, (int)length,
                          lf.lfFaceName, LF_FACESIZE - 1);
    return newCreateFontIndirectW(&lf);
  }
  return oldCreateFontIndirectA(lplf);
}

HFONT WINAPI Hijack::newCreateFontIndirectW(const LOGFONTW *lplf)
{

  // DOUT("width:" << lplf->lfWidth << ", height:" << lplf->lfHeight << ", weight:" << lplf->lfWeight);
  // if (auto p = HijackHelper::instance()) {
  // auto s = p->settings();
  if (lplf && (isFontCustomized() || isFontSizeCustomized()))
  {
    LOGFONTW lf(*lplf);
    ScaleFont(&lf);
    if (isFontCustomized())
      customizeLogFontW(&lf);
    auto handle = oldCreateFontIndirectW(&lf);
    // A creation hook can already apply the ratio. Drawing hooks must still
    // recover the unmodified LOGFONT rather than applying the ratio twice.
    fontManager.remember(handle, lf, *lplf);
    return handle;
  }
  // }
  return oldCreateFontIndirectW(lplf);
}

#define CREATE_FONT_ARGS nHeight, nWidth, nEscapement, nOrientation, fnWeight, fdwItalic, fdwUnderline, fdwStrikeOut, fdwCharSet, fdwOutputPrecision, fdwClipPrecision, fdwQuality, fdwPitchAndFamily, lpszFace
HFONT WINAPI Hijack::newCreateFontA(int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight, DWORD fdwItalic, DWORD fdwUnderline, DWORD fdwStrikeOut, DWORD fdwCharSet, DWORD fdwOutputPrecision, DWORD fdwClipPrecision, DWORD fdwQuality, DWORD fdwPitchAndFamily, LPCSTR lpszFace)
{
  if (isFontCustomized() || isFontSizeCustomized())
  {
    LOGFONTW lf = {nHeight, nWidth, nEscapement, nOrientation, fnWeight,
                  (BYTE)fdwItalic, (BYTE)fdwUnderline, (BYTE)fdwStrikeOut,
                  (BYTE)fdwCharSet, (BYTE)fdwOutputPrecision, (BYTE)fdwClipPrecision,
                  (BYTE)fdwQuality, (BYTE)fdwPitchAndFamily, {}};
    if (lpszFace)
      oldMultiByteToWideChar(CP_ACP, 0, lpszFace, -1, lf.lfFaceName, LF_FACESIZE);
    return newCreateFontIndirectW(&lf);
  }
  return oldCreateFontA(CREATE_FONT_ARGS);
}

HFONT WINAPI Hijack::newCreateFontW(int nHeight, int nWidth, int nEscapement, int nOrientation, int fnWeight, DWORD fdwItalic, DWORD fdwUnderline, DWORD fdwStrikeOut, DWORD fdwCharSet, DWORD fdwOutputPrecision, DWORD fdwClipPrecision, DWORD fdwQuality, DWORD fdwPitchAndFamily, LPCWSTR lpszFace)
{
  if (isFontCustomized() || isFontSizeCustomized())
  {
    LOGFONTW lf = {nHeight, nWidth, nEscapement, nOrientation, fnWeight,
                  (BYTE)fdwItalic, (BYTE)fdwUnderline, (BYTE)fdwStrikeOut,
                  (BYTE)fdwCharSet, (BYTE)fdwOutputPrecision, (BYTE)fdwClipPrecision,
                  (BYTE)fdwQuality, (BYTE)fdwPitchAndFamily, {}};
    if (lpszFace)
      wcsncpy_s(lf.lfFaceName, LF_FACESIZE, lpszFace, _TRUNCATE);
    return newCreateFontIndirectW(&lf);
  }
  return oldCreateFontW(CREATE_FONT_ARGS);
}
#undef CREATE_FONT_ARGS

/** Encoding */

LPSTR WINAPI Hijack::newCharNextA(LPCSTR lpString)
{

  // if (::GetACP() == 932)
  return const_cast<char *>(dynsjis::nextchar(lpString));
  // return oldCharNextA(lpString);
}

LPSTR WINAPI Hijack::newCharPrevA(LPCSTR lpStart, LPCSTR lpCurrent)
{

  // if (::GetACP() == 932)
  return const_cast<char *>(dynsjis::prevchar(lpCurrent, lpStart));
  // return oldCharNextA(lpStart, lpCurrent);
}
extern DynamicShiftJISCodec *dynamiccodec;
int WINAPI Hijack::newMultiByteToWideChar(UINT CodePage, DWORD dwFlags, LPCSTR lpMultiByteStr, int cbMultiByte, LPWSTR lpWideCharStr, int cchWideChar)
{
  //
  /* if (auto p = HijackHelper::instance())
     if (p->settings()->localeEmulationEnabled)
       if (CodePage == CP_THREAD_ACP || CodePage == CP_OEMCP)
         CodePage = CP_ACP;
         */
  if (CodePage == CP_THREAD_ACP || CodePage == CP_OEMCP)
    CodePage = CP_ACP;
  // CP_ACP(0), CP_MACCP(1), CP_OEMCP(2), CP_THREAD_ACP(3)
  if ((CodePage <= 3 || CodePage == 932) && cchWideChar > 0 && cbMultiByte > 1)
  {
    bool dynamic;
    std::string data(lpMultiByteStr, cbMultiByte);
    auto text = dynamiccodec->decode(data, &dynamic);
    if (dynamic && !text.empty())
    {
      int size = min(text.size() + 1, cchWideChar);
      ::memcpy(lpWideCharStr, text.c_str(), size * 2);
      // lpWideCharStr[size - 1] = 0; // enforce trailing zero
      return size - 1;
    }
  }
  return oldMultiByteToWideChar(CodePage, dwFlags, lpMultiByteStr, cbMultiByte, lpWideCharStr, cchWideChar);
}

int WINAPI Hijack::newWideCharToMultiByte(UINT CodePage, DWORD dwFlags, LPCWSTR lpWideCharStr, int cchWideChar, LPSTR lpMultiByteStr, int cbMultiByte, LPCSTR lpDefaultChar, LPBOOL lpUsedDefaultChar)
{
  //
  if (CodePage == CP_THREAD_ACP || CodePage == CP_OEMCP)
    CodePage = CP_ACP;

  if ((CodePage <= 3 || CodePage == 932) && cchWideChar > 0 && cbMultiByte >= 0)
  {
    bool dynamic;
    auto text = std::wstring(lpWideCharStr, cchWideChar);
    auto data = dynamiccodec->encodeSTD(text, &dynamic);
    if (dynamic && !data.empty())
    {

      int size = data.size() + 1;
      if (cbMultiByte && cbMultiByte < size)
        size = cbMultiByte;
      ::memcpy(lpMultiByteStr, data.c_str(), size);
      // lpMultiByteStr[size - 1] = 0; // enforce trailing zero
      return size - 1;
    }
  }
  return oldWideCharToMultiByte(CodePage, dwFlags, lpWideCharStr, cchWideChar, lpMultiByteStr, cbMultiByte, lpDefaultChar, lpUsedDefaultChar);
}

/** Text */
UINT decodeChar(UINT ch, bool *dynamic)
{
  if (dynamic)
    *dynamic = false;
  if (ch > 0xff)
  {
    bool t;
    char data[3] = {(char)((BYTE)(ch >> 8) & 0xff), char((BYTE)ch & 0xff), 0};
    auto text = dynamiccodec->decode(data, &t);
    if (t && text.size() == 1)
    {
      if (dynamic)
        *dynamic = true;
      return text[0];
    }
  }
  return ch;
}
#define DECODE_CHAR(uChar, ...)                \
  {                                            \
    if (uChar > 0xff)                          \
      if (1)                                   \
      {                                        \
        bool dynamic;                          \
        UINT ch = decodeChar(uChar, &dynamic); \
        if (dynamic && ch)                     \
        {                                      \
          uChar = ch;                          \
          return (__VA_ARGS__);                \
        }                                      \
      }                                        \
  }

#define DECODE_TEXT(lpString, cchString, ...)                                                \
  {                                                                                          \
    if (cchString == -1 || cchString > 1)                                                    \
      if (1)                                                                                 \
      {                                                                                      \
        bool dynamic;                                                                        \
        auto data = std::string(lpString, cchString == -1 ? ::strlen(lpString) : cchString); \
        if (data.size() > 1)                                                                 \
        {                                                                                    \
          auto text = dynamiccodec->decode(data, &dynamic);                                  \
          if (dynamic && !text.empty())                                                      \
          {                                                                                  \
            LPCWSTR lpString = (LPCWSTR)text.c_str();                                        \
            cchString = text.size();                                                         \
            return (__VA_ARGS__);                                                            \
          }                                                                                  \
        }                                                                                    \
      }                                                                                      \
  }
#define TRANSLATE_TEXT_A(lpString, cchString, ...)                                         \
  {                                                                                        \
    if (auto q = EngineController::instance())                                             \
    {                                                                                      \
      auto data = std::string(lpString, cchString == -1 ? ::strlen(lpString) : cchString); \
      std::wstring oldText = q->decode(data);                                              \
      if (!oldText.empty())                                                                \
      {                                                                                    \
        enum                                                                               \
        {                                                                                  \
          role = Engine::OtherRole                                                         \
        };                                                                                 \
        ULONG split = (ULONG)_ReturnAddress();                                             \
        auto sig = Engine::hashThreadSignature(role, split);                               \
        auto newText = q->dispatchTextWSTD(oldText, role, sig);                            \
        if (newText != oldText)                                                            \
        {                                                                                  \
          LPCWSTR lpString = (LPCWSTR)newText.c_str();                                     \
          cchString = newText.size();                                                      \
          return (__VA_ARGS__);                                                            \
        }                                                                                  \
      }                                                                                    \
    }                                                                                      \
  }

#define TRANSLATE_TEXT_W(lpString, cchString, ...)           \
  {                                                          \
    if (auto q = EngineController::instance())               \
    {                                                        \
      auto text = std::wstring(lpString, cchString);         \
      if (!text.empty())                                     \
      {                                                      \
        enum                                                 \
        {                                                    \
          role = Engine::OtherRole                           \
        };                                                   \
        ULONG split = (ULONG)_ReturnAddress();               \
        auto sig = Engine::hashThreadSignature(role, split); \
        text = q->dispatchTextWSTD(text, role, sig);         \
        LPCWSTR lpString = (LPCWSTR)text.c_str();            \
        cchString = text.size();                             \
        return (__VA_ARGS__);                                \
      }                                                      \
    }                                                        \
  }

DWORD WINAPI Hijack::newGetGlyphOutlineA(HDC hdc, UINT uChar, UINT uFormat, LPGLYPHMETRICS lpgm, DWORD cbBuffer, LPVOID lpvBuffer, const MAT2 *lpmat2)
{
  DCFontSwitcher fs(hdc);

  DECODE_CHAR(uChar, oldGetGlyphOutlineW(hdc, ch, uFormat, lpgm, cbBuffer, lpvBuffer, lpmat2))
  return oldGetGlyphOutlineA(hdc, uChar, uFormat, lpgm, cbBuffer, lpvBuffer, lpmat2);
}

DWORD WINAPI Hijack::newGetGlyphOutlineW(HDC hdc, UINT uChar, UINT uFormat, LPGLYPHMETRICS lpgm, DWORD cbBuffer, LPVOID lpvBuffer, const MAT2 *lpmat2)
{

  DCFontSwitcher fs(hdc);
  return oldGetGlyphOutlineW(hdc, uChar, uFormat, lpgm, cbBuffer, lpvBuffer, lpmat2);
}

BOOL WINAPI Hijack::newGetTextExtentPoint32A(HDC hdc, LPCSTR lpString, int cchString, LPSIZE lpSize)
{

  DCFontSwitcher fs(hdc);
  // TRANSLATE_TEXT_A(lpString, cchString, oldGetTextExtentPoint32W(hdc, lpString, cchString, lpSize))
  DECODE_TEXT(lpString, cchString, oldGetTextExtentPoint32W(hdc, lpString, cchString, lpSize))
  return oldGetTextExtentPoint32A(hdc, lpString, cchString, lpSize);
}

BOOL WINAPI Hijack::newGetTextExtentPoint32W(HDC hdc, LPCWSTR lpString, int cchString, LPSIZE lpSize)
{

  DCFontSwitcher fs(hdc);
  // TRANSLATE_TEXT_W(lpString, cchString, oldGetTextExtentPoint32W(hdc, lpString, cchString, lpSize))
  return oldGetTextExtentPoint32W(hdc, lpString, cchString, lpSize);
}

BOOL WINAPI Hijack::newGetTextExtentExPointA(HDC hdc, LPCSTR lpString, int cchString, int nMaxExtent, LPINT lpnFit, LPINT alpDx, LPSIZE lpSize)
{

  // DCFontSwitcher fs(hdc);
  // TRANSLATE_TEXT_A(lpString, cchString, oldGetTextExtentExPointW(hdc, lpString, cchString, nMaxExtent, lpnFit, alpDx, lpSize))
  DECODE_TEXT(lpString, cchString, oldGetTextExtentExPointW(hdc, lpString, cchString, nMaxExtent, lpnFit, alpDx, lpSize))
  return oldGetTextExtentExPointA(hdc, lpString, cchString, nMaxExtent, lpnFit, alpDx, lpSize);
}

BOOL WINAPI Hijack::newGetTextExtentExPointW(HDC hdc, LPCWSTR lpString, int cchString, int nMaxExtent, LPINT lpnFit, LPINT alpDx, LPSIZE lpSize)
{

  DCFontSwitcher fs(hdc);
  // TRANSLATE_TEXT_W(lpString, cchString, oldGetTextExtentExPointW(hdc, lpString, cchString, nMaxExtent, lpnFit, alpDx, lpSize))
  return oldGetTextExtentExPointW(hdc, lpString, cchString, nMaxExtent, lpnFit, alpDx, lpSize);
}

int WINAPI Hijack::newDrawTextA(HDC hdc, LPCSTR lpString, int cchString, LPRECT lpRect, UINT uFormat)
{

  DCFontSwitcher fs(hdc);
  // if (HijackManager::instance()->isFunctionTranslated((uintptr_t)::DrawTextA))
  //   TRANSLATE_TEXT_A(lpString, cchString, oldDrawTextW(hdc, lpString, cchString, lpRect, uFormat))
  // else
  DECODE_TEXT(lpString, cchString, oldDrawTextW(hdc, lpString, cchString, lpRect, uFormat))
  return oldDrawTextA(hdc, lpString, cchString, lpRect, uFormat);
}

int WINAPI Hijack::newDrawTextW(HDC hdc, LPCWSTR lpString, int cchString, LPRECT lpRect, UINT uFormat)
{

  DCFontSwitcher fs(hdc);
  // if (HijackManager::instance()->isFunctionTranslated((ULONG)::DrawTextW))
  //   TRANSLATE_TEXT_W(lpString, cchString, oldDrawTextW(hdc, lpString, cchString, lpRect, uFormat))
  return oldDrawTextW(hdc, lpString, cchString, lpRect, uFormat);
}

int WINAPI Hijack::newDrawTextExA(HDC hdc, LPSTR lpString, int cchString, LPRECT lpRect, UINT dwDTFormat, LPDRAWTEXTPARAMS lpDTParams)
{

  DCFontSwitcher fs(hdc);
  if (!(dwDTFormat & DT_MODIFYSTRING))
  {
    // if (HijackManager::instance()->isFunctionTranslated((uintptr_t)::DrawTextExA))
    //   TRANSLATE_TEXT_A(lpString, cchString, oldDrawTextExW(hdc, const_cast<LPWSTR>(lpString), cchString, lpRect, dwDTFormat, lpDTParams))
    // else
    DECODE_TEXT(lpString, cchString, oldDrawTextExW(hdc, const_cast<LPWSTR>(lpString), cchString, lpRect, dwDTFormat, lpDTParams))
  }
  return oldDrawTextExA(hdc, lpString, cchString, lpRect, dwDTFormat, lpDTParams);
}

int WINAPI Hijack::newDrawTextExW(HDC hdc, LPWSTR lpString, int cchString, LPRECT lpRect, UINT dwDTFormat, LPDRAWTEXTPARAMS lpDTParams)
{

  DCFontSwitcher fs(hdc);
  // if (!(dwDTFormat & DT_MODIFYSTRING) && HijackManager::instance()->isFunctionTranslated((ULONG)::DrawTextExW))
  //   TRANSLATE_TEXT_W(lpString, cchString, oldDrawTextExW(hdc, const_cast<LPWSTR>(lpString), cchString, lpRect, dwDTFormat, lpDTParams))
  return oldDrawTextExW(hdc, lpString, cchString, lpRect, dwDTFormat, lpDTParams);
}

BOOL WINAPI Hijack::newTextOutA(HDC hdc, int nXStart, int nYStart, LPCSTR lpString, int cchString)
{

  DCFontSwitcher fs(hdc);
  // if (HijackManager::instance()->isFunctionTranslated((uintptr_t)::TextOutA))
  //   TRANSLATE_TEXT_A(lpString, cchString, oldTextOutW(hdc, nXStart, nYStart, lpString, cchString))
  // else
  DECODE_TEXT(lpString, cchString, oldTextOutW(hdc, nXStart, nYStart, lpString, cchString))
  return oldTextOutA(hdc, nXStart, nYStart, lpString, cchString);
}

BOOL WINAPI Hijack::newTextOutW(HDC hdc, int nXStart, int nYStart, LPCWSTR lpString, int cchString)
{

  DCFontSwitcher fs(hdc);
  // if (HijackManager::instance()->isFunctionTranslated((ULONG)::TextOutW))
  //   TRANSLATE_TEXT_W(lpString, cchString, oldTextOutW(hdc, nXStart, nYStart, lpString, cchString))
  return oldTextOutW(hdc, nXStart, nYStart, lpString, cchString);
}

BOOL WINAPI Hijack::newExtTextOutA(HDC hdc, int X, int Y, UINT fuOptions, const RECT *lprc, LPCSTR lpString, UINT cchString, const INT *lpDx)
{

  DCFontSwitcher fs(hdc);
  // if (HijackManager::instance()->isFunctionTranslated((uintptr_t)::ExtTextOutA))
  //   TRANSLATE_TEXT_A(lpString, cchString, oldExtTextOutW(hdc, X, Y, fuOptions, lprc, lpString, cchString, lpDx))
  // else
  DECODE_TEXT(lpString, cchString, oldExtTextOutW(hdc, X, Y, fuOptions, lprc, lpString, cchString, lpDx))
  return oldExtTextOutA(hdc, X, Y, fuOptions, lprc, lpString, cchString, lpDx);
}

BOOL WINAPI Hijack::newExtTextOutW(HDC hdc, int X, int Y, UINT fuOptions, const RECT *lprc, LPCWSTR lpString, UINT cchString, const INT *lpDx)
{

  DCFontSwitcher fs(hdc);
  // if (HijackManager::instance()->isFunctionTranslated((ULONG)::ExtTextOutW))
  //   TRANSLATE_TEXT_W(lpString, cchString, oldExtTextOutW(hdc, X, Y, fuOptions, lprc, lpString, cchString, lpDx))
  return oldExtTextOutW(hdc, X, Y, fuOptions, lprc, lpString, cchString, lpDx);
}

// EOF
