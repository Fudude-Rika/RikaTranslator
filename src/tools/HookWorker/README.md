# RikaHookWorker

An independent GPL-3.0-or-later program using the LunaHook C ABI from LunaTranslator v12.0.1. Rika 0.7.4 uses the source-built Rika font fix while preserving the C ABI. The Rika Translator application communicates with this program using JSON lines on redirected standard input/output. No API keys are supplied to this process.

Build with the .NET 10 SDK: `dotnet publish HookWorker.csproj -c Release -r win-x64 --self-contained true`.

The complete worker source folder or archive contains this project, Program.cs, COPYING and this README. It can be built independently; the application icon is optional and is only used when building inside the Rika Translator tree. A standalone publish keeps its managed files beside the EXE. The portable application's build script instead places those files in the shared runtime directory and creates the corresponding relative-path launcher. Use `build.ps1 -LooseHookSource` to include this complete source as a folder without creating a new archive.

Usage: `RikaHookWorker.exe NATIVE_DIRECTORY PID EXPECTED_EXECUTABLE_PATH x86|x64 true|false`.

The native directory contains the original LunaHost64.dll and LunaSubprocess32/64.exe from the official v12.0.1 x64 release, and LunaHook32/64.dll rebuilt from the same tag with the Rika font resizing fix. Corresponding complete upstream source, the modified source, build instructions, pinned MinHook source and copyright/license notices accompany the binary distribution in `licenses/galgame`. Component SHA-256 values are verified before injection. Games injected by an older version must be completely restarted to load the new DLL.

Commands: select, configure, insert, reply, cancel, stop. Output events: connected, disconnected, thread, removed, text, embed, embed-timeout, embed-result, ready, inserted, log, error. Native callbacks are kept rooted for the process lifetime. Requests exceeding the embedding deadline return the original text. API translation takes place exclusively in the parent application.

The configure command accepts `embedFontSizePercent` (integer 50–200, default 100). It forwards a relative factor to Luna_SettingsEx, enabling font resizing only when the percentage is not 100. This affects supported game font interception; overlay size is managed independently by the parent application.
