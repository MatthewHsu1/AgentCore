namespace AgentCore.Application.Ports;

/// <summary>
/// One vendor a <c>kind</c> in the configuration document can name.
/// </summary>
/// <remarks>
/// <para>
/// Every vendor seam in the solution answers the same question — the document wrote a
/// <c>kind</c>, and one of the adapters the host registered serves it. Before this base existed,
/// each of the five seams carried its own copy of that lookup. They now share
/// <see cref="Providers.VendorAdapterSelector"/>, and this is the only member it needs.
/// </para>
/// <para>
/// The name is <c>IVendorAdapter</c> and not <c>IAdapter</c> on purpose: what these have in
/// common is not that they adapt something, but that each one is a vendor selected by a name a
/// human wrote in a document.
/// </para>
/// </remarks>
public interface IVendorAdapter
{
    /// <summary>Gets the one <c>kind</c> value this adapter serves, such as <c>telnyx-relay</c>.</summary>
    /// <remarks>A vendor name is written by a human, so it matches without regard to case.</remarks>
    string Kind { get; }
}
