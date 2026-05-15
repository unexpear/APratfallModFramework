using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PratfallModFramework;

// Static IL safety scanner. Walks every method in the mod's DLL and reports
// concerning API usage — Process spawn, network sockets, registry, P/Invoke,
// reflection emit, file deletion, environment probing. The user sees a finding
// list with severity and call sites; they decide whether to trust the mod.
//
// This is a static scan: no code runs. The scan loads the DLL via Mono.Cecil
// without resolving references, so it's safe to scan unsigned/untrusted bytes.
public static class ModScanner
{
    public enum Severity { Info, Warning, Danger }

    public sealed class Finding
    {
        public Severity Sev;
        public string Category = "";
        public string ApiCalled = "";
        public string CallSite = ""; // declaring type . method
        public string Note = "";
    }

    public sealed class Report
    {
        public string ModId = "";
        public string DllPath = "";
        public List<Finding> Findings = new();
        public List<string> ScanErrors = new();
        public bool ScannedSuccessfully;
        public int MethodsScanned;

        public int CountOf(Severity s) => Findings.Count(f => f.Sev == s);
    }

    // Pattern: namespace prefix → category, severity, note, optional method-name filter.
    private sealed record Rule(string NsPrefix, string? TypeName, string? MethodName, Severity Sev, string Category, string Note);

    private static readonly List<Rule> Rules = new()
    {
        // --- DANGER: native code execution ---
        new Rule("System.Diagnostics", "Process", "Start", Severity.Danger, "Process Execution",
            "Mod can launch external programs (e.g. cmd, browsers, installers)."),
        new Rule("System.Diagnostics", "ProcessStartInfo", null, Severity.Danger, "Process Execution",
            "Mod constructs ProcessStartInfo — typically used to launch external programs."),

        // --- DANGER: arbitrary network access (mods should use the framework's network layer) ---
        new Rule("System.Net.Sockets", null, null, Severity.Danger, "Raw Network",
            "Mod opens raw network sockets (out-of-band from the framework's P2P channel)."),
        new Rule("System.Net.Http", "HttpClient", null, Severity.Danger, "HTTP Calls",
            "Mod makes HTTP requests to arbitrary URLs."),
        new Rule("System.Net", "WebClient", null, Severity.Danger, "HTTP Calls",
            "Mod uses WebClient — can download/upload arbitrary URLs."),
        new Rule("System.Net.WebSockets", null, null, Severity.Danger, "WebSocket",
            "Mod opens raw WebSockets to arbitrary endpoints."),

        // --- DANGER: registry, code generation, environment ---
        new Rule("Microsoft.Win32", "Registry", null, Severity.Danger, "Registry",
            "Mod reads or writes the Windows Registry."),
        new Rule("Microsoft.Win32", "RegistryKey", null, Severity.Danger, "Registry",
            "Mod manipulates registry keys."),
        new Rule("System.Reflection.Emit", null, null, Severity.Danger, "Code Generation",
            "Mod generates code at runtime — can bypass static analysis."),

        // --- WARNING: file system mutations beyond the mod's own folder ---
        new Rule("System.IO", "File", "Delete", Severity.Warning, "File Deletion",
            "Mod can delete files. Confirm path is the mod's own folder."),
        new Rule("System.IO", "File", "Move", Severity.Warning, "File Move",
            "Mod can move/rename files."),
        new Rule("System.IO", "Directory", "Delete", Severity.Warning, "Directory Deletion",
            "Mod can delete directories. Confirm path is the mod's own folder."),
        new Rule("System.IO", "File", "WriteAllBytes", Severity.Info, "File Write",
            "Mod writes files (may be config, save data, or extracted assets)."),
        new Rule("System.IO", "File", "WriteAllText", Severity.Info, "File Write",
            "Mod writes text files (may be config or logs)."),

        // --- WARNING: information probing ---
        new Rule("System", "Environment", "GetEnvironmentVariable", Severity.Warning, "Environment Probe",
            "Mod reads environment variables — can leak machine configuration."),
        new Rule("System", "Environment", "get_UserName", Severity.Warning, "Identity Probe",
            "Mod reads the local Windows username."),
        new Rule("System", "Environment", "get_MachineName", Severity.Warning, "Identity Probe",
            "Mod reads the local machine name."),

        // --- INFO: things worth surfacing but usually benign ---
        new Rule("System.Runtime.InteropServices", "Marshal", null, Severity.Warning, "Unsafe Marshalling",
            "Mod uses raw memory marshalling — review carefully."),
    };

    public static Report Scan(ModManifest manifest)
    {
        var report = new Report { ModId = manifest.Id };

        var dllPath = ResolveDllPath(manifest);
        if (dllPath == null)
        {
            report.ScanErrors.Add("DLL not found — nothing to scan. (Manifest-only or asset-only mods have no executable code.)");
            return report;
        }
        report.DllPath = dllPath;

        if (!EnsureCecilLoaded(report))
            return report;

        try
        {
            ScanWithCecil(dllPath, report);
            report.ScannedSuccessfully = true;
        }
        catch (Exception ex)
        {
            report.ScanErrors.Add($"Scan failed: {ex.GetType().Name}: {ex.Message}");
        }

        return report;
    }

    // The actual scan is in a separate method so EnsureCecilLoaded can run before
    // the JIT touches Mono.Cecil types in the caller frame (otherwise the caller
    // would FileNotFound on Mono.Cecil before EnsureCecilLoaded could fix it).
    private static void ScanWithCecil(string dllPath, Report report)
    {
        var readerParams = new ReaderParameters { AssemblyResolver = new NullAssemblyResolver() };
        using var asm = AssemblyDefinition.ReadAssembly(dllPath, readerParams);

        foreach (var module in asm.Modules)
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    report.MethodsScanned++;

                    // P/Invoke surface: the method itself is a native binding.
                    if (method.HasPInvokeInfo)
                    {
                        report.Findings.Add(new Finding
                        {
                            Sev = Severity.Danger,
                            Category = "P/Invoke (Native Binding)",
                            ApiCalled = $"[DllImport] {method.PInvokeInfo.Module.Name}!{method.PInvokeInfo.EntryPoint ?? method.Name}",
                            CallSite = FormatCallSite(type, method),
                            Note = "Mod declares a direct binding to a native DLL function — can do anything the OS allows.",
                        });
                    }

                    if (method.Body == null) continue;

                    foreach (var ins in method.Body.Instructions)
                    {
                        if (ins.OpCode != OpCodes.Call && ins.OpCode != OpCodes.Callvirt &&
                            ins.OpCode != OpCodes.Newobj && ins.OpCode != OpCodes.Ldftn) continue;
                        if (ins.Operand is not MethodReference mref) continue;

                        var match = MatchRule(mref);
                        if (match == null) continue;

                        report.Findings.Add(new Finding
                        {
                            Sev = match.Sev,
                            Category = match.Category,
                            ApiCalled = $"{mref.DeclaringType.FullName}::{mref.Name}",
                            CallSite = FormatCallSite(type, method),
                            Note = match.Note,
                        });
                    }
                }
            }
        }
    }

    private static Rule? MatchRule(MethodReference mref)
    {
        var declaringType = mref.DeclaringType;
        if (declaringType == null) return null;

        var ns = declaringType.Namespace ?? "";
        var typeName = declaringType.Name;
        var methodName = mref.Name;

        foreach (var r in Rules)
        {
            // Namespace prefix match (e.g. "System.Net.Sockets" matches anything under it)
            if (!ns.StartsWith(r.NsPrefix, StringComparison.Ordinal)) continue;
            if (r.TypeName != null && !string.Equals(typeName, r.TypeName, StringComparison.Ordinal)) continue;
            if (r.MethodName != null && !string.Equals(methodName, r.MethodName, StringComparison.Ordinal)) continue;
            return r;
        }
        return null;
    }

    private static string FormatCallSite(TypeDefinition type, MethodDefinition method)
    {
        var typeName = type.FullName ?? type.Name;
        return $"{typeName}::{method.Name}";
    }

    private static string? ResolveDllPath(ModManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.DirectoryPath)) return null;
        var fileName = !string.IsNullOrWhiteSpace(manifest.AssemblyFile)
            ? manifest.AssemblyFile
            : $"{manifest.Id}.dll";
        var path = Path.Combine(manifest.DirectoryPath, fileName);
        return File.Exists(path) ? path : null;
    }

    // --- Lazy loader for embedded Mono.Cecil ---
    private static int _cecilLoaded; // 0 = not yet, 1 = success, 2 = failed
    private static bool EnsureCecilLoaded(Report report)
    {
        if (_cecilLoaded == 1) return true;
        if (_cecilLoaded == 2)
        {
            report.ScanErrors.Add("Mono.Cecil could not be loaded — scanner unavailable.");
            return false;
        }

        var frameworkAssembly = typeof(ModScanner).Assembly;
        var hostContext = AssemblyLoadContext.GetLoadContext(frameworkAssembly);
        if (hostContext == null)
        {
            report.ScanErrors.Add("Could not resolve framework load context.");
            _cecilLoaded = 2;
            return false;
        }

        try
        {
            var alreadyLoaded = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => a.GetName().Name == "Mono.Cecil");
            if (alreadyLoaded) { _cecilLoaded = 1; return true; }

            using var stream = frameworkAssembly.GetManifestResourceStream("Mono.Cecil.dll");
            if (stream != null)
            {
                hostContext.LoadFromStream(stream);
                _cecilLoaded = 1;
                return true;
            }

            var dir = Path.GetDirectoryName(frameworkAssembly.Location);
            if (dir != null)
            {
                var sidecar = Path.Combine(dir, "Mono.Cecil.dll");
                if (File.Exists(sidecar))
                {
                    hostContext.LoadFromAssemblyPath(sidecar);
                    _cecilLoaded = 1;
                    return true;
                }
            }

            report.ScanErrors.Add("Mono.Cecil.dll not found as embedded resource or sidecar — scanner unavailable.");
            _cecilLoaded = 2;
            return false;
        }
        catch (Exception ex)
        {
            report.ScanErrors.Add($"Failed to load Mono.Cecil: {ex.GetType().Name}: {ex.Message}");
            _cecilLoaded = 2;
            return false;
        }
    }

    // Cecil tries to resolve referenced assemblies (e.g. mscorlib, GodotSharp) for
    // method body analysis. We only need the IL surface, not full type resolution,
    // so a no-op resolver keeps the scan from failing on missing references.
    private sealed class NullAssemblyResolver : IAssemblyResolver
    {
        public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;
        public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;
        public void Dispose() { }
    }
}
