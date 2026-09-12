using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Scripting;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace AgentCore.Infrastructure.Scripting.Jint;

/// <summary>
/// Runs a model's script in a Jint engine that can reach nothing but <c>data</c>.
/// </summary>
/// <remarks>
/// A fresh engine per run: nothing one script defines survives into the next, and the budgets below
/// are per run. Jint never exposes the CLR unless asked to, and this class does not ask. The engine
/// is pinned to UTC because <c>new Date("2026-03-01").getMonth()</c> otherwise depends on where the
/// host machine sits, and a script that groups by month must not.
/// </remarks>
public sealed class JintScriptRunner : IScriptRunnerPort
{
    private static readonly TimeSpan TimeBudget = TimeSpan.FromSeconds(2);

    private const long MemoryBudgetBytes = 64L * 1024 * 1024;

    private const int RecursionBudget = 64;

    private const int StatementBudget = 500_000;

    /// <inheritdoc />
    public ValueTask<ScriptResult> RunAsync(ScriptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var code = Unfence(request.Code);

        try
        {
            var engine = new Engine(options => options
                .TimeoutInterval(TimeBudget)
                .LimitMemory(MemoryBudgetBytes)
                .LimitRecursion(RecursionBudget)
                .MaxStatements(StatementBudget)
                .LocalTimeZone(TimeZoneInfo.Utc)
                .CancellationToken(cancellationToken));

            engine.SetValue("__json", request.Data.ToJsonString());
            engine.Execute("var data = JSON.parse(__json); var __emitted;");
            if (request.Emit is { Length: > 0 } emit)
            {
                engine.Execute($"function {emit}(value) {{ __emitted = value; return value; }}");
            }

            return ValueTask.FromResult(ScriptResult.Returned(ToJson(engine, Evaluate(engine, code))));
        }
        catch (ExecutionCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return ValueTask.FromResult(ScriptResult.Failed(Describe(failure)));
        }
    }

    /// <summary>Runs the code as a body, then as an expression when the body answered nothing.</summary>
    private static JsValue Evaluate(Engine engine, string code)
    {
        JsValue returned;
        try
        {
            returned = engine.Evaluate($"(function () {{\n{code}\n}})()");
        }
        catch (JavaScriptException bodyFailure)
        {
            // A body that does not parse may be a bare function expression. When it is not, the
            // body's own error is the one the model wrote and the one it can act on.
            try
            {
                return engine.Evaluate($"({code})(data)");
            }
            catch (JavaScriptException)
            {
                throw bodyFailure;
            }
        }

        if (!returned.IsUndefined())
        {
            return returned;
        }

        var emitted = engine.GetValue("__emitted");
        if (!emitted.IsUndefined())
        {
            return emitted;
        }

        // A statement that is one function expression evaluates to the function and returns nothing.
        try
        {
            return engine.Evaluate($"({code})(data)");
        }
        catch (JavaScriptException)
        {
            return JsValue.Undefined;
        }
    }

    private static JsonNode? ToJson(Engine engine, JsValue value)
    {
        engine.SetValue("__result", value);
        var text = engine.Evaluate("JSON.stringify(__result)");
        return text.IsString() ? JsonNode.Parse(text.AsString()) : null;
    }

    private static string Describe(Exception failure)
        => failure switch
        {
            TimeoutException => $"the script ran longer than {TimeBudget.TotalSeconds:0} seconds and was stopped.",
            MemoryLimitExceededException => $"the script used more than {MemoryBudgetBytes / (1024 * 1024)} MB and was stopped.",
            RecursionDepthOverflowException => $"the script recursed deeper than {RecursionBudget} calls and was stopped.",
            StatementsCountOverflowException => $"the script ran more than {StatementBudget} statements and was stopped.",
            JavaScriptException script => script.Message,
            _ => failure.GetType().Name + ": " + failure.Message,
        };

    private static string Unfence(string code)
    {
        var text = code.Trim();
        if (!text.StartsWith("```", StringComparison.Ordinal))
        {
            return text;
        }

        var firstBreak = text.IndexOf('\n');
        text = firstBreak < 0 ? string.Empty : text[(firstBreak + 1)..];
        return text.EndsWith("```", StringComparison.Ordinal) ? text[..^3].TrimEnd() : text;
    }
}
