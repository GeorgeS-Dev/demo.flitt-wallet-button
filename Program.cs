using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// One-button Apple Pay / Google Pay demo on top of Flitt's JS SDK (PaymentButton).
// Flow: button renders from merchant_id + amount (no order) -> tap opens the native sheet ->
// payer authorizes -> SDK's `after` hook calls /api/orders -> SDK submits wallet data + token -> Flitt calls back.

var builder = WebApplication.CreateBuilder(args);
var flitt = builder.Configuration.GetSection("Flitt").Get<FlittOptions>()!;
builder.Services.AddHttpClient("flitt", c => c.BaseAddress = new Uri(flitt.BaseUrl));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

// Apple Pay domain verification: drop the file Flitt support sends into the project root.
// Extensionless, so UseStaticFiles would refuse it.
var appleAssociation = Path.Combine(app.Environment.ContentRootPath, "apple-developer-merchantid-domain-association.txt");
app.MapGet("/.well-known/apple-developer-merchantid-domain-association", () =>
    File.Exists(appleAssociation) ? Results.File(appleAssociation, "text/plain") : Results.NotFound());

// Stand-in for our own transaction table: the order exists BEFORE the wallet sheet opens.
var orders = new ConcurrentDictionary<string, OrderRecord>();

// Public merchant id for the button's client-side mode (renders without an order).
app.MapGet("/api/config", () => Results.Ok(new { merchantId = flitt.MerchantId, currency = "GEL" }));

// 1. Create our transaction + Flitt order. Called AFTER the payer authorizes in the wallet sheet.
app.MapPost("/api/orders", async (CreateOrder body, HttpRequest req, IHttpClientFactory http) =>
{
    // Empty CallbackUrl = call back to whatever public host served the page (e.g. the HTTPS tunnel).
    var scheme = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
    if (body.AmountTetri is <= 0 or > 1_000_000_00)
        return Results.BadRequest("amountTetri must be between 1 and 100000000");

    var callbackUrl = string.IsNullOrEmpty(flitt.CallbackUrl) ? $"{scheme}://{req.Host}/flitt/callback" : flitt.CallbackUrl;
    var orderId = $"wallet-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

    // Our record exists before Flitt hears of the order, so no Flitt token can outlive a missing record.
    if (!orders.TryAdd(orderId, new OrderRecord(orderId, body.AmountTetri, "initializing")))
        return Results.Problem("Could not record the order. Payment must not be submitted.", statusCode: 500);

    var request = new Dictionary<string, object?>
    {
        ["order_id"] = orderId,
        ["merchant_id"] = flitt.MerchantId,
        ["order_desc"] = "Wallet demo deposit",
        ["amount"] = body.AmountTetri,
        ["currency"] = "GEL",
        ["lifetime"] = 900,
        ["server_callback_url"] = callbackUrl,
    };
    request["signature"] = FlittSignature.Compute(flitt.SecretKey, request);

    // Success = Flitt says "success" AND returns a token. Anything else is a failed order, and the
    // frontend only ever receives a token from this success branch.
    string? token = null;
    try
    {
        var response = await PostFlitt(http, "/api/checkout/token", request);
        if (response?["response_status"]?.GetValue<string>() == "success")
            token = response["token"]?.GetValue<string>();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
    {
        // Fall through to the failure branch below.
    }

    if (string.IsNullOrWhiteSpace(token))
    {
        orders[orderId] = orders[orderId] with { Status = "failed" };
        return Results.Problem("Flitt did not create the order. Payment must not be submitted.", statusCode: 502);
    }

    orders[orderId] = orders[orderId] with { Status = "created" };
    return Results.Ok(new { orderId, token, merchantId = flitt.MerchantId });
});

// 2. Flitt server callback: the only place a deposit is actually confirmed.
app.MapPost("/flitt/callback", async (HttpRequest req, ILogger<Program> log) =>
{
    var fields = await ReadCallback(req);
    var valid = FlittSignature.Verify(flitt.SecretKey, fields);
    var orderId = fields.GetValueOrDefault("order_id")?.ToString() ?? "";
    var status = fields.GetValueOrDefault("order_status")?.ToString() ?? "";
    log.LogInformation("Flitt callback order={OrderId} status={Status} signatureValid={Valid}", orderId, status, valid);
    if (!valid) return Results.BadRequest();
    if (!orders.TryGetValue(orderId, out var order))
    {
        // A payment we never created an order for: the gate was bypassed (or another client used
        // this merchant). Never adopt it as one of our deposits; surface it loudly instead.
        log.LogWarning("ORPHAN Flitt callback for unknown order={OrderId} status={Status}", orderId, status);
        return Results.Ok();
    }
    orders[orderId] = order with { Status = status };
    return Results.Ok();
});

app.MapGet("/api/orders/{orderId}", (string orderId) =>
    orders.TryGetValue(orderId, out var order) ? Results.Ok(order) : Results.NotFound());

// 3. Status pull, because the demo callback URL is not reachable from Flitt when run locally.
app.MapGet("/api/orders/{orderId}/status", async (string orderId, IHttpClientFactory http) =>
{
    var request = new Dictionary<string, object?> { ["order_id"] = orderId, ["merchant_id"] = flitt.MerchantId };
    request["signature"] = FlittSignature.Compute(flitt.SecretKey, request);
    var response = await PostFlitt(http, "/api/status/order_id", request);
    return Results.Content(response?.ToJsonString() ?? "{}", "application/json");
});

app.Run();

static async Task<JsonNode?> PostFlitt(IHttpClientFactory http, string path, Dictionary<string, object?> request)
{
    var client = http.CreateClient("flitt");
    using var res = await client.PostAsJsonAsync(path, new { request });
    var json = await JsonNode.ParseAsync(await res.Content.ReadAsStreamAsync());
    return json?["response"];
}

static async Task<Dictionary<string, object?>> ReadCallback(HttpRequest req)
{
    if (req.HasFormContentType)
    {
        var form = await req.ReadFormAsync();
        return form.ToDictionary(kv => kv.Key, kv => (object?)kv.Value.ToString());
    }
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement.TryGetProperty("response", out var inner) ? inner : doc.RootElement;
    return root.EnumerateObject().ToDictionary(
        p => p.Name,
        p => (object?)(p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()));
}

record FlittOptions(string BaseUrl, long MerchantId, string SecretKey, string CallbackUrl)
{
    public FlittOptions() : this("", 0, "", "") { }
}

record CreateOrder(long AmountTetri);

record OrderRecord(string OrderId, long AmountTetri, string Status);

static class FlittSignature
{
    // sha1(secret | values of non-empty params sorted by key), lowercase hex. See docs.flitt.com/api/building-signature/
    public static string Compute(string secret, IReadOnlyDictionary<string, object?> fields)
    {
        var values = fields
            .Where(kv => kv.Key is not ("signature" or "response_signature_string"))
            .Select(kv => (kv.Key, Value: Convert.ToString(kv.Value, CultureInfo.InvariantCulture) ?? ""))
            .Where(kv => kv.Value.Length > 0)
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value);
        var raw = string.Join('|', values.Prepend(secret));
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public static bool Verify(string secret, IReadOnlyDictionary<string, object?> fields) =>
        fields.TryGetValue("signature", out var sig)
        && string.Equals(sig?.ToString(), Compute(secret, fields), StringComparison.Ordinal);
}
