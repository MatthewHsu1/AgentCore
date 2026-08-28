using AgentCore.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.AddAgentCoreHost();

var app = builder.Build();

app.MapAgentCoreHost();

// Which sites may frame the widget.
//
// Configure with, e.g.:
//   "Widget": { "AllowedOrigins": [ "https://exmaple.com" ] }
var widgetOrigins = builder.Configuration.GetSection("Widget:AllowedOrigins").Get<string[]>();
var frameAncestors = widgetOrigins is { Length: > 0 }
    ? string.Join(' ', widgetOrigins)
    : "'none'";

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/chat"))
    {
        context.Response.Headers["Content-Security-Policy"] = $"frame-ancestors {frameAncestors}";
    }

    await next();
});

app.UseStaticFiles();

// nonfile: without it, routing selects this fallback for /chat/assets/index.js, the static file
// middleware stands down because an endpoint is already selected, and every script and stylesheet
// is answered with the page. The constraint makes the fallback decline anything whose last segment
// looks like a file, so static files sees no endpoint and serves the real bytes.
//
// The /chat scope: an unmatched /v1 route must answer 404, or a mistyped endpoint reaches the
// browser as a confusing HTML parse error instead of an obvious miss.
app.MapFallbackToFile("/chat/{*path:nonfile}", "chat/index.html");

app.Run();
