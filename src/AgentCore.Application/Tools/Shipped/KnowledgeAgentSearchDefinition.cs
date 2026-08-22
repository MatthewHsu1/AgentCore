using AgentCore.Application.Configuration.Schema;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools.Builtin;
using Microsoft.Extensions.AI;

namespace AgentCore.Application.Tools.Shipped;

/// <summary>
/// <c>knowledge.agent_search</c>: the agent that searches the knowledge base over several hops and
/// answers from what it read.
/// </summary>
/// <remarks>
/// <para>
/// It ships beside <c>knowledge.search</c> rather than replacing it. The plain one is a single call
/// that returns a ranked list, and an agent that already knows what it is looking for should keep
/// using it. This one is for a question that needs a second look — read a document, search again on
/// what it said — and the outer agent never sees those intermediate calls. That isolation is the
/// whole reason it is an agent and not a bigger function.
/// </para>
/// <para>
/// <b>It must not go on a live call yet.</b> Several rounds of inner work is several seconds of
/// silence, and nothing in the codebase fills dead air on a telephone line. The design puts dead air
/// out of scope and makes it a blocker on shipping this to a voice document. Text is fine.
/// </para>
/// </remarks>
internal sealed class KnowledgeAgentSearchDefinition : IShippedAgentDefinition
{
    /// <inheritdoc />
    public string Name => BuiltinToolNames.KnowledgeAgentSearch;

    /// <inheritdoc />
    /// <remarks>
    /// The second sentence is the only place it can be said. <c>kind: builtin</c> forbids
    /// <c>parameters:</c>, and the one argument <c>AsAIFunction()</c> generates carries the
    /// framework's own wording, so a document has no lever on it. Without the clause a terse
    /// request loses the detail the search needed.
    /// </remarks>
    public string DefaultDescription
        => "Search the knowledge base and answer one question from it. Whoever searches cannot see "
           + "the conversation, so put everything the question depends on into the request.";

    /// <inheritdoc />
    public string Instructions => SearchVocabulary.Text;

    /// <inheritdoc />
    /// <remarks>
    /// Enough for search, read, search, read, and an answer. Section 8.7 budgets 40 rounds for the
    /// calling agent and this whole tool is one of them, so the cost of a high cap lands on the
    /// caller's patience rather than on that budget. A document that knows its knowledge base is
    /// shallow lowers it with <c>maxRounds:</c>.
    /// </remarks>
    public int DefaultMaxRounds => 6;

    /// <inheritdoc />
    public IReadOnlyList<AITool> InnerTools(ToolConfiguration tool, BuiltinToolPorts ports)
        => KnowledgeAgentTools.Build(ports);

    /// <inheritdoc />
    /// <remarks>
    /// It reads both halves of the knowledge base, and <see cref="ShippedAgentBuilder"/> takes one
    /// name, so the ranking half is reported first. A host that binds neither fixes the first and
    /// meets the second on the next boot. <see cref="KnowledgeAgentTools.Build"/> would throw for
    /// either anyway; naming them here is what makes the message say which agent wanted it.
    /// </remarks>
    public string? MissingPort(BuiltinToolPorts ports)
        => ports.Retrieval is null ? nameof(IKnowledgeRetrievalPort)
            : ports.Documents is null ? nameof(IDocumentStorePort)
            : null;
}
