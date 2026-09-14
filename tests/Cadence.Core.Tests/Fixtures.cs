using System.Text.Json;

namespace Cadence.Core.Tests;

/// <summary>
/// Loads the golden fixtures in <c>fixtures/</c>.
/// </summary>
/// <remarks>
/// Every fixture is synthetic. Real captures would embed live OAuth tokens in the repository, and
/// the parsers care about shape rather than about any particular secret.
/// </remarks>
internal static class Fixtures
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string ReadText(string name) => File.ReadAllText(Path.Combine(Root, name));

    public static JsonDocument Load(string name) => JsonDocument.Parse(ReadText(name));

    public static JsonElement Root_(string name) => Load(name).RootElement.Clone();
}
