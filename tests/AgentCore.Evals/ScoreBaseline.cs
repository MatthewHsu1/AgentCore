using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace AgentCore.Evals;

/// <summary>
/// The scores one execution recorded, and the scores it is allowed to fall to.
/// </summary>
/// <remarks>
/// <para>
/// The gate compares an average for each metric against the number a person recorded. A drop larger
/// than <see cref="Tolerance"/> fails the merge. A rise never fails, and a rise the team wants to keep
/// is copied into the file by hand: a baseline that moved on its own gates nothing.
/// </para>
/// <para>
/// A boolean metric averages as the share of rows that passed, so <c>Fault Code Accuracy</c> reads as
/// a rate from 0 through 1 and compares the same way a numeric metric does.
/// </para>
/// </remarks>
public sealed record ScoreBaseline
{
    private static readonly JsonSerializerOptions Options =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>The environment variable that names the baseline of the real set.</summary>
    public const string BaselineVariable = "AGENTCORE_EVAL_BASELINE";

    /// <summary>The baseline of the fixture suite, which travels with this repository.</summary>
    public const string FixtureBaselinePath = "golden/baseline.json";

    /// <summary>Gets the day a person recorded these scores.</summary>
    [JsonPropertyName("recordedAt")]
    public string RecordedAt { get; init; } = string.Empty;

    /// <summary>Gets how far a score may fall before the gate fails.</summary>
    [JsonPropertyName("tolerance")]
    public double Tolerance { get; init; }

    /// <summary>Gets the recorded average for each metric name.</summary>
    [JsonPropertyName("metrics")]
    public IReadOnlyDictionary<string, double> Metrics { get; init; } =
        new Dictionary<string, double>(StringComparer.Ordinal);

    /// <summary>Gets the path of the baseline of the real set, or <see langword="null"/> when none is named.</summary>
    public static string? DatasetBaselinePath
    {
        get
        {
            var path = Environment.GetEnvironmentVariable(BaselineVariable);
            return string.IsNullOrEmpty(path) ? null : path;
        }
    }

    /// <summary>Reads one baseline file.</summary>
    public static ScoreBaseline Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        return JsonSerializer.Deserialize<ScoreBaseline>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException($"{path} is not a baseline.");
    }

    /// <summary>Writes the measured averages beside the store, so a person can copy them in.</summary>
    /// <remarks>
    /// The gate never writes the baseline itself. It writes what it measured, and a person decides
    /// whether a drop is a regression or a new normal.
    /// </remarks>
    public static void WriteMeasured(string path, IReadOnlyDictionary<string, double> measured, double tolerance)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(measured);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new ScoreBaseline { Tolerance = tolerance, Metrics = measured },
                Options));
    }

    /// <summary>Averages every metric of one execution in the disk store.</summary>
    /// <param name="storageRoot">The directory the reporting configuration writes to.</param>
    /// <param name="executionName">The execution to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    /// The average for each metric name. A metric with no value in a row is left out of that average,
    /// because an inconclusive row measured nothing and must not read as a zero.
    /// </returns>
    public static async ValueTask<IReadOnlyDictionary<string, double>> MeasureAsync(
        string storageRoot,
        string executionName,
        CancellationToken cancellationToken = default)
    {
        DiskBasedResultStore store = new(storageRoot);

        Dictionary<string, (double Sum, int Count)> totals = new(StringComparer.Ordinal);

        await foreach (ScenarioRunResult result in store
            .ReadResultsAsync(executionName, cancellationToken: cancellationToken)
            .ConfigureAwait(false))
        {
            foreach (EvaluationMetric metric in result.EvaluationResult.Metrics.Values)
            {
                if (Value(metric) is not { } value)
                {
                    continue;
                }

                var running = totals.TryGetValue(metric.Name, out var found) ? found : (0.0, 0);
                totals[metric.Name] = (running.Item1 + value, running.Item2 + 1);
            }
        }

        return totals.ToDictionary(
            entry => entry.Key,
            entry => Math.Round(entry.Value.Sum / entry.Value.Count, 4),
            StringComparer.Ordinal);
    }

    private static double? Value(EvaluationMetric metric) => metric switch
    {
        NumericMetric numeric => numeric.Value,
        BooleanMetric boolean => boolean.Value is { } flag ? (flag ? 1.0 : 0.0) : null,
        _ => null,
    };
}
