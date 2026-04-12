using System;
using System.Linq;
using System.Reflection;

var asm = Assembly.LoadFrom("/home/seko/.nuget/packages/ffmpeg.autogen/8.0.0/lib/netstandard2.1/FFmpeg.AutoGen.dll");
var ffmpegType = asm.GetType("FFmpeg.AutoGen.ffmpeg");
var m = ffmpegType?.GetMethod("avio_alloc_context", BindingFlags.Public | BindingFlags.Static);
if (m != null) {
    Console.WriteLine("=== avio_alloc_context ===");
    foreach (var p in m.GetParameters())
        Console.WriteLine($"  {p.ParameterType} {p.Name}");
    Console.WriteLine($"  Return: {m.ReturnType}");
}

foreach (var name in new[] { "avio_alloc_context_read_packet_func", "avio_alloc_context_write_packet_func", "avio_alloc_context_seek_func" }) {
    var t = asm.GetType("FFmpeg.AutoGen." + name);
    if (t != null) {
        var inv = t.GetMethod("Invoke");
        Console.WriteLine($"\n=== {name} ===");
        Console.WriteLine($"  Return: {inv?.ReturnType}");
        if (inv != null)
            foreach (var p in inv.GetParameters())
                Console.WriteLine($"  {p.ParameterType} {p.Name}");
    }
}

