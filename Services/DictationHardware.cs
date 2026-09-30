using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Novalist.Extensions.AiAssistant.Services;

public static class DictationHardware
{
    public static readonly string[] Choices = ["auto", "cpu", "cuda", "rocm", "mlx"];
    private static readonly Lazy<string> Detected = new(Detect);
    public static string Automatic => Detected.Value;

    public static string Resolve(string choice)
    {
        if (!Choices.Contains(choice)) throw new ArgumentException("Choose a supported dictation accelerator.");
        var backend = choice == "auto" ? Automatic : choice;
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && !OperatingSystem.IsMacOSVersionAtLeast(14))
            throw new PlatformNotSupportedException("Local dictation on Apple Silicon requires macOS 14 or newer.");
        if (backend == "mlx" && (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64))
            throw new PlatformNotSupportedException("MLX dictation requires the native Apple Silicon build of Novalist.");
        if (backend is "cuda" or "rocm" && (OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.X64))
            throw new PlatformNotSupportedException("CUDA and ROCm dictation require 64-bit Windows or Linux.");
        return backend;
    }

    public static string SelectWindows(IEnumerable<string> names)
    {
        var adapters = names.Select(name => Regex.Replace(name.ToUpperInvariant()
            .Replace("(TM)", "").Replace("™", ""), @"\s+", " ").Trim()).ToArray();
        if (adapters.Any(name => name.Contains("NVIDIA", StringComparison.Ordinal))) return "cuda";
        // Official ROCm 7.2.1 Windows support, shared with Qwen Speech. Other
        // AMD adapters remain on CPU unless the writer explicitly selects ROCm.
        string[] supported = ["RX 9070 XT", "RX 9070", "RX 9060 XT", "RX 7900 XTX",
            "RX 7700", "AI PRO R9700", "PRO W7900", "PRO W7900 DUAL SLOT"];
        return adapters.Any(name => supported.Any(model => name == "AMD RADEON " + model
            || name == "RADEON " + model)) ? "rocm" : "cpu";
    }

    public static string RequirementsFile(string backend, bool windows) => backend switch
    {
        "cpu" => "requirements.txt",
        "cuda" => "requirements-cuda.txt",
        "rocm" => windows ? "requirements-rocm-windows.txt" : "requirements-rocm-linux.txt",
        "mlx" => "requirements-mlx.txt",
        _ => throw new ArgumentException("Unknown dictation backend.")
    };

    private static string Detect()
    {
        if (OperatingSystem.IsMacOS())
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "mlx" : "cpu";
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64) return "cpu";
        if (OperatingSystem.IsWindows())
        {
            var names = new List<string>();
            for (uint index = 0; ; index++)
            {
                var adapter = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
                if (!EnumDisplayDevices(null, index, ref adapter, 0)) break;
                names.Add(adapter.Description);
            }
            return SelectWindows(names);
        }
        try
        {
            var vendors = Directory.EnumerateDirectories("/sys/class/drm", "card*")
                .Select(path => Path.Combine(path, "device", "vendor")).Where(File.Exists)
                .Select(path => File.ReadAllText(path).Trim()).ToArray();
            if (vendors.Contains("0x10de") && File.Exists("/dev/nvidiactl")) return "cuda";
            if (vendors.Contains("0x1002") && File.Exists("/dev/kfd")) return "rocm";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return "cpu";
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public int Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice display, uint flags);
}
