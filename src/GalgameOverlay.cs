using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace GameTranslateToolkit;

internal sealed class GalgameOverlay : Form
{
    private const int Hotkey = 7713, ExtendedStyle = -20, Layered = 0x80000, Transparent = 0x20;
    private readonly ContextMenuStrip _menu = new();
    private readonly Action<string>? _onRenderError;
    private readonly Font _hintFont = new("Microsoft YaHei UI", 10);
    private Font? _sourceFont, _translationFont;
    private string _original = "", _translated = "", _status = "等待游戏文字…";
    private string _fontFamily = "", _hotkeyText = "Ctrl+Alt+T";
    private int _fontSize;
    private Color _fontColor = Color.White;
    private bool _showOriginal = true, _clickThrough, _hotkeyRegistered, _ready, _rendering;
    private bool _dragging, _resizing;
    private nint _hotkeyHandle;
    private Point _mouseStart, _windowStart;
    private Size _sizeStart;
    private Rectangle _toggleArea, _resizeArea;
    private RectangleF _sourceArea, _translationArea;
    private float _sourceScroll, _translationScroll, _sourceMaximum, _translationMaximum;
    internal string? RenderError { get; private set; }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; value.ExStyle |= Layered | 0x08000000 | 0x80; if (_clickThrough) value.ExStyle |= Transparent; return value; }
    }

    public GalgameOverlay(string gameName, Action<string>? onRenderError = null)
    {
        _onRenderError = onRenderError;
        // Keep a name for window management, without drawing a caption or status strip.
        Text = gameName + " · 梨花翻译浮窗";
        FormBorderStyle = FormBorderStyle.None;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Size = new Size(780, 260); MinimumSize = new Size(320, 140);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(area.Left + Math.Max(0, (area.Width - Width) / 2), Math.Max(area.Top, area.Bottom - Height - 35));
        var copyTranslation = _menu.Items.Add("复制译文", null, (_, _) => Copy(_translated));
        var copySource = _menu.Items.Add("复制原文", null, (_, _) => Copy(_original));
        _menu.Items.Add(new ToolStripSeparator());
        var toggle = (ToolStripMenuItem)_menu.Items.Add("鼠标穿透", null, (_, _) => SetClickThrough(!_clickThrough));
        _menu.Items.Add("隐藏浮窗", null, (_, _) => Hide());
        _menu.Opening += (_, _) => { copyTranslation.Enabled = _translated.Length > 0; copySource.Enabled = _original.Length > 0; toggle.Checked = _clickThrough; toggle.Enabled = _hotkeyRegistered; toggle.Text = $"鼠标穿透（{_hotkeyText}）"; };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
        ApplySettings(new GalgameSettings());
        _ready = true;
    }

    public void ApplySettings(GalgameSettings settings)
    {
        if (_fontFamily != settings.FontFamily || _fontSize != settings.FontSize)
        {
            var source = new Font(settings.FontFamily, Math.Max(10, settings.FontSize - 3));
            Font translation;
            try { translation = new Font(settings.FontFamily, settings.FontSize); } catch { source.Dispose(); throw; }
            var oldSource = _sourceFont; var oldTranslation = _translationFont;
            _sourceFont = source; _translationFont = translation;
            _fontFamily = settings.FontFamily; _fontSize = settings.FontSize;
            oldSource?.Dispose(); oldTranslation?.Dispose();
        }
        _fontColor = ColorTranslator.FromHtml(settings.FontColor);
        _showOriginal = settings.ShowOriginal;
        Render();
    }

    public void UpdateText(string original, string translated, string status, GalgameSettings settings)
    {
        if (_original != original) _sourceScroll = 0;
        if (_translated != translated) _translationScroll = 0;
        _original = original; _translated = translated; _status = status;
        ApplySettings(settings);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _hotkeyHandle = Handle;
        _hotkeyText = "Ctrl+Alt+T";
        _hotkeyRegistered = RegisterHotKey(_hotkeyHandle, Hotkey, 0x0001 | 0x0002 | 0x4000, 0x54);
        if (!_hotkeyRegistered) { _hotkeyText = "Ctrl+Alt+F8"; _hotkeyRegistered = RegisterHotKey(_hotkeyHandle, Hotkey, 0x0001 | 0x0002 | 0x4000, 0x77); }
        if (!_hotkeyRegistered) SetClickThrough(false);
        Render();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_hotkeyRegistered) UnregisterHotKey(_hotkeyHandle, Hotkey);
        _hotkeyHandle = 0; _hotkeyRegistered = false;
        base.OnHandleDestroyed(e);
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Render(); }
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Render(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Render(); }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    private void SetClickThrough(bool enabled)
    {
        if (enabled && !_hotkeyRegistered) return;
        _clickThrough = enabled;
        if (IsHandleCreated)
        {
            var style = GetWindowLong(Handle, ExtendedStyle);
            SetWindowLong(Handle, ExtendedStyle, enabled ? style | Transparent : style & ~Transparent);
        }
        Render();
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam == Hotkey) { SetClickThrough(!_clickThrough); return; }
        if (m.Msg == 0x0021) { m.Result = 3; return; } // Don't steal the game's keyboard focus.
        base.WndProc(ref m);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _clickThrough) return;
        if (_toggleArea.Contains(e.Location)) { SetClickThrough(!_clickThrough); return; }
        _resizing = _resizeArea.Contains(e.Location); _dragging = !_resizing;
        _mouseStart = MousePosition; _windowStart = Location; _sizeStart = Size;
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging && !_resizing) { Cursor = _resizeArea.Contains(e.Location) ? Cursors.SizeNWSE : Cursors.SizeAll; return; }
        var now = MousePosition; var dx = now.X - _mouseStart.X; var dy = now.Y - _mouseStart.Y;
        if (_resizing) Size = new Size(Math.Clamp(_sizeStart.Width + dx, MinimumSize.Width, 7680), Math.Clamp(_sizeStart.Height + dy, MinimumSize.Height, 4320));
        else Location = new Point(_windowStart.X + dx, _windowStart.Y + dy);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragging = _resizing = false; Capture = false;
        if (e.Button == MouseButtons.Right && !_clickThrough) _menu.Show(this, e.Location);
    }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) _dragging = _resizing = false; }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_sourceArea.Contains(e.Location)) _sourceScroll = Math.Clamp(_sourceScroll - e.Delta / 120f * 40 * DeviceDpi / 96f, 0, _sourceMaximum);
        else _translationScroll = Math.Clamp(_translationScroll - e.Delta / 120f * 40 * DeviceDpi / 96f, 0, _translationMaximum);
        Render();
    }

    internal Bitmap DrawSurface()
    {
        var bitmap = new Bitmap(Math.Max(1, Width), Math.Max(1, Height), PixelFormat.Format32bppPArgb);
        bitmap.SetResolution(DeviceDpi, DeviceDpi);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var scale = DeviceDpi / 96f; var margin = 14 * scale; var footer = 32 * scale;
            var width = Math.Max(1, Width - 2 * margin); var height = Math.Max(1, Height - footer - 2 * margin);
            var hasSource = _showOriginal && _original.Length > 0;
            var sourceHeight = hasSource ? Math.Max(1, height / 3 - 6 * scale) : 0;
            _sourceArea = hasSource ? new RectangleF(margin, margin, width, sourceHeight) : RectangleF.Empty;
            var resultTop = hasSource ? margin + height / 3 + 6 * scale : margin;
            _translationArea = new RectangleF(margin, resultTop, width, Math.Max(1, Height - footer - margin - resultTop));
            if (hasSource) DrawPanel(graphics, _original, _sourceFont!, _sourceArea, ref _sourceScroll, out _sourceMaximum, scale);
            else { _sourceMaximum = 0; _sourceScroll = 0; }
            var result = _translated.Length > 0 ? _translated : (_status.Length > 0 ? _status : "等待游戏文字…");
            DrawPanel(graphics, result, _translationFont!, _translationArea, ref _translationScroll, out _translationMaximum, scale);
            var hint = _hotkeyRegistered ? $"鼠标穿透（{_hotkeyText} 切换）" : "快捷键不可用，暂不可启用鼠标穿透";
            var hintY = Height - footer + 4 * scale;
            var check = new RectangleF(margin, hintY + 3 * scale, 12 * scale, 12 * scale);
            using var checkHit = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
            graphics.FillRectangle(checkHit, check.X - 3 * scale, check.Y - 3 * scale, check.Width + 6 * scale, check.Height + 6 * scale);
            using var pen = new Pen(Color.FromArgb(220, Color.White), 1.5f * scale);
            graphics.DrawRectangle(pen, check.X, check.Y, check.Width, check.Height);
            if (_clickThrough) graphics.DrawLines(pen, [new PointF(check.Left + 2 * scale, check.Top + 6 * scale), new PointF(check.Left + 5 * scale, check.Top + 9 * scale), new PointF(check.Right - 2 * scale, check.Top + 3 * scale)]);
            using var hintInk = new SolidBrush(Color.White);
            using var shadow = new SolidBrush(Color.FromArgb(210, Color.Black));
            var hintX = margin + 20 * scale;
            graphics.DrawString(hint, _hintFont, shadow, hintX + scale, hintY + scale);
            graphics.DrawString(hint, _hintFont, hintInk, hintX, hintY);
            _toggleArea = Rectangle.Ceiling(new RectangleF(margin - 3 * scale, hintY, Math.Min(width, 24 * scale + graphics.MeasureString(hint, _hintFont).Width), 24 * scale));
            _resizeArea = new Rectangle(Math.Max(0, Width - (int)(25 * scale)), Math.Max(0, Height - (int)(25 * scale)), (int)(25 * scale), (int)(25 * scale));
            // Only the resize grip needs a barely visible hit surface; all text gaps remain alpha zero.
            using var gripHit = new SolidBrush(Color.FromArgb(1, 0, 0, 0));
            graphics.FillRectangle(gripHit, _resizeArea);
            using var gripPen = new Pen(Color.FromArgb(180, Color.White), scale);
            for (var line = 1; line <= 3; line++) graphics.DrawLine(gripPen, Width - 5 * scale - line * 4 * scale, Height - 5 * scale, Width - 5 * scale, Height - 5 * scale - line * 4 * scale);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    private void DrawPanel(Graphics graphics, string text, Font font, RectangleF area, ref float offset, out float maximum, float scale)
    {
        using var format = new StringFormat { Trimming = StringTrimming.None, FormatFlags = StringFormatFlags.LineLimit };
        var measured = graphics.MeasureString(text, font, new SizeF(area.Width, 100000), format);
        maximum = Math.Max(0, measured.Height + 4 * scale - area.Height);
        offset = Math.Clamp(offset, 0, maximum);
        var state = graphics.Save(); graphics.SetClip(area);
        try
        {
            var bounds = new RectangleF(area.X, area.Y - offset, area.Width, Math.Max(area.Height, measured.Height + 4 * scale));
            using var shadow = new SolidBrush(Color.FromArgb(220, Color.Black));
            using var ink = new SolidBrush(_fontColor);
            var shadowBounds = bounds; shadowBounds.Offset(scale, 2 * scale);
            graphics.DrawString(text, font, shadow, shadowBounds, format);
            graphics.DrawString(text, font, ink, bounds, format);
        }
        finally { graphics.Restore(state); }
    }

    private void Render()
    {
        if (!_ready || _rendering || !IsHandleCreated || IsDisposed || _translationFont == null) return;
        _rendering = true;
        try { using var bitmap = DrawSurface(); Publish(bitmap); RenderError = null; }
        catch (Exception ex) when (ex is Win32Exception or ExternalException or ArgumentException)
        {
            if (RenderError != ex.Message) _onRenderError?.Invoke(ex.Message);
            RenderError = ex.Message;
        }
        finally { _rendering = false; }
    }

    private void Publish(Bitmap bitmap)
    {
        var screen = GetDC(0);
        if (screen == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        nint memory = 0, dib = 0, previous = 0;
        try
        {
            memory = CreateCompatibleDC(screen);
            if (memory == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var info = new BitmapInfo { Header = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, BitCount = 32 } };
            dib = CreateDIBSection(screen, ref info, 0, out var pixels, 0, 0);
            if (dib == 0 || pixels == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new byte[bitmap.Width * 4];
                for (var y = 0; y < bitmap.Height; y++) { Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length); Marshal.Copy(row, 0, pixels + y * row.Length, row.Length); }
            }
            finally { bitmap.UnlockBits(data); }
            previous = SelectObject(memory, dib);
            var position = new NativePoint { X = Left, Y = Top }; var size = new NativeSize { Width = bitmap.Width, Height = bitmap.Height }; var source = new NativePoint();
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            if (!UpdateLayeredWindow(Handle, screen, ref position, ref size, memory, ref source, 0, ref blend, 2)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (dib != 0) DeleteObject(dib);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
    private static void Copy(string text) { if (text.Length > 0) try { Clipboard.SetText(text); } catch (ExternalException) { } }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) { _sourceFont?.Dispose(); _sourceFont = null; _translationFont?.Dispose(); _translationFont = null; _hintFont.Dispose(); _menu.Dispose(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeSize { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(nint hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint hwnd, nint dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(nint hwnd, nint destinationDc, ref NativePoint position, ref NativeSize size, nint sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
}
