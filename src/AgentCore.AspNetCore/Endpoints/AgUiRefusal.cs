using AGUI.Abstractions;
using AGUI.Formatting;
using Microsoft.AspNetCore.Http;

namespace AgentCore.AspNetCore.Endpoints
{
    /// <summary>
    /// A run refused before it started. By default it is a 200 stream whose only event is RUN_ERROR with the code,
    /// because HttpAgent hands a non-2xx answer to the app as an Error with the raw body and no code.
    /// <c>?refusals=status</c> answers with the HTTP status and a JSON body instead, for comparison.
    /// </summary>
    internal static class AgUiRefusal
    {
        public static async Task WriteAsync(HttpContext http, RunAgentInput? input, int status, string code, string message)
        {
            if (http.Request.Query["refusals"] == "status")
            {
                http.Response.StatusCode = status;
                await http.Response.WriteAsJsonAsync(new { error = new { code, message } });
                return;
            }

            List<BaseEvent> events = [];
            if (http.Request.Query["refusals"] == "started")
            {
                events.Add(new RunStartedEvent { ThreadId = input?.ThreadId ?? string.Empty, RunId = input?.RunId ?? string.Empty });
            }

            events.Add(new RunErrorEvent { Code = code, Message = message });

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = SseEventStreamFormatter.ServerSentEventsMediaType;
            await new SseEventStreamFormatter().WriteAsync(Each(events), http.Response.Body, CancellationToken.None);
        }

        private static async IAsyncEnumerable<BaseEvent> Each(IEnumerable<BaseEvent> events)
        {
            foreach (BaseEvent evt in events)
            {
                await Task.Yield();
                yield return evt;
            }
        }
    }
}
