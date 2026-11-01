using Acornima;
using Acornima.Ast;

namespace ALN.Tests;

/// <summary>
/// The WebUI has no build step and no test runner: it is plain ES modules served as files. That means
/// a typo in an import specifier or a renamed export fails at runtime, in a browser, silently — which
/// is exactly the class of mistake nobody notices until the screen is blank. These tests parse the
/// modules with the same ECMAScript parser the scripting module already depends on, then check the
/// import graph against what each file actually exports.
/// </summary>
public sealed class WebUiAssetsTests
{
    private static readonly string WebRoot = ResolveWebRoot();

    private static string ResolveWebRoot()
    {
        var root = FindRepo() ?? throw new DirectoryNotFoundException("ALN.slnx was not found above the test binaries.");
        return Path.Combine(root, "src", "ALN.WebUi", "wwwroot");
    }

    [Fact]
    public void EveryModuleParsesAsAnEcmaScriptModule()
    {
        var modules = ModuleFiles().ToArray();
        Assert.NotEmpty(modules);

        var parser = new Parser(new ParserOptions { EcmaVersion = EcmaVersion.Latest });
        foreach (var path in modules)
        {
            var text = File.ReadAllText(path);
            try
            {
                parser.ParseModule(text);
            }
            catch (SyntaxErrorException error)
            {
                Assert.Fail($"{Relative(path)}:{error.LineNumber}:{error.Column} — {error.Message}");
            }
        }
    }

    [Fact]
    public void EveryRelativeImportPointsAtAFileThatExists()
    {
        foreach (var path in ModuleFiles())
        {
            foreach (var import in Imports(File.ReadAllText(path)))
            {
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, import.Source));
                Assert.True(File.Exists(target), $"{Relative(path)} imports '{import.Source}', which does not exist.");
                Assert.EndsWith(".js", import.Source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void EveryNamedImportIsExportedByItsTarget()
    {
        var exports = ModuleFiles().ToDictionary(path => path, path => Exports(File.ReadAllText(path)));

        foreach (var path in ModuleFiles())
        {
            foreach (var import in Imports(File.ReadAllText(path)))
            {
                if (import.Names.Length == 0) continue;
                var target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, import.Source));
                if (!exports.TryGetValue(target, out var available)) continue;
                foreach (var name in import.Names)
                {
                    Assert.True(available.Contains(name) || available.Contains("*"),
                        $"{Relative(path)} imports '{name}' from '{import.Source}', which does not export it. " +
                        $"Exported: {string.Join(", ", available.Order())}.");
                }
            }
        }
    }

    [Fact]
    public void IndexHtmlOnlyReferencesFilesThatExist()
    {
        var html = File.ReadAllText(Path.Combine(WebRoot, "index.html"));
        foreach (var reference in new[] { "./app.css", "./js/main.js" })
        {
            Assert.Contains(reference, html, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(WebRoot, reference.TrimStart('.', '/'))),
                $"index.html references '{reference}', which does not exist.");
        }
    }

    /// <summary>
    /// Every value the core sends is untrusted text. The helpers put it in textContent; an innerHTML
    /// assignment would be the one place where a signal detail could become markup. Cheap check, and
    /// it fails loudly if somebody reaches for the shortcut.
    /// </summary>
    [Fact]
    public void ModulesNeverAssignInnerHtml()
    {
        foreach (var path in ModuleFiles())
        {
            var text = File.ReadAllText(path);
            Assert.DoesNotContain(".innerHTML", text, StringComparison.Ordinal);
            Assert.DoesNotContain(".outerHTML", text, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> ModuleFiles() =>
        Directory.EnumerateFiles(Path.Combine(WebRoot, "js"), "*.js", SearchOption.AllDirectories).Order();

    private static string Relative(string path) =>
        Path.GetRelativePath(WebRoot, path).Replace('\\', '/');

    private static string? FindRepo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ALN.slnx")))
            directory = directory.Parent;
        return directory?.FullName;
    }

    private sealed record ImportEdge(string Source, string[] Names);

    /// <summary>
    /// Parsed by hand rather than with a full AST visitor: only top-level import statements matter
    /// here, and the shapes are few. Anything unusual makes this return nothing, which the syntax
    /// test above would already have caught as invalid JavaScript.
    /// </summary>
    private static IEnumerable<ImportEdge> Imports(string text)
    {
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text,
                     @"^\s*import\s+(?:([\s\S]*?)\s+from\s+)?['""](?<source>[^'""]+)['""]",
                     System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            var source = match.Groups["source"].Value;
            if (!source.StartsWith('.')) continue;
            var clause = match.Groups[1].Success ? match.Groups[1].Value : string.Empty;
            var braces = System.Text.RegularExpressions.Regex.Match(clause, @"\{(?<names>[^}]*)\}");
            var names = braces.Success
                ? braces.Groups["names"].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(part => part.Split(" as ")[0].Trim())
                    .Where(name => name.Length > 0)
                    .ToArray()
                : [];
            yield return new ImportEdge(source, names);
        }
    }

    private static HashSet<string> Exports(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        // export function|class|const|let|var name
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text,
                     @"^\s*export\s+(?:async\s+)?(?:function\*?|class|const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)",
                     System.Text.RegularExpressions.RegexOptions.Multiline))
            names.Add(match.Groups["name"].Value);

        // export { a, b as c }
        foreach (System.Text.RegularExpressions.Match match in
                 System.Text.RegularExpressions.Regex.Matches(text, @"^\s*export\s*\{(?<names>[^}]*)\}",
                     System.Text.RegularExpressions.RegexOptions.Multiline))
        {
            foreach (var part in match.Groups["names"].Value.Split(',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split(" as ");
                names.Add(pieces.Length > 1 ? pieces[1].Trim() : pieces[0].Trim());
            }
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*export\s+default\b",
                System.Text.RegularExpressions.RegexOptions.Multiline))
            names.Add("default");

        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\s*export\s+\*", System.Text.RegularExpressions.RegexOptions.Multiline))
            names.Add("*");

        return names;
    }
}
