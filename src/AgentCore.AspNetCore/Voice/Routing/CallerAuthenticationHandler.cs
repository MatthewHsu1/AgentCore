using System.Security.Claims;
using System.Text.Encodings.Web;
using AgentCore.AspNetCore.DependencyInjection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentCore.AspNetCore.Voice.Routing
{
    /// <summary>
    /// The scheme the call route requires: the active vendor's own check that the request came from it. One scheme for
    /// every vendor, because AgentCore learns which vendor runs only when the host starts and loads the document, after
    /// the schemes are registered.
    /// </summary>
    internal sealed class CallerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        /// <summary>The claim that names the vendor the caller proved to be.</summary>
        internal const string VendorClaim = "agentcore:vendor";

        protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (Context.RequestServices.GetService<AgentCoreBoot>() is not { ConversationRoute: { } route } boot)
            {
                return AuthenticateResult.Fail("this host routes no inbound conversation.");
            }

            if (!await route.IsVendor(Context).ConfigureAwait(false))
            {
                return AuthenticateResult.Fail("the caller is not the conversation vendor.");
            }

            string vendor = boot.Configuration.Providers?.Conversation?.Kind ?? string.Empty;
            ClaimsIdentity identity = new([new Claim(VendorClaim, vendor)], Scheme.Name);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
        }
    }
}
