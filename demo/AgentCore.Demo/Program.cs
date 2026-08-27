using AgentCore.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAgentCoreHost();

var app = builder.Build();

app.MapAgentCoreHost();

app.UseStaticFiles();

// Two things in this pattern are load-bearing, both measured rather than assumed.
//
// nonfile: without it, routing selects this fallback for /chat/assets/index.js, the static file
// middleware stands down because an endpoint is already selected, and every script and stylesheet
// is answered with the page. The constraint makes the fallback decline anything whose last segment
// looks like a file, so static files sees no endpoint and serves the real bytes.
//
// The /chat scope: an unmatched /v1 route must answer 404, or a mistyped endpoint reaches the
// browser as a confusing HTML parse error instead of an obvious miss.
app.MapFallbackToFile("/chat/{*path:nonfile}", "chat/index.html");

app.Run();
