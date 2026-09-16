using System.Text.Json;
using AgentCore.Infrastructure.Calls.Postgres;
using AgentCore.Infrastructure.Tests.Database.Postgres;
using Xunit;

namespace AgentCore.Infrastructure.Tests.Calls.Postgres;

/// <summary>The continuation map beside the calls: one envelope per opaque id.</summary>
public sealed class ContinuationStoreTests : PostgresDatabaseTest
{
    /// <inheritdoc />
    protected override bool Migrated => true;

    [PostgresFact]
    public async Task Migration_Applied_MakesTheContinuationTable()
    {
        // Act
        var tables = await ScalarAsync<long>(
            """
            SELECT count(*) FROM information_schema.tables
             WHERE table_schema = 'agentcore' AND table_name = 'response_continuation'
            """);

        // Assert
        Assert.Equal(1, tables);
    }

    [PostgresFact]
    public async Task RoundTrip_SaveGetDelete()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);
        using var document = JsonDocument.Parse("""{ "callId": "c1", "state": {} }""");

        // Act
        await store.SaveContinuationAsync("conv_1", document.RootElement, Token);
        var found = await store.GetContinuationAsync("conv_1", Token);

        // Assert
        Assert.NotNull(found);
        Assert.True(JsonElement.DeepEquals(document.RootElement, found.Value));

        // Act
        using var replacement = JsonDocument.Parse("{}");
        await store.SaveContinuationAsync("conv_1", replacement.RootElement, Token);
        var replaced = await store.GetContinuationAsync("conv_1", Token);

        // Assert
        Assert.Equal("{}", replaced?.GetRawText());

        // Act
        await store.DeleteContinuationAsync("conv_1", Token);

        // Assert
        Assert.Null(await store.GetContinuationAsync("conv_1", Token));
    }

    [PostgresFact]
    public async Task GetContinuation_UnknownId_AnswersNull()
    {
        // Arrange
        PostgresCallStore store = new(DataSource);

        // Act
        var found = await store.GetContinuationAsync("conv_missing", Token);

        // Assert
        Assert.Null(found);
    }
}
