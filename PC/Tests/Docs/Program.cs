using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

var checks = 0;
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
}

// The docs live next to the sources, and the test does not know where the repo is at runtime. The
// compiler records this file's own path, which is the one anchor that keeps working if the repo
// moves or is cloned somewhere else.
var docs = Path.Combine(RepoRoot(), "PC", "docs");
var index = Path.Combine(docs, "README.md");

Assert(Directory.Exists(docs), "the docs folder exists");
Assert(File.Exists(index), "the docs folder has an index");

var documents = Directory.GetFiles(docs, "*.md", SearchOption.TopDirectoryOnly)
    .Where(file => !string.Equals(file, index, StringComparison.OrdinalIgnoreCase))
    .OrderBy(file => file, StringComparer.Ordinal)
    .ToArray();

Assert(documents.Length > 0, "there are documents to index");

var indexText = File.ReadAllText(index);

// 1. No orphans: every document is reachable from the index.
foreach (var document in documents)
{
    var name = Path.GetFileName(document);
    Assert(indexText.Contains($"({name})", StringComparison.Ordinal), $"the index links to {name}");
}

// 2. No dead links: every local link in every document resolves to a file that is really there.
foreach (var document in documents.Append(index))
{
    var text = File.ReadAllText(document);
    foreach (Match match in Regex.Matches(text, @"\]\(([^)\s]+)\)"))
    {
        var target = match.Groups[1].Value;

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith('#'))
        {
            continue;
        }

        var path = target.Split('#')[0];
        if (path.Length == 0) continue;

        var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(document)!, path));
        Assert(File.Exists(resolved),
            $"{Path.GetFileName(document)} links to a file that exists: {target}");
    }
}

// 3. The index must not point at documents that are gone (covered above) and the linked assets in
//    the docs folder must be referenced by something, so unused files get noticed.
var images = Directory.GetFiles(docs, "*.png", SearchOption.TopDirectoryOnly);
foreach (var image in images)
{
    var name = Path.GetFileName(image);
    var referenced = documents.Append(index).Any(file =>
        File.ReadAllText(file).Contains(name, StringComparison.Ordinal));
    Assert(referenced, $"the image {name} is referenced by some document");
}

Console.WriteLine($"All {checks} docs checks passed.");

static string RepoRoot([CallerFilePath] string thisFile = "")
{
    // PC/Tests/Docs/Program.cs -> PC -> repo root
    var docsFolder = Path.GetDirectoryName(thisFile)!;
    return Path.GetFullPath(Path.Combine(docsFolder, "..", "..", ".."));
}
