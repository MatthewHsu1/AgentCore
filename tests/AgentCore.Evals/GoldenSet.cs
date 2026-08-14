using System.Text.Json;
using Xunit;

namespace AgentCore.Evals;

/// <summary>
/// Reads a golden set from one JSON Lines file.
/// </summary>
/// <remarks>
/// <para>
/// JSON Lines, and not one JSON array, because a diff of one changed row then shows one changed line.
/// A golden set is reviewed by a person, so the diff is part of the format.
/// </para>
/// <para>
/// Two sets exist, and they answer two questions. The fixture travels with this repository and proves
/// the harness works. The real set belongs to a deployment, holds its own knowledge base, and proves
/// that retrieval and the reply still work. The fixture never replaces the real set, and the real set
/// never replaces the fixture.
/// </para>
/// </remarks>
public static class GoldenSet
{
    /// <summary>The environment variable that names the real set.</summary>
    /// <remarks>
    /// A deployment points this at its own file. With the variable unset, the golden-set suites skip
    /// and only the fixture suite runs, so a contributor with no data still gets a green build.
    /// </remarks>
    public const string DatasetVariable = "AGENTCORE_EVAL_DATASET";

    /// <summary>The synthetic set that travels with this repository.</summary>
    public const string FixturePath = "golden/fixture.jsonl";

    /// <summary>The synthetic knowledge base the fixture rows name.</summary>
    public const string FixtureKnowledgeRoot = "golden/kb";

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Gets the path of the real set, or <see langword="null"/> when none is named.</summary>
    public static string? DatasetPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(DatasetVariable);
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>Reads one JSON Lines file.</summary>
    /// <param name="path">The file.</param>
    /// <returns>The rows, in file order.</returns>
    /// <exception cref="InvalidDataException">One line is not a row.</exception>
    public static IReadOnlyList<GoldenRow> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        List<GoldenRow> rows = [];
        var number = 0;
        foreach (var line in File.ReadLines(path))
        {
            number++;
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            rows.Add(
                JsonSerializer.Deserialize<GoldenRow>(line, Options)
                ?? throw new InvalidDataException($"{path} line {number} is not a row."));
        }

        return rows;
    }

    /// <summary>Feeds the fixture rows to a theory. It never skips.</summary>
    public static TheoryData<GoldenRow> Fixture() => Feed(Load(FixturePath));

    /// <summary>
    /// Feeds the rows of the real set to a theory, or nothing when no set is named.
    /// </summary>
    /// <remarks>
    /// An empty feed leaves the theory with no case to run, which is what a repository that holds no
    /// data should report. It is not a failure.
    /// </remarks>
    public static TheoryData<GoldenRow> Dataset()
        => DatasetPath is { } path ? Feed(Load(path)) : [];

    private static TheoryData<GoldenRow> Feed(IReadOnlyList<GoldenRow> rows)
    {
        TheoryData<GoldenRow> data = [];
        foreach (var row in rows)
        {
            data.Add(row);
        }

        return data;
    }
}
