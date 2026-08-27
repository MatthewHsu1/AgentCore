using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Infrastructure.Secrets;

namespace AgentCore.Evals;

/// <summary>
/// The settings both eval suites read.
/// </summary>
/// <remarks>
/// <para>
/// The store is on disk, and <see cref="StorageRoot"/> is the directory <c>dotnet aieval report</c>
/// reads.
/// </para>
/// <para>
/// The judge comes from <c>evaluation.judge</c> of the configuration document, and never from an
/// environment variable of its own. One document then tunes the online sampled path of T18 and this
/// offline gate together, and the key it reads is the one <c>providers.llm</c> already resolves.
/// </para>
/// </remarks>
public static class EvalHarness
{
    /// <summary>The directory that holds the results and the response cache.</summary>
    public const string StorageRoot = "eval-results";

    /// <summary>The environment variable that names the configuration document of the real set.</summary>
    public const string ConfigurationVariable = "AGENTCORE_EVAL_CONFIG";

    /// <summary>The execution name of the golden-set suite.</summary>
    public const string DatasetExecution = "golden-set";

    /// <summary>The environment variable naming the Qdrant this harness reaches for.</summary>
    /// <remarks>
    /// Read for presence only, as a third precondition beside <see cref="GoldenSet.DatasetVariable"/>
    /// and <see cref="ConfigurationVariable"/>: a deployer who sets the dataset and the configuration
    /// document with no Qdrant target running should see a skip that names what is still missing,
    /// rather than a raw connection failure the first time a row runs. <c>AgentCore.Infrastructure
    /// .Tests</c>' <c>QdrantServer</c> reads the same variable, but this is a second test project and
    /// gains nothing by referencing that one just to reuse a string, so the name is kept here as its
    /// own constant instead.
    /// </remarks>
    public const string QdrantVariable = "AGENTCORE_TEST_QDRANT";

    /// <summary>The chain a deployment resolves a secret through: the environment, then a mounted file.</summary>
    public static ChainedSecretResolver Secrets { get; } =
        new([new EnvironmentSecretResolver(), new FileSecretResolver()]);

    /// <summary>Gets the path of the configuration document, or <see langword="null"/> when none is named.</summary>
    public static string? ConfigurationPath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(ConfigurationVariable);
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>Gets whether a golden-set run is possible at all.</summary>
    public static bool DatasetIsConfigured => DatasetSkipReason is null;

    /// <summary>Gets why a golden-set run cannot happen, or <see langword="null"/> when it can.</summary>
    /// <remarks>
    /// Names every missing variable in one message, not just the first one this checks, so a deployer
    /// who sets one and forgets another learns which on the first red run instead of chasing them one
    /// at a time.
    /// </remarks>
    public static string? DatasetSkipReason
    {
        get
        {
            List<string> missing = [];
            if (GoldenSet.DatasetPath is null)
            {
                missing.Add(GoldenSet.DatasetVariable);
            }

            if (ConfigurationPath is null)
            {
                missing.Add(ConfigurationVariable);
            }

            if (Environment.GetEnvironmentVariable(QdrantVariable) is not { Length: > 0 })
            {
                missing.Add(QdrantVariable);
            }

            return missing.Count == 0 ? null : $"set {string.Join(" and ", missing)} to run the golden set.";
        }
    }

    /// <summary>Reads the configuration document of the real set.</summary>
    /// <exception cref="InvalidOperationException">No document is named.</exception>
    public static AgentCoreConfiguration LoadConfiguration()
    {
        var path = ConfigurationPath
            ?? throw new InvalidOperationException($"{ConfigurationVariable} names no configuration document.");

        return ConfigurationLoader.LoadFile(path);
    }
}
