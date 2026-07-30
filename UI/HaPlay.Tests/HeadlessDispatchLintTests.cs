using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace HaPlay.Tests;

/// <summary>
/// Guards the two ways a headless UI-thread test can silently assert NOTHING. Both shipped for
/// months and were only found in 2026-07 by accident, across 21 sites in 21 files - a whole class of
/// test that passed no matter what the code under test did (proved by asserting an impossible value
/// and watching it pass). A lint is the only thing that stops the shape coming back, because the
/// broken and the correct call look almost identical at the call site.
/// <list type="number">
/// <item><b>Discarded result.</b> <c>HeadlessUnitTestSession.Dispatch</c> hands back a Task that
/// carries the body's exceptions. Drop it and every <c>Assert</c> inside the body is thrown away.</item>
/// <item><b>The async-lambda overload trap.</b> <c>Dispatch(async () =&gt; …)</c> binds to
/// <c>Dispatch&lt;TResult&gt;(Func&lt;TResult&gt;)</c> with <c>TResult = Task</c>: it runs the lambda
/// only up to its first <c>await</c> and returns the inner task UN-awaited, so everything after that
/// await runs (or fails) after the test has already passed. Use
/// <see cref="HeadlessDispatchExtensions.DispatchAsync"/> for async bodies.</item>
/// </list>
/// This lints the DEFECT rather than banning the API: a wrapper could be introduced and misused just
/// as easily, and the rule below stays true whichever helper a test reaches for.
/// </summary>
public sealed class HeadlessDispatchLintTests(ITestOutputHelper output)
{
    private const string ThisFileName = "HeadlessDispatchLintTests.cs";

    /// <summary>Every entry point that hands back a Task carrying a dispatched body's assertions. All three
    /// are linted, not just the raw API: <c>DispatchGuarded</c> is the sanctioned synchronous wrapper (it
    /// adds the app-init-race retry, see <see cref="HeadlessDispatchExtensions.IsHeadlessAppInitRace"/>) and
    /// is exactly as droppable as <c>Dispatch</c> was, and <c>DispatchAsync</c> - which had never been
    /// scanned at all, because `.Dispatch(` does not match `.DispatchAsync(` - is too.</summary>
    private static readonly string[] DispatchCalls = [".Dispatch(", ".DispatchGuarded(", ".DispatchAsync("];

    /// <summary>The subset that binds an <c>async () =&gt; …</c> lambda to <c>Func&lt;TResult&gt;</c> with
    /// <c>TResult = Task</c> - the overload trap. <c>DispatchAsync</c> is the cure and so is absent.</summary>
    private static readonly string[] AsyncLambdaTraps = [".Dispatch", ".DispatchGuarded"];

    /// <summary>Consumed AFTER the call - the returned Task is blocked on or unwrapped. Matched
    /// against the text immediately following the call's OWN closing paren (see
    /// <see cref="CallEnd"/>), never a fixed-width window.</summary>
    private static readonly string[] TrailingConsumption = [".GetAwaiter(", ".Wait(", ".Result"];

    /// <summary>Keywords that can precede an expression but are never part of a member-access chain,
    /// so the backward walk must stop at them rather than eat them as identifiers.</summary>
    private static readonly HashSet<string> StopWords =
        new(StringComparer.Ordinal) { "await", "return", "new", "throw", "yield" };

    [Fact]
    public void HeadlessDispatchResults_AreNeverDiscarded()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(TestSourceDir(), "*.cs", SearchOption.AllDirectories))
        {
            // This scanner necessarily contains the pattern it looks for, so it matches itself.
            if (Path.GetFileName(file) == ThisFileName)
                continue;

            // Blank comments and string literals first, preserving offsets. Without this a `.Dispatch(`
            // mentioned in prose (HeadlessDispatchExtensions documents the very trap this lints) reads
            // as a call site, and a comment sitting between `=>` and the expression hides the arrow
            // from the backward walk.
            var text = BlankCommentsAndStrings(File.ReadAllText(file));
            if (!Array.Exists(DispatchCalls, m => text.Contains(m, StringComparison.Ordinal)))
                continue;

            foreach (var marker in DispatchCalls)
            {
                for (var call = text.IndexOf(marker, StringComparison.Ordinal);
                     call >= 0;
                     call = text.IndexOf(marker, call + 1, StringComparison.Ordinal))
                {
                    var reason = ClassifyDispatch(text, call, marker);
                    if (reason is not null)
                        offenders.Add($"{Path.GetFileName(file)}: {reason} -> {Squash(Context(text, call))}");
                }
            }

            // The async-lambda overload trap. DispatchAsync is the sanctioned form and is excluded by
            // being absent from AsyncLambdaTraps; the `(?<!Async)` lookbehind keeps its own call sites
            // from matching at all.
            foreach (Match m in Regex.Matches(text, @"(?<!Async)\(\s*async\s"))
            {
                var before = text[..m.Index];
                var trap = Array.Find(AsyncLambdaTraps, t => before.EndsWith(t, StringComparison.Ordinal));
                if (trap is not null)
                    offenders.Add(
                        $"{Path.GetFileName(file)}: async lambda passed to {trap.TrimStart('.')} "
                        + "- use DispatchAsync");
            }
        }

        output.WriteLine($"headless dispatch offenders: {offenders.Count}");
        Assert.True(
            offenders.Count == 0,
            "Headless UI dispatches must surface their body's assertions. Await the returned Task "
            + "(or GetAwaiter().GetResult() in a sync test), and use DispatchAsync for async bodies. "
            + "Offenders:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>Null when the call's result is consumed; otherwise why it is not.
    /// <para>Works by walking BACK from the call over the receiver chain to whatever precedes the
    /// expression - splitting the file on <c>;</c> does not survive a lambda body with statements in
    /// it, which is the shape every one of these tests has.</para>
    /// Deliberately errs toward flagging: a false positive costs one explicit <c>await</c>, a false
    /// negative costs a test that silently asserts nothing.</summary>
    private static string? ClassifyDispatch(string text, int call, string marker)
    {
        // Consumption must sit at the END of THIS call - `.GetAwaiter()`, `.Wait(…)` or `.Result`
        // immediately after its own closing paren. The original fixed 200-character window scanned
        // straight INTO the dispatched body, where a perfectly ordinary
        // `Dispatch(() => { svc.DoAsync().GetAwaiter().GetResult(); Assert…; })` exempted the whole
        // discarded call - the exact vacuous shape this lint exists to catch. Re-probe before
        // loosening: a body containing `.Result` was silently passed by the windowed form.
        var callEnd = CallEnd(text, call, marker);
        if (callEnd > 0)
        {
            var after = text.AsSpan(callEnd).TrimStart();
            foreach (var consumption in TrailingConsumption)
            {
                if (after.StartsWith(consumption, StringComparison.Ordinal))
                    return null;
            }
        }

        var exprStart = ExpressionStart(text, call);
        var lead = text[..exprStart].TrimEnd();

        if (lead.EndsWith("_ =", StringComparison.Ordinal) || lead.EndsWith("_=", StringComparison.Ordinal))
            return "result discarded via `_ =`";
        if (EndsWithWord(lead, "await") || EndsWithWord(lead, "return"))
            return null;
        // Passed straight into another call - that callee owns it.
        if (lead.EndsWith('(') || lead.EndsWith(','))
            return null;
        if (lead.EndsWith("=>", StringComparison.Ordinal))
            return DeclaresTaskReturn(lead)
                ? null
                : "expression-bodied member does not return the Task - assertions are discarded";
        // `var t = …` / `Task t = …`, but not `==`/`!=`/`<=`/`>=`. Storing the Task is only
        // consumption if something later actually touches it: `var t = session.Dispatch(…);` with no
        // later `await t` throws the assertions away exactly like a bare statement does, just spelled
        // with a variable.
        if (lead.EndsWith('=') && lead.Length >= 2 && !"=!<>+-*/&|^".Contains(lead[^2]))
            return AssignedTargetIsUsedLater(text, lead, call)
                ? null
                : "assigned to a variable that is never awaited - assertions are discarded";
        return "result never consumed";
    }

    /// <summary>True when the identifier an assignment writes to is mentioned again AFTER the call -
    /// enough to clear `await t` / `tasks.Add(t)` / `Task.WhenAll(t)` without modelling any of them,
    /// while still catching the dead store. Anything that is not a plain `name =` target (a member
    /// path, an indexer, a deconstruction) is left alone rather than guessed at.</summary>
    private static bool AssignedTargetIsUsedLater(string text, string lead, int call)
    {
        var target = lead[..^1].TrimEnd();
        var start = target.Length;
        while (start > 0 && (char.IsLetterOrDigit(target[start - 1]) || target[start - 1] == '_'))
            start--;
        var name = target[start..];
        if (name.Length == 0 || (start > 0 && target[start - 1] is '.' or ']' or ')'))
            return true;
        return Regex.IsMatch(text[call..], $@"(?<![\w.]){Regex.Escape(name)}\b");
    }

    /// <summary>Index just past the matching <c>)</c> of the dispatch call at <paramref name="call"/>, or
    /// −1 when unbalanced. Scanning starts on the marker's own <c>(</c> - hence <c>marker.Length - 1</c>,
    /// which is what keeps this correct for the longer names too. Literals are already blanked, so a paren
    /// inside a string cannot skew the count.</summary>
    private static int CallEnd(string text, int call, string marker)
    {
        var depth = 0;
        for (var i = call + marker.Length - 1; i < text.Length; i++)
        {
            if (text[i] == '(')
                depth++;
            else if (text[i] == ')' && --depth == 0)
                return i + 1;
        }
        return -1;
    }

    /// <summary>Start of the expression whose tail is the <c>.Dispatch(</c> at <paramref name="call"/>:
    /// walks left over the receiver chain (identifiers, dots, and balanced <c>()</c>/<c>[]</c>/<c>&lt;&gt;</c>
    /// generic arguments), so `HeadlessUnitTestSession.GetOrStartForAssembly(typeof(X).Assembly)` is
    /// crossed in one go and we land on whatever really precedes the statement.</summary>
    private static int ExpressionStart(string text, int call)
    {
        var i = call - 1;
        while (i >= 0)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c) || c is '.' or '@')
            {
                i--;
                continue;
            }

            if (char.IsLetterOrDigit(c) || c == '_')
            {
                // Cross a whole identifier at once so a KEYWORD is never mistaken for part of the
                // receiver chain: walking `await` character-by-character consumed it, and the walk
                // then ran on to the enclosing `{`, making a correctly awaited call look unconsumed.
                var j = i;
                while (j >= 0 && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                    j--;
                var word = text[(j + 1)..(i + 1)];
                if (StopWords.Contains(word))
                    break;
                i = j;
                continue;
            }

            // The '>' of a lambda arrow is NOT a generic close bracket - treating it as one sent the
            // walk scanning for a '<' that never comes, ran it off the front of the file, and made
            // every correctly-returned expression body look unconsumed.
            if (c == '>' && i > 0 && text[i - 1] == '=')
                break;

            if (c is ')' or ']' or '>')
            {
                var close = c;
                var open = close switch { ')' => '(', ']' => '[', _ => '<' };
                var depth = 0;
                while (i >= 0)
                {
                    if (text[i] == close) depth++;
                    else if (text[i] == open && --depth == 0) { i--; break; }
                    i--;
                }
                continue;
            }

            break;
        }

        return i + 1;
    }

    /// <summary>True when the member signature this expression body belongs to returns a Task. Looks
    /// back only as far as the previous statement/block boundary so an unrelated `Task` elsewhere in
    /// the file cannot vouch for a `void` member.</summary>
    private static bool DeclaresTaskReturn(string lead)
    {
        var boundary = lead.LastIndexOfAny([';', '{', '}']);
        var signature = boundary >= 0 ? lead[(boundary + 1)..] : lead;
        return Regex.IsMatch(signature, @"\bTask\b");
    }

    private static bool EndsWithWord(string text, string word) =>
        text.EndsWith(word, StringComparison.Ordinal)
        && (text.Length == word.Length || !char.IsLetterOrDigit(text[^(word.Length + 1)]));

    /// <summary>Replaces every comment and string/char literal with spaces, keeping the string the
    /// same length so all indices stay valid. Handles line and block comments, verbatim and raw
    /// strings well enough for test sources.</summary>
    private static string BlankCommentsAndStrings(string text)
    {
        var buffer = text.ToCharArray();
        for (var i = 0; i < buffer.Length; i++)
        {
            var c = buffer[i];
            if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '/')
            {
                while (i < buffer.Length && buffer[i] != '\n') buffer[i++] = ' ';
            }
            else if (c == '/' && i + 1 < buffer.Length && buffer[i + 1] == '*')
            {
                while (i < buffer.Length
                       && !(buffer[i] == '*' && i + 1 < buffer.Length && buffer[i + 1] == '/'))
                    Blank(buffer, ref i);
                if (i < buffer.Length) buffer[i] = ' ';
                if (i + 1 < buffer.Length) buffer[++i] = ' ';
            }
            else if (c is '"' or '\'')
            {
                var quote = c;
                var verbatim = i > 0 && buffer[i - 1] == '@';
                buffer[i++] = ' ';
                while (i < buffer.Length)
                {
                    if (!verbatim && buffer[i] == '\\' && i + 1 < buffer.Length)
                    {
                        buffer[i++] = ' ';
                        buffer[i++] = ' ';
                        continue;
                    }
                    var end = buffer[i] == quote;
                    Blank(buffer, ref i);
                    if (end) break;
                }
                i--;
            }
        }

        return new string(buffer);
    }

    /// <summary>Blanks one character, keeping newlines so line structure survives.</summary>
    private static void Blank(char[] buffer, ref int i)
    {
        if (buffer[i] != '\n' && buffer[i] != '\r') buffer[i] = ' ';
        i++;
    }

    /// <summary>The call plus a little context either side, for the failure message.</summary>
    private static string Context(string text, int call) =>
        text[Math.Max(0, call - 90)..Math.Min(text.Length, call + 60)];

    private static string Squash(string statement)
    {
        var flat = Regex.Replace(statement, @"\s+", " ").Trim();
        return flat.Length <= 120 ? flat : flat[..120] + "…";
    }

    private static string TestSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HaPlay.Tests.csproj")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
