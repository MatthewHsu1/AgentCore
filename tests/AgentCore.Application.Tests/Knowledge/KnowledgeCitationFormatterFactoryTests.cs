using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Knowledge;
using AgentCore.Domain.Knowledge;
using Xunit;

namespace AgentCore.Application.Tests.Knowledge;

/// <summary>
/// The seam that decides how a citation is worded.
/// </summary>
/// <remarks>
/// The wording used to be six lines inside <c>KnowledgeCardMapper</c> with no way past them. A
/// deployment whose documents are not manifest rows and page numbers had to accept "ct900-om, p.27"
/// shaped labels or turn citations off entirely.
/// </remarks>
public sealed class KnowledgeCitationFormatterFactoryTests
{
    private const string BaseYaml =
        """
        apiVersion: agentcore/v1
        name: citations
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: manuals
            fields: { body: body }
        agents:
          items:
            - { id: only, instructions: "hello" }
        """;

    [Fact]
    public void Resolve_ADocumentThatNamesNone_TakesTheShippedWording()
    {
        var formatter = KnowledgeCitationFormatterFactory.Resolve(Load(BaseYaml), registered: null);

        Assert.Equal(SourceLocatorCitationFormatter.FormatterName, formatter.Name);
    }

    [Fact]
    public void Resolve_ADocumentNamingAHostFormatter_TakesIt()
    {
        var configuration = Load(BaseYaml.Replace(
            "fields: { body: body }",
            "fields: { body: body }\n    citation: acme-handbook",
            StringComparison.Ordinal));

        var formatter = KnowledgeCitationFormatterFactory.Resolve(configuration, [new AcmeFormatter()]);

        Assert.IsType<AcmeFormatter>(formatter);
    }

    [Fact]
    public void Resolve_AHostFormatterUnderTheShippedName_ReplacesTheShippedOne()
    {
        // How a deployment changes the wording for every agent without touching the document.
        var formatter = KnowledgeCitationFormatterFactory.Resolve(
            Load(BaseYaml), [new ShadowingFormatter()]);

        Assert.IsType<ShadowingFormatter>(formatter);
    }

    [Fact]
    public void Resolve_ANameNobodyAnswersTo_FailsAndPointsAtTheField()
    {
        var configuration = Load(BaseYaml.Replace(
            "fields: { body: body }",
            "fields: { body: body }\n    citation: no-such-wording",
            StringComparison.Ordinal));

        var failure = Assert.Throws<ConfigurationLoadException>(
            () => KnowledgeCitationFormatterFactory.Resolve(configuration, registered: null));

        Assert.Equal("/providers/knowledge/citation", failure.Pointer);
        Assert.Contains("no-such-wording", failure.Message, StringComparison.Ordinal);
        Assert.Contains("UseKnowledgeCitationFormatters", failure.Message, StringComparison.Ordinal);
    }

    private static AgentCore.Application.Configuration.Schema.AgentCoreConfiguration Load(string yaml)
        => ConfigurationLoader.LoadYaml(yaml);

    private sealed class AcmeFormatter : IKnowledgeCitationFormatter
    {
        public string Name => "acme-handbook";

        public string? Format(KnowledgeCard card) => "handbook";
    }

    /// <summary>A host formatter answering to the shipped name.</summary>
    private sealed class ShadowingFormatter : IKnowledgeCitationFormatter
    {
        public string Name => SourceLocatorCitationFormatter.FormatterName;

        public string? Format(KnowledgeCard card) => "shadowed";
    }
}
