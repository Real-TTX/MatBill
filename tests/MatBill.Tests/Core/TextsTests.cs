using System.Text.RegularExpressions;
using MatBill.Localization;

namespace MatBill.Tests.Core;

/// <summary>The interface texts: every key in both languages, in the file of its area, and every key the code asks for exists.</summary>
public sealed partial class TextsTests
{
    private static readonly string Source = Path.Combine(RepoRoot(), "src", "MatBill");

    // Documents for customers and the tax office stay German on purpose (InvoiceDocument, PDF, e-invoice, mail templates, export, sample data),
    // and so do the German templates of the configuration and the German words a bank statement is read by.
    private static readonly string[] GermanOnPurpose =
    [
        "Services/InvoicePdf.cs", "Services/InvoiceDocument.cs", "Services/DocumentTextPresets.cs", "Services/EInvoice.cs", "Services/ExportService.cs", "Services/DemoData.cs",
        "Services/MailService.cs", "Configuration/AppConfig.cs", "Pages/Shared/_DocumentView.cshtml", "Pages/Shared/_DocumentLine.cshtml",
        "Pages/Shared/_DocumentEditor.cshtml", "Pages/Shared/SampleDocument.cs", "Data/Migrations/", "Resources/Texts/", "wwwroot/ui/",
    ];

    [Fact]
    public void Every_text_has_english_and_german()
    {
        var missing = TextCatalog.Files()
            .SelectMany(e => Languages.Codes.Where(c => !e.Texts.TryGetValue(c, out var text) || string.IsNullOrWhiteSpace(text)).Select(c => $"{e.Key} ({c})"))
            .ToList();
        Assert.True(missing.Count == 0, "Texts without a translation: " + string.Join(", ", missing));
    }

    [Fact]
    public void A_key_lives_in_the_file_of_its_area_and_only_there()
    {
        var entries = TextCatalog.Files().ToList();
        var duplicates = entries.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "Keys in more than one file: " + string.Join(", ", duplicates));

        var misplaced = entries.Where(e => !e.Key.StartsWith(Area(e.File) + ".", StringComparison.Ordinal)).Select(e => $"{e.Key} in {Area(e.File)}.json").ToList();
        Assert.True(misplaced.Count == 0, "Keys in the wrong file (the part before the first dot names the file): " + string.Join(", ", misplaced));
    }

    [Fact]
    public void The_placeholders_are_the_same_in_both_languages()
    {
        var different = TextCatalog.Files()
            .Where(e => e.Texts.Count == Languages.Codes.Count)
            .Where(e => e.Texts.Values.Select(t => string.Join(",", Placeholder().Matches(t).Select(m => m.Value).Distinct().Order(StringComparer.Ordinal))).Distinct().Count() > 1)
            .Select(e => e.Key)
            .ToList();
        Assert.True(different.Count == 0, "Placeholders differ between the languages: " + string.Join(", ", different));
    }

    [Fact]
    public void Every_key_the_code_asks_for_exists()
    {
        var known = TextCatalog.Entries.Keys.ToHashSet(StringComparer.Ordinal);
        var unknown = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in Files("*.cs", "*.cshtml", "*.js"))
        {
            var text = string.Join('\n', File.ReadAllLines(file).Select(StripComments));
            var regex = file.EndsWith(".js", StringComparison.Ordinal) ? ScriptKey() : CodeKey();
            foreach (Match m in regex.Matches(text))
            {
                var key = m.Groups["key"].Value;
                if (!known.Contains(key))
                {
                    unknown.Add($"{key} ({Relative(file)})");
                }
            }
        }

        Assert.True(unknown.Count == 0, "Keys that are not in Resources/Texts: " + string.Join(", ", unknown));
    }

    [Fact]
    public void The_interface_has_no_german_left_in_the_code()
    {
        var german = new List<string>();
        foreach (var file in Files("*.cs", "*.cshtml", "*.js"))
        {
            var relative = Relative(file);
            if (GermanOnPurpose.Any(p => relative.StartsWith(p, StringComparison.Ordinal)))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (Umlaut().IsMatch(StripComments(lines[i])))
                {
                    german.Add($"{relative}:{i + 1}");
                }
            }
        }

        Assert.True(german.Count == 0, "German text outside of Resources/Texts (move it into a text file): " + string.Join(", ", german.Take(60)) + (german.Count > 60 ? $" … ({german.Count})" : ""));
    }

    private static string StripComments(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal) || trimmed.StartsWith('*') || trimmed.StartsWith("/*", StringComparison.Ordinal) || trimmed.StartsWith("@*", StringComparison.Ordinal))
        {
            return "";
        }

        var comment = line.IndexOf(" // ", StringComparison.Ordinal);
        return comment >= 0 ? line[..comment] : line;
    }

    private static string Area(string resource)
    {
        var name = resource[..^".json".Length];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    private static IEnumerable<string> Files(params string[] patterns)
        => patterns.SelectMany(p => Directory.EnumerateFiles(Source, p, SearchOption.AllDirectories))
            .Where(f => !Relative(f).StartsWith("bin/", StringComparison.Ordinal) && !Relative(f).StartsWith("obj/", StringComparison.Ordinal));

    private static string Relative(string file) => Path.GetRelativePath(Source, file).Replace('\\', '/');

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MatBill.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("MatBill.slnx not found above " + AppContext.BaseDirectory);
    }

    // L["Key"], L["Key", …], Text.T("Key"…), Text.L["Key"], [Display(Name = "Key")], ErrorMessage = "Key": a key is Area.Part[.Part…].
    [GeneratedRegex("""(?:\bL\[|Text\.T\(|Text\.L\[|Display\(Name\s*=\s*|ErrorMessage\s*=\s*)"(?<key>[A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)"[\],)]""")]
    private static partial Regex CodeKey();

    [GeneratedRegex("""\bt\('(?<key>Js(?:\.[A-Za-z0-9]+)+)'""")]
    private static partial Regex ScriptKey();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex("[äöüÄÖÜß]")]
    private static partial Regex Umlaut();
}
