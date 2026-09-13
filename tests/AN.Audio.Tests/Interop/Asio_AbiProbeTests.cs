using System.Diagnostics;
using System.Text.RegularExpressions;
using AN.Audio.BindingsCompiler;
using AN.Audio.Tests.BindingsCompiler;
using Xunit;

namespace AN.Audio.Tests.Interop;

/// <summary>Spec 71 D7 — compile Interop/abi-probe.cpp against the REAL SDK headers with MSVC and diff its sizes/offsets/slots against the
/// bindings compiler's ABI model. Skips (passes vacuously, with a message) when cl.exe or the SDK is not on this machine — same policy as
/// FluidUI's gcc probe.</summary>
public class Asio_AbiProbeTests
{
    private static string? FindVsInstallation()
    {
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        var psi = new ProcessStartInfo(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath") { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        string path = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return path.Length > 0 && Directory.Exists(path) ? path : null;
    }

    private static string? SdkRoot()
    {
        string wanted = Path.Combine(CHeader_AbiModelTests.RepoRoot(), "src", "AN.Audio", "CodeGen", "Wanted.ytdata.hjson");
        var list = CHeader_Extractor.ParseHjson<CHeader_WantedList>(File.ReadAllText(wanted), wanted);
        string root = Assert.Single(list.Surfaces, s => s.Name == "Asio").SdkRoot;
        return Directory.Exists(root) ? root : null;
    }

    [Fact]
    public void Msvc_agrees_with_the_abi_model_on_x64()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? vs = FindVsInstallation();
        string? sdk = SdkRoot();
        if (vs is null || sdk is null) { Console.Error.WriteLine($"[skip] ABI probe: vs={vs ?? "none"} sdk={sdk ?? "none"}"); return; }
        string vcvars = Path.Combine(vs, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcvars)) { Console.Error.WriteLine("[skip] ABI probe: vcvars64.bat not found"); return; }

        string src = Path.Combine(AppContext.BaseDirectory, "Interop", "abi-probe.cpp");
        Assert.True(File.Exists(src), src);
        string work = Path.Combine(Path.GetTempPath(), "an-audio-abi-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            string cmd = $"\"{vcvars}\" >nul && cl /nologo /EHsc /W3 /I\"{Path.Combine(sdk, "common")}\" \"{src}\" /Fe:probe.exe /Fo:probe.obj >cl.log 2>&1 && probe.exe";
            var psi = new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string stdout = p.StandardOutput.ReadToEnd(); string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();
            string clLog = File.Exists(Path.Combine(work, "cl.log")) ? File.ReadAllText(Path.Combine(work, "cl.log")) : "";
            Assert.True(p.ExitCode == 0, $"probe build/run failed ({p.ExitCode}):\n{clLog}\n{stderr}\n{stdout}");

            var sizes = new Dictionary<string, int>(); var offsets = new Dictionary<(string, string), int>(); var slots = new Dictionary<string, int>(); int ptr = 0;
            foreach (Match m in Regex.Matches(stdout, @"^(SIZE|OFFSET|SLOT|PTR) (.*?)\s*$", RegexOptions.Multiline))
            {
                string rest = m.Groups[2].Value.Trim();
                string[] a = rest.Split(' ');
                switch (m.Groups[1].Value)
                {
                    case "PTR": ptr = int.Parse(a[0]); break;
                    case "SIZE": sizes[a[0]] = int.Parse(a[1]); break;
                    case "OFFSET": offsets[(a[0], a[1])] = int.Parse(a[2]); break;
                    case "SLOT": { var s = Regex.Match(rest, @"^(\w+)\(.*\)\s+(-?\d+)$"); Assert.True(s.Success, "bad SLOT line: " + rest); slots[s.Groups[1].Value] = int.Parse(s.Groups[2].Value); break; }
                }
            }
            Assert.Equal(8, ptr);
            Assert.True(sizes.Count >= 9, "probe printed no sizes: " + stdout);

            var surface = CHeader_AbiModelTests.AsioSurface();
            var model = CHeader_AbiModel.For("msvc-x64");
            model.Load(surface, n => "Asio_" + n);
            var mismatches = new List<string>();
            foreach (var (name, size) in sizes)
            {
                var st = Assert.Single(surface.Structs, s => s.Name == name);
                var l = model.LayoutOf(st);
                if (l.Size != size) mismatches.Add($"sizeof({name}): msvc={size} model={l.Size}");
                foreach (var f in l.Fields) if (offsets.TryGetValue((name, f.Field.Name), out int o) && o != f.Offset) mismatches.Add($"offsetof({name},{f.Field.Name}): msvc={o} model={f.Offset}");
            }
            var cls = Assert.Single(surface.Classes, c => c.Name == "IASIO");
            foreach (var m in cls.Methods) if (!slots.TryGetValue(m.Name, out int s) || s != m.Slot) mismatches.Add($"slot({m.Name}): msvc={(slots.TryGetValue(m.Name, out int v) ? v : -1)} model={m.Slot}");
            Assert.True(mismatches.Count == 0, "ABI model disagrees with MSVC:\n" + string.Join("\n", mismatches));
        }
        finally { try { Directory.Delete(work, recursive: true); } catch { /* best effort */ } }
    }
}