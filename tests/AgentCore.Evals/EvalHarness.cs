using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Evaluation;
using AgentCore.Application.Ports;
using AgentCore.Infrastructure.Knowledge;
using AgentCore.Infrastructure.Secrets;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace AgentCore.Evals;

/// <summary>
/// The settings and the ports both eval suites read.
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

    /// <summary>The execution name of the fixture suite.</summary>
    public const string FixtureExecution = "fixture";

    /// <summary>The execution name of the golden-set suite.</summary>
    public const string DatasetExecution = "golden-set";

    /// <summary>How many passages one search asks for.</summary>
    /// <remarks>
    /// The retrieval metric measures whether the answer file is inside this many hits. A deployment
    /// that reads a different number in production has to set the same number here, or the score
    /// describes a system nobody runs.
    /// </remarks>
    public const int SearchLimit = 5;

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

    /// <summary>Answers whether a golden-set run is possible at all.</summary>
    public static bool DatasetIsConfigured => GoldenSet.DatasetPath is not null && ConfigurationPath is not null;

    /// <summary>
    /// Builds the reporting configuration of the fixture suite.
    /// </summary>
    /// <remarks>
    /// It holds one evaluator, and that evaluator calls no model. So this configuration needs no key,
    /// no document, and no cache. It runs on every pull request, including one from a fork.
    /// </remarks>
    public static ReportingConfiguration ForFixture()
        => DiskBasedReportingConfiguration.Create(
            storageRootPath: StorageRoot,
            evaluators: [new DocumentRecallEvaluator()],
            enableResponseCaching: false,
            executionName: FixtureExecution);

    /// <summary>Opens the synthetic knowledge base the fixture rows name.</summary>
    /// <returns>The ranking port and the document port. One store answers both.</returns>
    public static (IKnowledgeRetrievalPort Search, IDocumentStorePort Documents) OpenFixtureKnowledge()
    {
        FileSystemKnowledgeStore store = new(GoldenSet.FixtureKnowledgeRoot);
        return (store, store);
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
