using AgentCore.Application.Configuration.Parsing;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Providers;

/// <summary>
/// Picks the one adapter a <c>kind</c> in the document names, for any vendor seam.
/// </summary>
/// <remarks>
/// <para>
/// Every seam — chat clients, knowledge, moderation, telemetry, speech, and the call transport —
/// asked the same question and, before this type, each carried its own copy of the answer. The
/// copies had drifted: four failed when two adapters answered to one kind, and the chat client one
/// silently kept whichever the host listed last.
/// </para>
/// <para>
/// <b>Two adapters for one kind fail the start, in every seam.</b> A host that registers two
/// vendors for <c>openai</c> has a bug, and the alternative is a deployment that runs whichever
/// adapter happened to be listed last. A host that genuinely wants two behaviours gives them two
/// kinds in the document.
/// </para>
/// </remarks>
public static class VendorAdapterSelector
{
    /// <summary>Picks the adapter that serves one <c>kind</c>.</summary>
    /// <typeparam name="TAdapter">The adapter interface of one seam.</typeparam>
    /// <param name="kind">The <c>kind</c> the document wrote.</param>
    /// <param name="adapters">The vendors this host registered for that seam.</param>
    /// <param name="seam">What this seam calls itself, for the failure messages.</param>
    /// <returns>The one adapter serving <paramref name="kind"/>.</returns>
    /// <exception cref="ArgumentNullException">The adapters are <see langword="null"/>.</exception>
    /// <exception cref="ConfigurationLoadException">
    /// No adapter serves the kind, or two of them do.
    /// </exception>
    public static TAdapter Select<TAdapter>(
        string kind,
        IReadOnlyList<TAdapter> adapters,
        VendorSeam seam)
        where TAdapter : IVendorAdapter
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(adapters);

        // The kind is a vendor name, and a vendor name is written by a human. It matches without
        // regard to case, and every other name in the document stays ordinal.
        Dictionary<string, List<TAdapter>> byKind = new(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (!byKind.TryGetValue(adapter.Kind, out var same))
            {
                same = [];
                byKind[adapter.Kind] = same;
            }

            same.Add(adapter);
        }

        if (!byKind.TryGetValue(kind, out var matching))
        {
            throw Fail(
                seam,
                $"{seam.DocumentPath} is kind: {kind}, and this host registers "
                + Registered(byKind, seam));
        }

        if (matching.Count > 1)
        {
            throw Fail(
                seam,
                $"two adapters answer to the kind '{kind}', so {seam.DocumentPath} names two "
                + $"{seam.Plural}. Register one adapter for each kind.");
        }

        return matching[0];
    }

    /// <summary>Writes what the host does register, and the move that would fix the document.</summary>
    /// <remarks>
    /// A host that registered nothing has no kind to offer the reader instead, so that message names
    /// the call itself — the shape <c>AddAgentCoreAsync</c> already uses when it names
    /// <c>options.UseChatClients(...)</c> to a host that bound no chat client adapter. A host that
    /// registered some other vendor is told to add one for this kind, because the list it just read
    /// is the rest of the answer.
    /// </remarks>
    private static string Registered<TAdapter>(Dictionary<string, List<TAdapter>> byKind, VendorSeam seam)
        where TAdapter : IVendorAdapter
        => byKind.Count == 0
            ? $"no adapter. Call {seam.RegistrationHint}, or change the document."
            : string.Join(", ", byKind.Keys.Select(k => "'" + k + "'"))
                + ". Register an adapter for that kind, or change the document.";

    /// <summary>Builds the one exception every failure of this selector uses.</summary>
    private static ConfigurationLoadException Fail(VendorSeam seam, string message)
        => new(new ConfigurationError
        {
            Pointer = seam.Pointer,
            Message = message,
            Check = ConfigurationCheck.ReferenceResolution,
        });
}
