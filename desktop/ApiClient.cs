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
        { BaseAddress = Safety.ApiUri(Environment.GetEnvironmentVariable("MSGUIDE_API_URL")), Timeout = TimeSpan.FromSeconds(55), MaxResponseContentBufferSize = 512 * 1024 };
        token = Environment.GetEnvironmentVariable("MSGUIDE_LOCAL_TOKEN");
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
                session = null;
                throw new InvalidOperationException($"Local service returned HTTP {(int)response.StatusCode}. Check the service/token; capture and approve again. No automatic retry.");
            }
            return await response.Content.ReadFromJsonAsync<T>(Json, ct)
                ?? throw new InvalidOperationException("The local service returned an empty response.");
        }
    }

    public Task<HealthInfo> Health(CancellationToken ct) => Send<HealthInfo>(new(HttpMethod.Get, "health"), false, ct);

    public async Task<Guidance> Guide(Observation observation, string prompt, CancellationToken ct)
    {
        if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(5))
        {
            session = await Send<SessionInfo>(new(HttpMethod.Post, "v1/sessions"), true, ct);
            if (string.IsNullOrWhiteSpace(session.SessionId) || session.ExpiresAt <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("The service returned an invalid or expired session.");
        }
        ct.ThrowIfCancellationRequested();
        if (!Safety.Fresh(observation.CapturedAt, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("Snapshot expired. Capture and review again.");
        return await Send<Guidance>(new(HttpMethod.Post, "v1/guidance")
        { Content = JsonContent.Create(new GuidanceRequest(session.SessionId, prompt, true, observation), options: Json) }, true, ct);
    }

    public void Dispose() { session = null; http.Dispose(); }
}