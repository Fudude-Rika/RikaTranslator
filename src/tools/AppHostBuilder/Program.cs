using Microsoft.NET.HostModel.AppHost;

if (args.Length != 4) throw new ArgumentException("template, destination, relative DLL, resource EXE required");
var hookLauncher = Path.GetFileName(args[1]) == "RikaHookWorker.exe" && args[2] is "../../runtime/RikaHookWorker.dll" or "../../RikaHookWorker.dll";
if (!hookLauncher && (!args[2].StartsWith("runtime/") || args[2].Contains(".."))) throw new ArgumentException("Runtime DLL must be relative to the launcher");
HostWriter.CreateAppHost(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]), args[2], windowsGraphicalUserInterface: true, assemblyToCopyResourcesFrom: Path.GetFullPath(args[3]));
