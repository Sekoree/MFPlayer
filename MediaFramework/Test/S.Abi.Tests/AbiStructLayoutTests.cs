using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Xunit;

namespace S.Abi.Tests;

/// <summary>
/// The managed mirrors in <c>AbiNative.cs</c> are kept in sync with <c>include/mfp_plugin.h</c> BY HAND.
/// Nothing in the build checks that, so a field added on one side - or a type whose width differs between
/// them - silently misreads every plugin that uses the struct, with no compiler error and no test failure.
/// </summary>
/// <remarks>
/// This compiles a tiny C probe against the real header, has it print <c>sizeof</c> and every
/// <c>offsetof</c>, and compares those numbers with <see cref="Marshal.SizeOf{T}"/> /
/// <see cref="Marshal.OffsetOf{T}"/> for the managed mirror. It runs wherever gcc does and skips (with a
/// reason) where it does not, exactly like the real-plugin tests next door.
/// </remarks>
public sealed class AbiStructLayoutTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mfp-abi-layout-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Managed mirror → the C fields it must match, in declaration order.</summary>
    private static readonly (Type Managed, string CName, string[] Fields)[] Mirrors =
    [
        (typeof(MfpEffectParameterDescriptor), "MfpEffectParameterDescriptor",
            ["abi_version", "struct_size", "id", "display_name", "unit",
             "minimum", "maximum", "default_value", "scale", "flags"]),
        (typeof(MfpAudioEffectVTable), "MfpAudioEffectVTable",
            ["abi_version", "struct_size", "configure", "process", "destroy", "set_parameter"]),
        (typeof(MfpAudioEffectFactoryVTable), "MfpAudioEffectFactoryVTable",
            ["abi_version", "struct_size", "create", "effect_vtable", "destroy",
             "get_parameter_count", "get_parameter_descriptor"]),
        (typeof(MfpVideoEffectVTable), "MfpVideoEffectVTable",
            ["abi_version", "struct_size", "configure", "process", "destroy"]),
        (typeof(MfpVideoEffectFactoryVTable), "MfpVideoEffectFactoryVTable",
            ["abi_version", "struct_size", "create", "effect_vtable", "destroy"]),
    ];

    [MediaPluginDirectoryTests.GccFact]
    public void ManagedMirrorsMatchTheCHeaderExactly()
    {
        var actual = RunLayoutProbe();

        foreach (var (managed, cName, fields) in Mirrors)
        {
            Assert.True(
                actual.TryGetValue($"{cName}.sizeof", out var cSize),
                $"probe produced no size for {cName}");
            Assert.True(
                Marshal.SizeOf(managed) == cSize,
                $"{cName}: C sizeof is {cSize}, managed {managed.Name} is {Marshal.SizeOf(managed)}");

            var managedFields = managed.GetFields();
            Assert.True(
                managedFields.Length == fields.Length,
                $"{cName}: header lists {fields.Length} fields, managed {managed.Name} has {managedFields.Length}");

            for (var index = 0; index < fields.Length; index++)
            {
                var cOffset = actual[$"{cName}.{fields[index]}"];
                var managedOffset = (int)Marshal.OffsetOf(managed, managedFields[index].Name);
                Assert.True(
                    cOffset == managedOffset,
                    $"{cName}.{fields[index]} is at {cOffset} in C but {managed.Name}."
                    + $"{managedFields[index].Name} is at {managedOffset}");
            }
        }
    }

    /// <summary>Compiles and runs a generated probe, returning <c>Struct.field → offset</c> (plus
    /// <c>Struct.sizeof</c>).</summary>
    private Dictionary<string, int> RunLayoutProbe()
    {
        var source = new System.Text.StringBuilder()
            .AppendLine("#include <stddef.h>")
            .AppendLine("#include <stdio.h>")
            .AppendLine("#include \"mfp_plugin.h\"")
            .AppendLine("int main(void) {");
        foreach (var (_, cName, fields) in Mirrors)
        {
            source.AppendLine(
                $"  printf(\"{cName}.sizeof=%zu\\n\", sizeof(struct {cName}));");
            foreach (var field in fields)
                source.AppendLine(
                    $"  printf(\"{cName}.{field}=%zu\\n\", offsetof(struct {cName}, {field}));");
        }

        source.AppendLine("  return 0;").AppendLine("}");

        var cFile = Path.Combine(_dir, "layout.c");
        var exe = Path.Combine(_dir, "layout");
        File.WriteAllText(cFile, source.ToString());

        var include = Path.Combine(
            MediaPluginDirectoryTests.RepoRoot()!, "MediaFramework", "Interop", "S.Abi", "include");
        using (var gcc = Process.Start(new ProcessStartInfo(
                   "gcc", $"-I\"{include}\" \"{cFile}\" -o \"{exe}\"") { RedirectStandardError = true })!)
        {
            gcc.WaitForExit(30_000);
            Assert.True(gcc.ExitCode == 0, $"probe failed to compile: {gcc.StandardError.ReadToEnd()}");
        }

        using var run = Process.Start(
            new ProcessStartInfo(exe) { RedirectStandardOutput = true })!;
        var output = run.StandardOutput.ReadToEnd();
        run.WaitForExit(30_000);
        Assert.Equal(0, run.ExitCode);

        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Split('='))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => int.Parse(parts[1], CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
    }
}
