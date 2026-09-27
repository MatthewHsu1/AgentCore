using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// Picks the entry for one request on a route that carries this selector, so the server chooses the
    /// agent and the URL does not.
    /// </summary>
    public interface IEntrySelector
    {
        /// <summary>Picks the entry this request runs.</summary>
        /// <param name="http">The request, already authenticated when the host runs authentication first.</param>
        /// <param name="cancellationToken">Cancels the pick.</param>
        /// <returns>The entry key, or <see langword="null"/> to refuse the request with 403.</returns>
        ValueTask<string?> SelectAsync(HttpContext http, CancellationToken cancellationToken);
    }
}
