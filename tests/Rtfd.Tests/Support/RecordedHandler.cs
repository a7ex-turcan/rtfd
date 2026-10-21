using System.Net;
using System.Text;

namespace Rtfd.Tests.Support;

/// <summary>Answers HTTP requests from a queue per path and query, and keeps every request for the assertions.</summary>
public sealed class RecordedHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _answers = new(StringComparer.Ordinal);

    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    /// <summary>The next answer to <paramref name="pathAndQuery"/>; the last one queued repeats.</summary>
    public RecordedHandler On(string pathAndQuery, HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
        On(pathAndQuery, status, body, "application/json", headers);

    public RecordedHandler On(string pathAndQuery, HttpStatusCode status, string body, string mediaType, params (string Name, string Value)[] headers)
    {
        if (!_answers.TryGetValue(pathAndQuery, out var queue))
        {
            _answers[pathAndQuery] = queue = new();
        }

        queue.Enqueue(() =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
            foreach (var (name, value) in headers)
            {
                response.Headers.TryAddWithoutValidation(name, value);
            }

            return response;
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request, request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
        var key = request.RequestUri!.PathAndQuery;
        if (!_answers.TryGetValue(key, out var queue) || queue.Count == 0)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"message": "Not Found"}""") };
        }

        var answer = queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        return answer();
    }
}
