using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Configuration.Validation;
using Xunit;

namespace AgentCore.Application.Tests.Configuration;

/// <summary>
/// The document schema knows the <c>resolve</c> block on a filterable facet.
/// </summary>
public sealed class FacetResolveSchemaTests
{
    private const string Facets = """
        apiVersion: agentcore/v1
        name: plain
        providers:
          call:   { kind: telnyx-relay }
          speech:
            stt: { kind: telnyx-relay }
            tts: { kind: telnyx-relay }
          knowledge:
            kind: qdrant
            collection: kb
            scope:
              template: "facets.{key}"
              filterable:
                - key: lookup
                  description: The only value is model-numbers.
                - key: model
                  description: One machine and its year, as one tag.
                  resolve:
        """;

    [Fact]
    public void AFacetWithResolve_PassesTheSchema()
    {
        var document = ConfigurationLoader.ReadDocument(
            Facets + """

                        via: { key: lookup, value: model-numbers }
                        query: "<product> model number"
                        read: the Tag column of the row for the person's year
            """,
            ConfigurationFormat.Yaml);

        Assert.Empty(ConfigurationSchemaValidator.Evaluate(document));
    }

    [Fact]
    public void AResolveMissingItsRead_FailsTheSchema()
    {
        var document = ConfigurationLoader.ReadDocument(
            Facets + """

                        via: { key: lookup, value: model-numbers }
                        query: "<product> model number"
            """,
            ConfigurationFormat.Yaml);

        var failure = Assert.Single(ConfigurationSchemaValidator.Evaluate(document));
        Assert.Equal(ConfigurationCheck.DocumentSchema, failure.Check);
    }
}
