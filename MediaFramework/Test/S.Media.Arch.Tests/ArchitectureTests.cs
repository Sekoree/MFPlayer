using System.Xml.Linq;
using Xunit;

namespace S.Media.Arch.Tests;

/// <summary>
/// Enforces the layered dependency rules: dependencies point down only, and each project may reference
/// only its allowed set. The test reads the <c>*.csproj</c> graph directly, so it is independent of build
/// order. Every first-party production project (framework libraries + native wrappers) must appear in
/// <see cref="Allowed"/>; <c>Tools/</c> and <c>Test/</c> are harness and exempt.
/// </summary>
public sealed class ArchitectureTests
{
    // project -> ProjectReference target names it is ALLOWED to have. Native-wrapper projects
    // (PALib/MALib/NDILib/OSCLib/PMLib/LibAssLib) reference no other first-party project, so their allowed
    // set is empty.
    private static readonly IReadOnlyDictionary<string, string[]> Allowed = new Dictionary<string, string[]>
    {
        ["S.Media.Core"] = [],
        ["S.Media.Time"] = ["S.Media.Core"],
        ["S.Media.Routing"] = ["S.Media.Core", "S.Media.Time"],
        ["S.Media.Gpu"] = ["S.Media.Core"],
        ["S.Media.Compositor"] = ["S.Media.Core", "S.Media.Gpu"],
        ["S.Media.Players"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing"],
        ["S.Media.Session"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "S.Media.Players", "S.Media.Compositor"],
        ["S.Media.FFmpeg.Common"] = ["S.Media.Core"],
        // Time: the FFmpeg-backed audio output wrappers (ResamplingAudioOutput / AdaptiveRateAudioOutput)
        // forward IPlaybackClock, which lives in S.Media.Time. Downward ref (Time is tier 2); the module
        // keeps the cohesive FFmpeg audio-processing set together rather than spinning up a new project.
        ["S.Media.Decode.FFmpeg"] = ["S.Media.Core", "S.Media.Time", "S.Media.FFmpeg.Common"],
        // Encode module (Tier 3b slot from the rewrite docs): packet-producing encoders + mux sinks
        // behind IVideoOutput/IAudioOutput. Never references Decode.FFmpeg - shared glue lives in Common.
        ["S.Media.Encode.FFmpeg"] = ["S.Media.Core", "S.Media.FFmpeg.Common"],
        // LAN streaming server over the encode module's packet-sink seam. Sockets stay OUT of the
        // encode project; this is the one place HTTP meets the muxers.
        ["S.Media.Stream.Http"] = ["S.Media.Core", "S.Media.Encode.FFmpeg"],
        // projectM visualizer: an effect-bus visual source + NXT-10 GL layer surface (Compositor for
        // the surface contract, like Source.MMD). ProjectMLib is its dedicated P/Invoke binding.
        ["S.Media.Visualizer.ProjectM"] = ["S.Media.Core", "S.Media.Compositor", "ProjectMLib"],
        ["ProjectMLib"] = [],
        // External-source module (Gate 5): YoutubeExplode is an out-of-tree LOCAL SOURCE reference
        // (Reference/YoutubeExplode-6.6) and deliberately not part of the layering table.
        ["S.Media.Source.YouTube"] =
            ["S.Media.Core", "S.Media.FFmpeg.Common", "S.Media.Decode.FFmpeg", "S.Media.Time", "YoutubeExplode"],
        // MMD prototype (Gate 6): pure managed PMX/VMD + software render, plus the NXT-10 GL
        // layer-surface renderer (Compositor for the surface contract; Silk.NET bindings and
        // StbImageSharp are pure managed - a GL context only ever comes from the hosting compositor,
        // so the module still ships no native runtime of its own).
        ["S.Media.Source.MMD"] = ["S.Media.Core", "S.Media.Time", "S.Media.Compositor"],
        // Text cue source (SESSION-02): a pure-managed SkiaSharp text rasterizer + held-frame source. References
        // Decode.FFmpeg only for the swscale CPU converter that repacks the rendered BGRA card to the negotiated
        // output format. SkiaSharp is a NuGet package (isolated here), not a first-party project ref.
        ["S.Media.Source.Text"] = ["S.Media.Core", "S.Media.Decode.FFmpeg"],
        ["S.Media.Audio.PortAudio"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "PALib"],
        ["S.Media.Audio.MiniAudio"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "MALib"],
        ["S.Media.Present.SDL3"] = ["S.Media.Core", "S.Media.Gpu"],
        // The SDL3<->Compositor bridge (D7): the one place SDL3 + Compositor meet, kept out of the
        // Present.SDL3 presenter so that stays [Core, Gpu]. References Present.SDL3 for SDL3Runtime only.
        ["S.Media.Present.SDL3.Compositor"] = ["S.Media.Core", "S.Media.Gpu", "S.Media.Compositor", "S.Media.Present.SDL3"],
        ["S.Media.Present.Avalonia"] = ["S.Media.Core", "S.Media.Gpu"],
        ["S.Media.NDI"] = ["S.Media.Core", "S.Media.Time", "S.Media.Routing", "NDILib"],
        ["S.Media.Subtitles"] = ["S.Media.Core", "LibAssLib"],
        ["S.Control.Abstractions"] = ["OSCLib"],
        ["S.Control"] = ["S.Media.Core", "S.Media.Session", "S.Control.Abstractions", "PMLib", "OSCLib"],
        ["S.Abi"] = ["S.Media.Core", "S.Media.Time", "S.Media.Compositor", "S.Control.Abstractions"],
        // S.Media.Interop is the host: it bundles the backend modules it ships (Phase 7).
        ["S.Media.Interop"] =
        [
            "S.Media.Core", "S.Media.Session", "S.Media.Time", "S.Media.Routing", "S.Media.Gpu",
            "S.Media.Compositor", "S.Media.Players", "S.Media.FFmpeg.Common", "S.Media.Decode.FFmpeg",
            "S.Media.Audio.PortAudio", "S.Media.Audio.MiniAudio",
            "S.Media.Present.SDL3", "S.Media.Present.Avalonia", "S.Media.NDI",
            "S.Media.Subtitles",
        ],
        // Native-runtime wrapper projects: pure P/Invoke bindings, no first-party project references.
        ["PALib"] = [],
        ["MALib"] = [],
        ["PMLib"] = [],
        ["NDILib"] = [],
        ["OSCLib"] = [],
        ["LibAssLib"] = [],
    };

    // First-party production subtrees that MUST be covered by the Allowed map (Tools/ and Test/ are harness,
    // exempt). Includes the native-wrapper trees (TEST-02) so PALib/MALib/PMLib/NDILib/OSCLib/LibAssLib are
    // checked too, not just the S.Media.* / S.Control.* / S.Abi projects.
    private static readonly string[] FrameworkDirs =
        ["Media", "Control", "Interop", "Audio", "MIDI", "NDI", "OSC", "Subtitles", "Visualizer"];

    /// <summary>
    /// Layering rules for the app tree. Until now <c>UI/</c> was invisible to these tests - the scope
    /// walked only <c>MediaFramework/</c> - so the apps and the shared app-support libraries had NO
    /// enforcement at all, and the extraction plan's "register each new library in the arch tests" was
    /// impossible to satisfy. This is that scope.
    /// </summary>
    /// <remarks>
    /// Two differences from the framework map. Out-of-tree names are allowed (HaPlay references three
    /// <c>External/Classic.Avalonia</c> projects), and test projects under <c>UI/</c> are exempt exactly
    /// as <c>Test/</c> is in the framework: a test legitimately reaches across layers, so enforcing them
    /// adds churn without catching real violations.
    /// </remarks>
    private static readonly Dictionary<string, HashSet<string>> UiAllowed = new(StringComparer.Ordinal)
    {
        // The apps sit at the top and may consume any framework layer.
        ["HaPlay"] =
        [
            "Classic.Avalonia.Theme", "Classic.Avalonia.Theme.ColorPicker", "Classic.Avalonia.Theme.Dock",
            "S.Media.Core", "S.Media.Time", "S.Media.Routing", "S.Media.Players", "S.Media.Gpu",
            "S.Media.Compositor", "S.Media.Session", "S.Media.Subtitles", "S.Media.Interop", "S.Abi",
            "S.Media.Decode.FFmpeg", "S.Media.Encode.FFmpeg", "S.Media.Stream.Http",
            "S.Media.Visualizer.ProjectM", "S.Media.Source.YouTube", "S.Media.Source.MMD",
            "S.Media.Source.Text", "S.Media.Audio.PortAudio", "S.Media.Audio.MiniAudio", "S.Media.NDI",
            "S.Media.Present.SDL3", "S.Media.Present.SDL3.Compositor", "S.Media.Present.Avalonia",
            "S.Control", "S.Control.Abstractions", "PMLib", "OSCLib",
            // Shared app-support libraries, as they land (HaCue2 extraction phase 2).
            "HaOutput", "HaSource", "HaControl.Input", "HaStrings",
        ],
        // HaCue2 is the cue player leaving HaPlay. It sees the project model and MACHINE FACTS
        // (HaCue2.Machine: audio-device enumeration and media probing, plus the decoder they need).
        // What must NOT appear here is "S.Media.Session" — the shell projects a document and asks the
        // box what it has; it does not run a show. That reference lands in Phase 5, when
        // `Session/ShowRuntime` stops being a stand-in, and its absence until then is what proves the
        // shell is not quietly depending on a session. "HaPlay" must never appear at all, which
        // NoAppReferencesAnotherApp asserts separately.
        ["HaCue2"] =
        [
            "HaCue2.Core", "HaCue2.Machine", "HaCue2.Engine",
            // Transitively, through HaCue2.Machine's decoder and HaCue2.Core's model.
            "S.Media.Decode.FFmpeg", "S.Media.Session", "S.Media.Core", "S.Media.Time",
            "S.Media.Routing", "S.Media.Players", "S.Media.Compositor", "S.Media.Gpu",
        ],
        // The running show: the session, the project patch bay and the program-audio target that
        // joins them. Separate from HaCue2 because a session is long-lived, thread-affine and holds
        // devices — the views must not be able to reach into it by accident, and they do not: they
        // see it through `Session/ShowRuntime`, exactly as they saw the sample before it existed.
        ["HaCue2.Engine"] =
        [
            "HaCue2.Core", "HaCue2.Machine", "S.Media.Session", "S.Media.Routing",
            "S.Media.Decode.FFmpeg", "S.Media.Core", "S.Media.Time", "S.Media.Players",
            "S.Media.Compositor", "S.Media.Gpu",
            // Action cues send OSC from here, beside the transport that fires them.
            "OSCLib",
        ],
        // Machine facts: what this box has and what a file turned out to be. Separate from
        // HaCue2.Core because everything here needs real hardware or a real decoder, and Core has to
        // stay runnable where there is neither — that is what lets `hacue2-check` run in CI.
        ["HaCue2.Machine"] =
        [
            "HaCue2.Core", "S.Media.Decode.FFmpeg", "S.Media.Session", "S.Media.Core", "S.Media.Time",
            "S.Media.Routing", "S.Media.Players", "S.Media.Compositor", "S.Media.Gpu",
        ],
        // The project model: document, journal, validation, patch operations. It may see the session
        // layer (it reuses ShowValidationIssue and CustomFadeCurve, and the compiler that turns a
        // HaCueProject into a ShowDocument lands here) but nothing above it, and no UI toolkit — this
        // is what keeps the project-status pass runnable from a script.
        ["HaCue2.Core"] =
        [
            "S.Media.Session", "S.Media.Core", "S.Media.Time", "S.Media.Routing", "S.Media.Players",
            "S.Media.Compositor", "S.Media.Gpu",
        ],
        // The headless status runner: the project model and nothing else, which is what lets it run
        // where no audio backend or window system exists.
        ["HaCue2.Check"] = ["HaCue2.Core"],
        // A desktop head is a composition root: it references its app and nothing else.
        // A desktop head is a composition root: it references its app, plus the audio backend it
        // chooses to enumerate against — which backend is exactly the kind of decision a head makes.
        ["HaCue2.Desktop"] =
        [
            "HaCue2", "HaCue2.Core", "HaCue2.Machine", "HaCue2.Engine", "S.Media.Audio.PortAudio",
            "S.Media.Decode.FFmpeg", "S.Media.Session", "S.Media.Core", "S.Media.Time",
            "S.Media.Routing", "S.Media.Players", "S.Media.Compositor", "S.Media.Gpu",
        ],
        ["HaPlay.Desktop"] = ["HaPlay"],
        ["HaViz.Desktop"] =
        [
            "HaViz.Core", "S.Media.Core", "S.Media.Routing", "S.Media.Players", "S.Media.Decode.FFmpeg",
            "S.Media.Audio.PortAudio", "S.Media.Present.SDL3.Compositor",
        ],
        ["HaViz.Android"] = ["HaViz.Core"],
        ["HaViz.Core"] = ["S.Media.Core", "S.Media.Compositor", "S.Media.Visualizer.ProjectM", "S.Media.NDI"],
    };

    /// <summary>Every <c>UI/</c> project except test assemblies, which are exempt (see
    /// <see cref="UiAllowed"/>).</summary>
    private static IEnumerable<string> UiProjects(string root)
    {
        var ui = Path.Combine(root, "UI");
        if (!Directory.Exists(ui))
            yield break;
        foreach (var f in Directory.EnumerateFiles(ui, "*.csproj", SearchOption.AllDirectories))
        {
            if (!Path.GetFileNameWithoutExtension(f).EndsWith(".Tests", StringComparison.Ordinal))
                yield return f;
        }
    }

    [Fact]
    public void EveryUiProjectIsRegisteredInTheRules()
    {
        foreach (var csproj in UiProjects(RepoRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            Assert.True(UiAllowed.ContainsKey(name),
                $"'{name}' is not in the UiAllowed map. Add it there (with its allowed references) before adding the project.");
        }
    }

    [Fact]
    public void UiProjectReferencesAreAllowed()
    {
        var violations = new List<string>();
        foreach (var csproj in UiProjects(RepoRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (!UiAllowed.TryGetValue(name, out var allowed))
                continue;
            foreach (var dep in ProjectRefNames(csproj))
                if (!allowed.Contains(dep))
                    violations.Add($"{name} -> {dep}");
        }

        Assert.True(violations.Count == 0,
            "Disallowed UI project references (fix the ref or update the UiAllowed map):\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>No app may reference another app: HaCue2 must never depend on HaPlay, and the reverse
    /// only through the shared support libraries. This is the rule the whole extraction exists to make
    /// true, so it is worth asserting on its own rather than leaving it implicit in the map.</summary>
    [Fact]
    public void NoAppReferencesAnotherApp()
    {
        string[] apps = ["HaPlay", "HaCue2", "HaViz.Core"];
        var violations = new List<string>();
        foreach (var csproj in UiProjects(RepoRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            // A desktop head legitimately references its own app.
            if (name.EndsWith(".Desktop", StringComparison.Ordinal) ||
                name.EndsWith(".Android", StringComparison.Ordinal))
                continue;
            foreach (var dep in ProjectRefNames(csproj))
                if (apps.Contains(dep) && dep != name)
                    violations.Add($"{name} -> {dep}");
        }

        Assert.True(violations.Count == 0,
            "An app references another app:\n  " + string.Join("\n  ", violations));
    }

    /// <summary>
    /// Only the known composition root and still-coupled output-engine implementation types may name
    /// <c>OutputManagementViewModel</c>; playback/session consumers use <c>IOutputRuntimeCatalog</c>.
    /// </summary>
    /// <remarks>
    /// The allow-list makes today's remaining boundary work explicit (deck, line VM and local-window
    /// runtime) while scanning the whole app prevents a new consumer or renamed file from silently escaping
    /// a four-file spot check. Removing an allowed dependency deliberately requires updating this list.
    /// </remarks>
    [Fact]
    public void OnlyKnownOutputEngineOwnersUseTheConcreteViewModel()
    {
        var appRoot = Path.Combine(RepoRoot(), "UI", "HaPlay");
        string[] allowed =
        [
            Path.Combine("OutputPreview", "LocalVideoPreviewRuntime.cs"),
            Path.Combine("ViewModels", "MainViewModel.cs"),
            Path.Combine("ViewModels", "MediaPlayerViewModel.cs"),
            Path.Combine("ViewModels", "OutputLineViewModel.cs"),
            Path.Combine("ViewModels", "OutputManagementViewModel.cs"),
        ];

        Assert.All(allowed, rel => Assert.True(File.Exists(Path.Combine(appRoot, rel)),
            $"known concrete output-engine owner moved or disappeared: {rel}"));

        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var offenders = Directory.EnumerateFiles(appRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => (Path: path, Rel: Path.GetRelativePath(appRoot, path)))
            .Where(x => !x.Rel.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !x.Rel.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Where(x => !allowedSet.Contains(x.Rel))
            .Where(x => StripComments(File.ReadAllText(x.Path))
                .Contains("OutputManagementViewModel", StringComparison.Ordinal))
            .Select(x => x.Rel)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "new concrete output-engine dependencies must use IOutputRuntimeCatalog or be reviewed as an "
            + "explicit owner: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The cue layer drives the engine through <c>ICueRunnerHost</c> and never holds the session itself.
    /// </summary>
    /// <remarks>
    /// The other half of the engine/cue seam. The soundboard test guards "the engine has no soundboard in
    /// it"; this guards "the cue layer is liftable" - if the runner can reach <c>ShowSession</c> directly it
    /// can reach anything on it, and the seam stops being a boundary and becomes a suggestion.
    /// </remarks>
    [Fact]
    public void TheCueRunnerDrivesTheEngineThroughItsHostInterfaceOnly()
    {
        var path = Path.Combine(RepoRoot(), "MediaFramework", "Media", "S.Media.Session", "CueRunner.cs");
        Assert.True(File.Exists(path), $"expected the cue runner at {path}");
        var code = StripComments(File.ReadAllText(path));

        Assert.False(
            code.Contains("ShowSession", StringComparison.Ordinal),
            "CueRunner must reach the engine only through ICueRunnerHost, never ShowSession directly.");
    }

    /// <summary>Drops <c>//</c> comments so a source rule is about code, not prose. A file may legitimately
    /// NAME the type it is decoupled from when explaining the decoupling.</summary>
    private static string StripComments(string source) =>
        string.Join("\n", source.Split('\n').Select(line =>
        {
            var slashes = line.IndexOf("//", StringComparison.Ordinal);
            return slashes >= 0 ? line[..slashes] : line;
        }));

    /// <summary>
    /// The soundboard is neutral one-shot playback and must stay that way: no cue, no document, no
    /// transport group, no composition.
    /// </summary>
    /// <remarks>
    /// This is the engine/cue-semantics seam stated as a rule rather than an intention. The soundboard and
    /// the cue preview used to share one class holding a whole <c>ShowSession</c>, which is how an app
    /// adopting the playback engine ended up inheriting soundboard responsibilities. A source-text check is
    /// crude, but it fails on the way the coupling actually comes back - someone reaching for the session to
    /// get "just one thing" - and it fails at the moment it is written rather than a release later.
    /// </remarks>
    [Fact]
    public void TheSoundboardDoesNotReachIntoCueOrDocumentConcerns()
    {
        var path = Path.Combine(
            RepoRoot(), "MediaFramework", "Media", "S.Media.Session", "SoundboardVoicePlayer.cs");
        Assert.True(File.Exists(path), $"expected the soundboard player at {path}");
        var source = File.ReadAllText(path);

        string[] forbidden =
        [
            "ShowSession",      // the whole point: it takes ISessionVoiceHost, not the session
            "ShowDocument",
            "CueGraph",
            "CueDefinition",
            "ShowClipBinding",
            "TransportGroup",
            "TransportVoice",
            "ClipCompositionRuntime",
        ];

        // Comments are stripped first: the rule is about what the CODE depends on, and the file
        // legitimately points at ShowSession in prose ("the session forwards this event", "identical to
        // TransportVoice.StopClaim"). Those references are documentation of a relationship, not a use of it.
        var code = StripComments(source);

        var found = forbidden.Where(t => code.Contains(t, StringComparison.Ordinal)).ToArray();

        Assert.True(
            found.Length == 0,
            $"SoundboardVoicePlayer must not depend on cue/document/composition concerns; found: {string.Join(", ", found)}");
    }

    private static string RepoRoot()  // repo root = the directory holding MFPlayer.sln
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MFPlayer.sln")))
            dir = dir.Parent;
        Assert.True(dir is not null, "Could not locate MFPlayer.sln above the test output directory.");
        return dir!.FullName;
    }

    private static IEnumerable<string> FrameworkProjects(string root)
    {
        var fw = Path.Combine(root, "MediaFramework");
        foreach (var sub in FrameworkDirs)
        {
            var d = Path.Combine(fw, sub);
            if (!Directory.Exists(d))
                continue;
            foreach (var f in Directory.EnumerateFiles(d, "*.csproj", SearchOption.AllDirectories))
                yield return f;
        }
    }

    private static string[] ProjectRefNames(string csproj) =>
        XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => Path.GetFileNameWithoutExtension(s!.Replace('\\', '/')))
            .ToArray();

    [Fact]
    public void EveryFrameworkProjectIsRegisteredInTheRules()
    {
        foreach (var csproj in FrameworkProjects(RepoRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            Assert.True(Allowed.ContainsKey(name),
                $"'{name}' is not in the Allowed map. Add it here (with its allowed downward references) before adding the project.");
        }
    }

    [Fact]
    public void ProjectReferencesAreDownwardAndAllowed()
    {
        var violations = new List<string>();
        foreach (var csproj in FrameworkProjects(RepoRoot()))
        {
            var name = Path.GetFileNameWithoutExtension(csproj);
            if (!Allowed.TryGetValue(name, out var allowed))
                continue;
            foreach (var dep in ProjectRefNames(csproj))
                if (!allowed.Contains(dep))
                    violations.Add($"{name} -> {dep}");
        }

        Assert.True(violations.Count == 0,
            "Disallowed project references (violate the layering rules - fix the ref or update the Allowed map):\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void CoreHasNoProjectReferences()
    {
        var core = FrameworkProjects(RepoRoot())
            .Single(f => Path.GetFileNameWithoutExtension(f) == "S.Media.Core");
        Assert.Empty(ProjectRefNames(core));
    }

    [Theory]
    [InlineData("MIDI/PMLib/PMLib.csproj")]
    [InlineData("OSC/OSCLib/OSCLib.csproj")]
    [InlineData("Audio/PALib/PALib.csproj")]
    [InlineData("Audio/MALib/MALib.csproj")]
    [InlineData("NDI/NDILib/NDILib.csproj")]
    [InlineData("Subtitles/LibAssLib/LibAssLib.csproj")]
    public void NativeWrappersHaveNoFrameworkProjectReferences(string relativePath)
    {
        var project = Path.Combine(RepoRoot(), "MediaFramework", relativePath);
        Assert.Empty(ProjectRefNames(project));
    }

    [Theory]
    [InlineData("Audio/PALib/PALib.csproj")]
    [InlineData("Audio/MALib/MALib.csproj")]
    [InlineData("MIDI/PMLib/PMLib.csproj")]
    [InlineData("NDI/NDILib/NDILib.csproj")]
    [InlineData("Subtitles/LibAssLib/LibAssLib.csproj")]
    [InlineData("Visualizer/ProjectMLib/ProjectMLib.csproj")]
    [InlineData("Media/S.Media.Source.MMD/S.Media.Source.MMD.csproj")]
    [InlineData("Media/S.Media.Present.SDL3/S.Media.Present.SDL3.csproj")]
    public void BundledNativeWrappersUseSharedSystemFirstResolverPolicy(string relativePath)
    {
        var project = Path.Combine(RepoRoot(), "MediaFramework", relativePath);
        var linkedSources = XDocument.Load(project).Descendants("Compile")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include));

        Assert.Contains(linkedSources, include =>
            include!.Replace('\\', '/').EndsWith("Shared/SystemFirstNativeLibraryResolver.cs", StringComparison.Ordinal));
    }

    private static string[] SolutionProjectPaths(string root) =>
        File.ReadLines(Path.Combine(root, "MFPlayer.sln"))
            .Select(l => System.Text.RegularExpressions.Regex.Match(l, "= \"[^\"]+\", \"([^\"]+\\.csproj)\""))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToArray();

    [Fact]
    public void EverySolutionProjectExistsOnDisk()
    {
        // A fresh clone must build: an sln entry whose csproj was never committed (the review P2-5
        // meta packages were once added to the sln without the new Packages/ directory reaching git)
        // fails every `dotnet build MFPlayer.sln` with MSB3202. Packages/ is outside FrameworkDirs,
        // so only this check covers it.
        var root = RepoRoot();
        var missing = SolutionProjectPaths(root)
            .Where(p => !File.Exists(Path.Combine(root, p)))
            .ToList();

        Assert.True(missing.Count == 0,
            "MFPlayer.sln references project files that do not exist on disk (forgotten `git add`?):\n  "
            + string.Join("\n  ", missing));
    }

    [Fact]
    public void NoSolutionProjectIsGitignored()
    {
        // The trap that lost MediaFramework/Packages/ TWICE: the stock VS .gitignore's
        // `**/[Pp]ackages/*` rule (meant for the legacy NuGet restore cache) silently made
        // `git add` skip the meta-package sources, so the local tree built and tested green while
        // every fresh clone failed. On-disk existence can't see that - ask git itself.
        var root = RepoRoot();
        if (!Directory.Exists(Path.Combine(root, ".git")))
            return; // source tarball / exported tree - nothing to check

        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("check-ignore");
        psi.ArgumentList.Add("--stdin");

        using var git = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(git);
        foreach (var path in SolutionProjectPaths(root))
            git.StandardInput.WriteLine(path);
        git.StandardInput.Close();
        var ignored = git.StandardOutput.ReadToEnd();
        git.WaitForExit();

        // Exit 1 = nothing ignored (good); 0 = at least one ignored; anything else (128 = not a
        // repo, missing git) means the environment cannot answer - do not fail the build for that.
        Assert.True(git.ExitCode != 0,
            "MFPlayer.sln references project files that are GITIGNORED - `git add` will silently "
            + "skip them and a fresh clone cannot build. Fix .gitignore (see the "
            + "MediaFramework/Packages/ negation) for:\n  "
            + ignored.Trim().Replace("\n", "\n  "));
    }

    [Fact]
    public void EveryProjectReferencePathResolves()
    {
        var missing = new List<string>();
        foreach (var csproj in FrameworkProjects(RepoRoot()))
        {
            var dir = Path.GetDirectoryName(csproj)!;
            foreach (var inc in XDocument.Load(csproj).Descendants("ProjectReference")
                         .Select(e => (string?)e.Attribute("Include"))
                         .Where(s => !string.IsNullOrWhiteSpace(s)))
            {
                var full = Path.GetFullPath(Path.Combine(dir, inc!.Replace('\\', '/')));
                if (!File.Exists(full))
                    missing.Add($"{Path.GetFileNameWithoutExtension(csproj)} -> {inc}");
            }
        }

        Assert.True(missing.Count == 0, "Dangling project references:\n  " + string.Join("\n  ", missing));
    }
}
