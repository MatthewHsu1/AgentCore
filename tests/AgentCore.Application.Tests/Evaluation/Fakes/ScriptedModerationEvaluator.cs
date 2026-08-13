using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using AgentCore.Application.Evaluation;

namespace AgentCore.Application.Tests.Evaluation.Fakes;

/// <summary>
/// A moderation evaluator that answers from a script, and never reaches a network.
/// </summary>
/// <remarks>
/// It is the moderation twin of the scripted chat client: the turn tests need an endpoint whose
/// verdict, timing, and failure they own. It records the text it was given, so a test proves which
/// words were moderated.
/// </remarks>
public sealed class ScriptedModerationEvaluator : IEvaluator
{
    /// <summary>The metric name the real adapter produces.</summary>
    public const string ContentSafetyMetricName = "Content Safety";

    private readonly List<string> _moderated = [];
    private readonly string[] _categories;
    private readonly bool _answers;
    private readonly Exception? _throws;
    private readonly TimeSpan _takes;

    private ScriptedModerationEvaluator(
        string[] categories,
        bool answers,
        Exception? throws = null,
        TimeSpan takes = default)
    {
        _categories = categories;
        _answers = answers;
        _throws = throws;
        _takes = takes;
    }

    /// <summary>Gets the texts a caller asked this evaluator about, oldest first.</summary>
    public IReadOnlyList<string> Moderated => _moderated;

    /// <inheritdoc />
    public IReadOnlyCollection<string> EvaluationMetricNames => [ContentSafetyMetricName];

    /// <summary>An endpoint that answers, and flags nothing.</summary>
    public static ScriptedModerationEvaluator Clean() => new([], answers: true);

    /// <summary>An endpoint that answers, and flags the named categories in the order given.</summary>
    public static ScriptedModerationEvaluator Flagging(params string[] categories)
        => new(categories, answers: true);

    /// <summary>An endpoint that did not answer, the way the real adapter reports a 500 or a bad body.</summary>
    /// <remarks>
    /// It returns an inconclusive metric carrying NO <see cref="ModerationVerdict"/>, which is exactly
    /// what <c>OpenAiModerationEvaluator</c> does for every vendor failure. The turn must go on.
    /// </remarks>
    public static ScriptedModerationEvaluator Unanswered() => new([], answers: false);

    /// <summary>An endpoint that throws rather than reporting a failure.</summary>
    public static ScriptedModerationEvaluator Throwing(Exception exception) => new([], answers: false, exception);

    /// <summary>An endpoint that takes longer than the caller allows.</summary>
    public static ScriptedModerationEvaluator Slow(TimeSpan takes) => new([], answers: true, throws: null, takes);

    /// <inheritdoc />
    public async ValueTask<EvaluationResult> EvaluateAsync(
        IEnumerable<ChatMessage> messages,
        ChatResponse modelResponse,
        ChatConfiguration? chatConfiguration = null,
        IEnumerable<EvaluationContext>? additionalContext = null,
        CancellationToken cancellationToken = default)
    {
        _moderated.Add(modelResponse.Text);

        if (_throws is not null)
        {
            throw _throws;
        }

        if (_takes > TimeSpan.Zero)
        {
            // A real deadline, so the caller's linked token is what ends the wait.
            await Task.Delay(_takes, cancellationToken).ConfigureAwait(false);
        }

        if (!_answers)
        {
            BooleanMetric unknown = new(ContentSafetyMetricName, value: null);
            unknown.AddDiagnostics(EvaluationDiagnostic.Error("the endpoint did not answer."));
            unknown.Interpretation = new EvaluationMetricInterpretation(
                EvaluationRating.Inconclusive,
                failed: false,
                reason: "the moderation endpoint did not answer, so the text is unchecked.");

            return new EvaluationResult(unknown);
        }

        var flagged = _categories.Length > 0;
        BooleanMetric metric = new(ContentSafetyMetricName, !flagged);
        metric.Interpretation = new EvaluationMetricInterpretation(
            flagged ? EvaluationRating.Unacceptable : EvaluationRating.Exceptional,
            failed: flagged);
        metric.AddOrUpdateContext(new ModerationVerdict(flagged, _categories));

        return new EvaluationResult(metric);
    }
}
