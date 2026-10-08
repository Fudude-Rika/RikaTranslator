// SPDX-License-Identifier: GPL-3.0-or-later
// An isolated stdio host for the LunaHook v12.0.1 C ABI, with the Rika font fix.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private static readonly object OutputGate = new();
    private static readonly StreamWriter Output = new(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
    private static readonly List<Delegate> Roots = [];
    private static readonly ConcurrentDictionary<string, ThreadParam> Threads = new();
    private static readonly ConcurrentDictionary<string, bool> Embeddable = new();
    private static readonly ConcurrentDictionary<string, (ThreadParam Tp, string Text)> Pending = new();
    private static nint Library;
    private static uint Pid;
    private static string Selected = "";
    private static bool EmbedEnabled;
    private static int WaitMs = 2000;
    private static UseEmbed? SetEmbed;
    private static EmbedReply? Reply;
    private static SettingsEx? ConfigureEmbed;
    private static string FontFamily = "Microsoft YaHei UI";
    private static int EmbedFontSizePercent = 100;

    [StructLayout(LayoutKind.Sequential)] internal struct ThreadParam
    {
        public uint ProcessId;
        public ulong Address, Context, Context2;
        public readonly string Key => $"{ProcessId}-{Address:X}-{Context:X}-{Context2:X}";
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProcessEvent(uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ThreadCreate(nint code, nint name, ThreadParam tp, [MarshalAs(UnmanagedType.I1)] bool embed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ThreadDestroy(nint code, nint name, ThreadParam tp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void TextOutput(nint code, nint name, ThreadParam tp, nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HostInfo(int type, nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void HookInsert(uint pid, ulong address, nint code);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EmbedRequest(nint text, ThreadParam tp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint I18nQuery(nint text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void EmuInfo(nint id, nint title, nint version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Start(ProcessEvent connect, ProcessEvent disconnect, ThreadCreate create, ThreadDestroy destroy, TextOutput output, HostInfo info, HookInsert insert, EmbedRequest embed, I18nQuery i18n, EmuInfo emu);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ProcessCommand(uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)] private delegate bool NeedInject(uint pid);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Settings(int delay, int codepage, int buffer, int history, [MarshalAs(UnmanagedType.I1)] bool pcHooks);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UseEmbed(ThreadParam tp, [MarshalAs(UnmanagedType.I1)] bool use);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate void EmbedReply(ThreadParam tp, string original, string translation);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate void InsertCode(uint pid, string code);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate nint AllocString(string text);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate void SettingsEx(uint pid, uint wait, byte charset, [MarshalAs(UnmanagedType.I1)] bool charsetEnabled, string font, uint mode, [MarshalAs(UnmanagedType.I1)] bool fastSkip, [MarshalAs(UnmanagedType.I1)] bool clear, [MarshalAs(UnmanagedType.I1)] bool resize, float size, [MarshalAs(UnmanagedType.I1)] bool veh, string unityFonts);
    private static T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library, name));
    private static string Wide(nint ptr) { var text = Marshal.PtrToStringUni(ptr) ?? ""; return text[..Math.Min(65536, text.Length)]; }
    private static string Name(nint ptr) => Marshal.PtrToStringUTF8(ptr) ?? "";
    private static void Emit(object message) { lock (OutputGate) { try { Output.WriteLine(JsonSerializer.Serialize(message)); } catch { } } }
    private static void Safe(Action action) { try { action(); } catch { Emit(new { @event = "error", message = "Hook 回调处理失败。" }); } }
    private static void Configure() => ConfigureEmbed!(Pid, (uint)WaitMs, 0, false, FontFamily, 0, true, false, EmbedFontSizePercent != 100, EmbedFontSizePercent / 100f, false, "");
    private static void CancelPending() { foreach (var key in Pending.Keys) if (Pending.TryRemove(key, out var pending)) Reply!(pending.Tp, pending.Text, pending.Text); }

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 5 || !uint.TryParse(args[1], out Pid) || Pid == 0) throw new ArgumentException("Hook 启动参数无效。");
            var nativeDir = Path.GetFullPath(args[0]);
            var expected = Path.GetFullPath(args[2]);
            using var target = Process.GetProcessById((int)Pid);
            if (!string.Equals(Path.GetFullPath(target.MainModule!.FileName), expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标进程路径已改变。");
            if (args[3] is not ("x86" or "x64")) throw new ArgumentException("仅支持 x86/x64 游戏。");
            Library = NativeLibrary.Load(Path.Combine(nativeDir, "LunaHost64.dll"));
            SetEmbed = Export<UseEmbed>("Luna_UseEmbed");
            Reply = Export<EmbedReply>("Luna_EmbedCallback");
            ConfigureEmbed = Export<SettingsEx>("Luna_SettingsEx");
            var alloc = Export<AllocString>("Luna_AllocString");
            ProcessEvent connect = pid => Safe(() => { Configure(); Emit(new { @event = "connected", pid }); });
            ProcessEvent disconnect = pid => Safe(() => Emit(new { @event = "disconnected", pid }));
            ThreadCreate create = (code, name, tp, embed) => Safe(() => {
                Threads[tp.Key] = tp; Embeddable[tp.Key] = embed;
                Emit(new { @event = "thread", id = tp.Key, hookCode = Wide(code), name = Name(name), address = tp.Address.ToString("X"), context = tp.Context.ToString("X"), context2 = tp.Context2.ToString("X"), embeddable = embed });
            });
            ThreadDestroy destroy = (_, _, tp) => Safe(() => { Threads.TryRemove(tp.Key, out _); Embeddable.TryRemove(tp.Key, out _); Emit(new { @event = "removed", id = tp.Key }); });
            TextOutput output = (code, name, tp, text) => Safe(() => Emit(new { @event = "text", id = tp.Key, hookCode = Wide(code), name = Name(name), text = Wide(text) }));
            HostInfo info = (type, text) => Safe(() => Emit(new { @event = "log", type, message = Wide(text) }));
            HookInsert insert = (pid, address, code) => Safe(() => Emit(new { @event = "inserted", pid, address = address.ToString("X"), hookCode = Wide(code) }));
            EmbedRequest embedRequest = (text, tp) => Safe(() => {
                var original = Wide(text);
                if (!EmbedEnabled || Selected != tp.Key || !Embeddable.GetValueOrDefault(tp.Key)) { Reply(tp, original, original); return; }
                var requestId = Guid.NewGuid().ToString("N");
                Pending[requestId] = (tp, original);
                Emit(new { @event = "embed", id = tp.Key, requestId, text = original });
                _ = Task.Run(async () => { await Task.Delay(WaitMs); if (Pending.TryRemove(requestId, out var pending)) Safe(() => { Reply(pending.Tp, pending.Text, pending.Text); Emit(new { @event = "embed-timeout", requestId }); }); });
            });
            I18nQuery i18n = _ => nint.Zero;
            EmuInfo emu = (_, _, _) => { };
            Roots.AddRange([connect, disconnect, create, destroy, output, info, insert, embedRequest, i18n, emu]);
            Export<Start>("Luna_Start")(connect, disconnect, create, destroy, output, info, insert, embedRequest, i18n, emu);
            Export<Settings>("Luna_Settings")(150, 932, 65536, 65536, args[4] == "true");
            Export<ProcessCommand>("Luna_ConnectProcess")(Pid);
            if (Export<NeedInject>("Luna_CheckIfNeedInject")(Pid))
            {
                var bits = args[3] == "x86" ? "32" : "64";
                var start = new ProcessStartInfo(Path.Combine(nativeDir, $"LunaSubprocess{bits}.exe")) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("dllinject"); start.ArgumentList.Add(Pid.ToString()); start.ArgumentList.Add(Path.Combine(nativeDir, $"LunaHook{bits}.dll"));
                using var injector = Process.Start(start) ?? throw new IOException("无法启动 Hook 注入组件。");
                await injector.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if (injector.ExitCode != 1) throw new IOException("Hook 注入未成功，请检查游戏进程权限和组件完整性。");
            }
            Emit(new { @event = "ready", pid = Pid, version = "12.0.1-rika-fontfix1" });
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
            while (await input.ReadLineAsync() is { } line)
            {
                if (line.Length > 150000) continue;
                try
                {
                    using var document = JsonDocument.Parse(line); var command = document.RootElement;
                    string Text(string key) => command.TryGetProperty(key, out var value) ? value.GetString() ?? "" : "";
                    switch (Text("command"))
                    {
                        case "select":
                            CancelPending();
                            if (Selected.Length > 0 && Threads.TryGetValue(Selected, out var old)) SetEmbed(old, false);
                            Selected = Text("id"); EmbedEnabled = false;
                            break;
                        case "configure":
                            CancelPending();
                            var codepage = command.GetProperty("codepage").GetInt32();
                            Export<Settings>("Luna_Settings")(150, codepage, 65536, 65536, command.GetProperty("systemHooks").GetBoolean());
                            WaitMs = Math.Clamp(command.GetProperty("waitMs").GetInt32(), 200, 5000);
                            FontFamily = Text("font"); if (FontFamily.Length > 90) FontFamily = "Microsoft YaHei UI";
                            EmbedFontSizePercent = command.TryGetProperty("embedFontSizePercent", out var scale) ? Math.Clamp(scale.GetInt32(), 50, 200) : 100;
                            Configure();
                            if (Threads.TryGetValue(Selected, out var selected)) {
                                EmbedEnabled = command.GetProperty("embed").GetBoolean() && Embeddable.GetValueOrDefault(Selected);
                                SetEmbed(selected, EmbedEnabled);
                            }
                            break;
                        case "insert":
                            var hookCode = Text("code");
                            if (hookCode.Length is > 0 and <= 1000) Export<InsertCode>("Luna_InsertHookCode")(Pid, hookCode);
                            break;
                        case "reply":
                            var replyId = Text("requestId");
                            if (Pending.TryRemove(replyId, out var pending)) { var translated = Text("text"); var fallback = command.TryGetProperty("fallback", out var f) && f.GetBoolean(); var valid = !fallback && translated.Length is > 0 and <= 4000; Reply(pending.Tp, pending.Text, valid ? translated : pending.Text); Emit(new { @event = "embed-result", requestId = replyId, sent = valid, fallback }); }
                            else Emit(new { @event = "embed-result", requestId = replyId, sent = false });
                            break;
                        case "cancel": CancelPending(); break;
                        case "stop": CancelPending(); return 0;
                    }
                }
                catch (Exception) { Emit(new { @event = "error", message = "Hook 控制命令处理失败。" }); }
            }
            return 0;
        }
        catch (Exception ex) { Emit(new { @event = "error", message = ex is DllNotFoundException or BadImageFormatException ? "Hook 原生组件缺失、位数不匹配或无法加载。" : ex.Message }); return 1; }
        finally { if (Library != nint.Zero && Pid != 0) { try { Export<ProcessCommand>("Luna_DetachProcess")(Pid); await Task.Delay(200); } catch { } } }
    }
}
