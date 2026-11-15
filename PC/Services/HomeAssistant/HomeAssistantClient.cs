using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MyAiAssistant.Services.HomeAssistant;

/// <summary>
/// The REST surface of Home Assistant that the app uses: who am I, what is the state of
/// everything, and call one service. Nothing here decides whether an action is allowed —
/// that happens before the call, in the permission gate.
/// </summary>
public sealed class HomeAssistantClient
{
    private readonly HttpClient _http;
    private readonly Uri _server;

    public HomeAssistantClient(HttpClient http, Uri server)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _server = server ?? throw new ArgumentNullException(nameof(server));
    }

    /// <summary>
    /// Supplies the bearer token. A delegate rather than a field so the caller can refresh an
    /// expired token at the moment of use instead of pinning one for the process lifetime.
    /// </summary>
    public Func<CancellationToken, Task<string?>>? TokenProvider { get; set; }

    public Uri Server => _server;

    /// <summary>
    /// Validates a user-typed address. Only http and https are accepted: a Home Assistant
    /// client has no business being pointed at file:// or a custom scheme.
    /// </summary>
    public static bool TryNormalizeServer(string? input, out Uri server, out string error)
    {
        server = null!;
        error = "";

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "请填写 Home Assistant 地址";
            return false;
        }

        var trimmed = input.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal)) trimmed = "http://" + trimmed;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed))
        {
            error = "地址格式不正确";
            return false;
        }

        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
        {
            error = "只支持 http 或 https 地址";
            return false;
        }

        if (string.IsNullOrEmpty(parsed.Host))
        {
            error = "地址缺少主机名";
            return false;
        }

        var builder = new UriBuilder(parsed) { Path = parsed.AbsolutePath.TrimEnd('/') + "/" };
        server = builder.Uri;
        return true;
    }

    public async Task<HaApiInfo> GetApiInfoAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/", null, cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);

        string? locationName = null;
        string? version = null;
        var message = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var messageElement)) message = messageElement.GetString() ?? "";
            if (root.TryGetProperty("location_name", out var locationElement)) locationName = locationElement.GetString();
            if (root.TryGetProperty("version", out var versionElement)) version = versionElement.GetString();
        }
        catch (JsonException)
        {
            // A reachable server that answers /api/ with something else is still not our server.
            throw new HaRequestException((int)response.StatusCode, "服务器返回的内容不是 Home Assistant API 响应");
        }

        return new HaApiInfo(message, locationName, version);
    }

    public async Task<IReadOnlyList<HaEntity>> GetStatesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(HttpMethod.Get, "api/states", null, cancellationToken).ConfigureAwait(false);
        var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new HaRequestException((int)response.StatusCode, "实体列表不是数组");

            var entities = new List<HaEntity>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var entityId = element.TryGetProperty("entity_id", out var idElement) ? idElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(entityId)) continue;

                var state = element.TryGetProperty("state", out var stateElement)
                    ? stateElement.ValueKind == JsonValueKind.String ? stateElement.GetString() ?? "" : stateElement.ToString()
                    : "";

                string? friendlyName = null;
                string? unit = null;
                if (element.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object)
                {
                    if (attributes.TryGetProperty("friendly_name", out var nameElement))
                        friendlyName = nameElement.GetString();
                    if (attributes.TryGetProperty("unit_of_measurement", out var unitElement))
                        unit = unitElement.GetString();
                }

                DateTimeOffset? lastChanged = null;
                if (element.TryGetProperty("last_changed", out var changedElement) &&
                    changedElement.GetString() is { } changedText &&
                    DateTimeOffset.TryParse(changedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedChanged))
                    lastChanged = parsedChanged;

                var domain = entityId.Contains('.') ? entityId[..entityId.IndexOf('.')] : entityId;
                entities.Add(new HaEntity(entityId, domain, string.IsNullOrWhiteSpace(friendlyName) ? entityId : friendlyName!,
                    state ?? "", unit, lastChanged));
            }

            return entities;
        }
        catch (JsonException ex)
        {
            throw new HaRequestException((int)response.StatusCode, $"实体列表不是合法 JSON：{ex.Message}");
        }
    }

    public async Task CallServiceAsync(HaServiceCall call, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(call);
        if (string.IsNullOrWhiteSpace(call.Domain) || string.IsNullOrWhiteSpace(call.Service))
            throw new ArgumentException("服务调用必须同时有 domain 与 service", nameof(call));

        var payload = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(call.EntityId)) payload["entity_id"] = call.EntityId;

        if (!string.IsNullOrWhiteSpace(call.DataJson))
        {
            using var document = JsonDocument.Parse(call.DataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("附加数据必须是 JSON 对象", nameof(call));

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("entity_id")) continue;
                payload[property.Name] = property.Value.Clone();
            }
        }

        var path = $"api/services/{Uri.EscapeDataString(call.Domain)}/{Uri.EscapeDataString(call.Service)}";
        using var response = await SendAsync(HttpMethod.Post, path, JsonSerializer.Serialize(payload), cancellationToken)
            .ConfigureAwait(false);
        await ReadAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, string? jsonBody, CancellationToken cancellationToken)
    {
        var token = TokenProvider is null ? null : await TokenProvider(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(token))
            throw new HaAuthenticationException("尚未获得 Home Assistant 令牌");

        using var request = new HttpRequestMessage(method, new Uri(_server, path));
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            throw new HaAuthenticationException("令牌已失效，需要重新授权");
        }

        return response;
    }

    private static async Task<string> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = body.Length > 200 ? body[..200] : body;
            throw new HaRequestException((int)response.StatusCode,
                $"Home Assistant 返回 HTTP {(int)response.StatusCode}：{reason}");
        }
        return body;
    }
}
