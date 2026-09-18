using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace MSGuide.Desktop;

public sealed class ApiClient : IDisposable
{
    private readonly HttpClient http;
    private readonly string? token;
    private SessionInfo? session;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ApiClient()
    {
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
        { BaseAddress = Safety.ApiUri(Environment.GetEnvironmentVariable("MSGUIDE_API_URL")), Timeout = TimeSpan.FromSeconds(54), MaxResponseContentBufferSize = 512 * 1024 };
        token = Environment.GetEnvironmentVariable("MSGUIDE_LOCAL_TOKEN");
    }

    internal ApiClient(HttpMessageHandler handler, string localToken)
    {
        http = new HttpClient(handler)
        { BaseAddress = Safety.ApiUri("http://127.0.0.1:8000"), Timeout = TimeSpan.FromSeconds(54),
            MaxResponseContentBufferSize = 512 * 1024 };
        token = localToken;
    }

    private async Task<T> Send<T>(HttpRequestMessage request, bool authenticated, CancellationToken ct)
    {
        using (request)
        {
            if (authenticated)
            {
                if (string.IsNullOrWhiteSpace(token))
                    throw new InvalidOperationException("Missing MSGUIDE_LOCAL_TOKEN. Start the client through the authenticated local launcher. No snapshot was sent.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 401 or 404 or 410) session = null;
                DiagnosticLog.Record("http_error", new
                {
                    endpoint = request.RequestUri?.ToString(),
                    status = (int)response.StatusCode,
                    errorCode = Header(response, "X-MSGuide-Error-Code"),
                    errorType = Header(response, "X-MSGuide-Error-Type"),
                    correlationId = Header(response, "X-MSGuide-Correlation-ID"),
                });
                throw new InvalidOperationException(ServiceErrorMessage(response));
            }
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new InvalidOperationException("The local service returned an empty response.");
        }
    }

    private static string ServiceErrorMessage(HttpResponseMessage response)
    {
        string? code = Header(response, "X-MSGuide-Error-Code");
        return code switch
        {
            "provider-invalid-result" => "Copilot could not match the request to an approved control. Try the request again on the current screen.",
            "provider-invalid-context" => "MSGuide could not prepare the captured controls for Copilot. Capture the window again.",
            "guidance-invalid-result" => "Copilot returned a target that no longer matches the captured window. Capture the window again.",
            "provider-not_started" or "provider-startup" or "provider-runtime" =>
                "The Copilot provider could not complete the request. Restart MSGuide and try again.",
            "guidance-unexpected" =>
                $"MSGuide hit an unexpected guidance error ({SafeErrorType(response)}).",
            _ when (int)response.StatusCode == 504 => "Guidance exceeded the remaining evidence budget. No action was accepted; capture fresh evidence to continue.",
            _ => $"Local service returned HTTP {(int)response.StatusCode}. Restart MSGuide and try again.",
        };
    }

    private static string SafeErrorType(HttpResponseMessage response)
    {
        string? value = Header(response, "X-MSGuide-Error-Type");
        return value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? value
            : "Unknown";
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? values.SingleOrDefault() : null;

    public Task<HealthInfo> Health(CancellationToken ct) => Send<HealthInfo>(new(HttpMethod.Get, "health"), false, ct);

    public async Task<Guidance> Guide(
        Observation observation, string prompt, CancellationToken ct, TaskProgress? task = null, bool planSegments = true)
    {
        var budget = Safety.GuidanceBudget(observation.CapturedAt, DateTimeOffset.UtcNow);
        if (!Safety.Fresh(observation.CapturedAt, DateTimeOffset.UtcNow) || budget <= TimeSpan.Zero)
            throw new InvalidOperationException("Too little freshness remains for guidance. Capture new evidence; no action was accepted.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(budget);
        var requestToken = deadline.Token;
        try
        {
            if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(5))
            {
                var created = await Send<SessionInfo>(new(HttpMethod.Post, "v1/sessions"), true, requestToken);
                requestToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(created.SessionId) || created.ExpiresAt <= DateTimeOffset.UtcNow)
                    throw new InvalidOperationException("The service returned an invalid or expired session.");
                session = created;
            }
            requestToken.ThrowIfCancellationRequested();
            if (!Safety.Fresh(observation.CapturedAt, DateTimeOffset.UtcNow))
                throw new InvalidOperationException("Snapshot expired. Capture and review again.");
            var result = await Send<Guidance>(new(HttpMethod.Post, "v1/guidance")
            { Content = JsonContent.Create(new GuidanceRequest(session.SessionId, prompt, true, observation, task, planSegments), options: Json) }, true, requestToken);
            if (planSegments && result.Plan is null
                || result.Plan is not null && !Safety.ValidPlan(result.Plan, observation))
                throw new InvalidOperationException("The service returned a missing or invalid whole plan. No action was accepted.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Guidance timed out before the evidence expired. No action was accepted; review the retained task and capture fresh evidence.");
        }
    }

    public void Dispose() { session = null; http.Dispose(); }
}