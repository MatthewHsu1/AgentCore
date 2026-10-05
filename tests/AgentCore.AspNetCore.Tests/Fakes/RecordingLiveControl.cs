using System.Net;
using System.Text.Json.Nodes;

namespace AgentCore.AspNetCore.Tests.Fakes
{
    /// <summary>Stands where OpenAI's call-control API would be: records each request and answers 200 unless told otherwise.</summary>
    internal sealed class RecordingLiveControl : HttpMessageHandler
    {
        private readonly List<Seen> _requests = [];

        public Func<string, HttpResponseMessage?>? Answer { get; set; }

        public IReadOnlyList<Seen> Requests
        {
            get
            {
                lock (_requests)
                {
                    return [.. _requests];
                }
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            lock (_requests)
            {
                _requests.Add(new Seen(request.Method.Method, path, request.Headers.Authorization?.ToString(), body is null ? null : JsonNode.Parse(body)));
            }

            return Answer?.Invoke(path) ?? new HttpResponseMessage(HttpStatusCode.OK);
        }

        internal sealed record Seen(string Method, string Path, string? Authorization, JsonNode? Body);
    }
}
