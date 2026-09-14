using System.Text.RegularExpressions;

namespace Cadence.Core.Tests;

/// <summary>
/// Fails the build if a real credential, or a file that holds credentials, gets into the repository.
/// </summary>
/// <remarks>
/// Cadence reads other tools' sign-ins, so its tests handle token-shaped strings all the time. The
/// fixtures are deliberately short or labelled FAKE; live tokens are long, and that length is what
/// these patterns key on. A pasted real token is caught, a fixture is not. Findings name the file
/// and the kind of secret, never the value, so the test output cannot leak what it found.
/// </remarks>
public class RepositoryHygieneTests
{
    private static readonly string[] SkippedDirectories = [".git", ".vs", "bin", "obj", "dist", "node_modules"];

    private static readonly string[] BinaryExtensions =
        [".ico", ".png", ".jpg", ".gif", ".dll", ".exe", ".pdb", ".zip", ".nupkg", ".db"];

    private static readonly string[] CredentialFileNames = [".credentials.json", "auth.json", "oauth_creds.json", ".env"];

    private static readonly string[] CredentialExtensions = [".pem", ".pfx", ".p12", ".key"];

    private static readonly (string Kind, Regex Pattern)[] Secrets =
    [
        ("an Anthropic key or OAuth token", new Regex(@"sk-ant-[a-z]+[0-9]{2}-[A-Za-z0-9_\-]{80,}")),
        ("an OpenAI key", new Regex(@"\bsk-(proj|svcacct|admin)-[A-Za-z0-9_\-]{40,}|\bsk-[A-Za-z0-9]{48}\b")),
        ("a Google API key", new Regex(@"AIza[0-9A-Za-z_\-]{35}")),
        ("a Google OAuth token", new Regex(@"ya29\.[0-9A-Za-z_\-]{60,}")),
        ("a GitHub token", new Regex(@"\bgh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{60,}")),
        ("a signed JSON Web Token", new Regex(@"eyJ[A-Za-z0-9_\-]{30,}\.eyJ[A-Za-z0-9_\-]{30,}\.[A-Za-z0-9_\-]{30,}")),
        ("a private key", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
    ];

    [Fact]
    public void NoCredentialsAreCheckedIn()
    {
        var root = RepositoryRoot();
        var findings = new List<string>();

        foreach (var file in SourceFiles(root))
        {
            var relative = Path.GetRelativePath(root, file);

            if (CredentialFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase) ||
                CredentialExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                findings.Add($"{relative} is a credential file");
                continue;
            }

            if (BinaryExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) continue;
            if (new FileInfo(file).Length > 5_000_000) continue;

            var text = File.ReadAllText(file);
            foreach (var (kind, pattern) in Secrets)
            {
                if (pattern.IsMatch(text)) findings.Add($"{relative} contains what looks like {kind}");
            }
        }

        Assert.Empty(findings);
    }

    [Fact]
    public void TheScanRecognisesARealLookingToken()
    {
        // Guards the guard: a pattern that silently matched nothing would make the test above pass forever.
        var live = "sk-ant-oat01-" + new string('x', 95);

        Assert.Contains(Secrets, s => s.Pattern.IsMatch(live));
        Assert.DoesNotContain(Secrets, s => s.Pattern.IsMatch("sk-ant-oat01-FAKE-TOKEN-FOR-TESTS"));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Cadence.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not find the repository root (no Cadence.slnx above the test output).");
    }

    private static IEnumerable<string> SourceFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) pending.Push(sub);
            }

            foreach (var file in Directory.EnumerateFiles(dir)) yield return file;
        }
    }
}
