using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// One-button Apple Pay / Google Pay demo on top of Flitt's JS SDK (PaymentButton).
// Flow: click starts our order + opens the sheet. Our order is inserted before requesting
// a Flitt token. The SDK after hook blocks payment until both order creation and wallet authorization finish.

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

// Demo-only store. Insert our record before making any Flitt order request.
var orders = new ConcurrentDictionary<string, OrderRecord>();

// Public merchant id for the button's client-side mode (renders without an order).
app.MapGet("/api/config", () => Results.Ok(new { merchantId = flitt.MerchantId, currency = "GEL" }));

// 1. Called on wallet click, concurrently with opening the native sheet.
app.MapPost("/api/orders", async (CreateOrder body, HttpRequest req, IHttpClientFactory http) =>
{
    // Empty CallbackUrl = call back to whatever public host served the page (e.g. the HTTPS tunnel).
    var scheme = req.Headers["X-Forwarded-Proto"].FirstOrDefault() ?? req.Scheme;
    if (body.AmountTetri is <= 0 or > 1_000_000_00)
        return Results.BadRequest("amountTetri must be between 1 and 100000000");

    var callbackUrl = string.IsNullOrEmpty(flitt.CallbackUrl) ? $"{scheme}://{req.Host}/flitt/callback" : flitt.CallbackUrl;
    var orderId = $"wallet-{Guid.NewGuid():N}";
    if (!orders.TryAdd(orderId, new OrderRecord(orderId, body.AmountTetri, "created")))
        return Results.Problem("Could not create our order.");
    var returnUrl = $"{scheme}://{req.Host}/payment-return?orderId={Uri.EscapeDataString(orderId)}";
    var request = new Dictionary<string, object?>
    {
        ["order_id"] = orderId,
        ["merchant_id"] = flitt.MerchantId,
        ["order_desc"] = "Wallet demo deposit",
        ["amount"] = body.AmountTetri,
        ["currency"] = "GEL",
        ["lifetime"] = 900,
        ["server_callback_url"] = callbackUrl,
        ["response_url"] = returnUrl,
        ["delayed"] = "N",
    };
    request["signature"] = FlittSignature.Compute(flitt.SecretKey, request);

    try
    {
        var response = await PostFlitt(http, "/api/checkout/token", request);
        var token = response?["token"]?.GetValue<string>();
        if (response?["response_status"]?.GetValue<string>() != "success" || string.IsNullOrWhiteSpace(token))
        {
            orders[orderId] = orders[orderId] with { Status = "initialization_failed" };
            return Results.Problem("Flitt could not initialize this order. Payment was not submitted.");
        }
        return Results.Ok(new { orderId, token, merchantId = flitt.MerchantId });
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
    {
        orders[orderId] = orders[orderId] with { Status = "initialization_unknown" };
        return Results.Problem("Could not obtain a Flitt token. Payment was not submitted.", statusCode: 502);
    }
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
    if (!orders.TryGetValue(orderId, out var order)) return Results.NotFound();
    if (fields.GetValueOrDefault("merchant_id")?.ToString() != flitt.MerchantId.ToString(CultureInfo.InvariantCulture)
        || fields.GetValueOrDefault("currency")?.ToString() != "GEL"
        || fields.GetValueOrDefault("amount")?.ToString() != order.AmountTetri.ToString(CultureInfo.InvariantCulture))
        return Results.BadRequest();
    orders[orderId] = order with { Status = status };
    return Results.Ok();
});

// 3. Status pull, because the demo callback URL is not reachable from Flitt when run locally.
app.MapGet("/api/orders/{orderId}/status", async (string orderId, IHttpClientFactory http) =>
{
    var request = new Dictionary<string, object?> { ["order_id"] = orderId, ["merchant_id"] = flitt.MerchantId };
    request["signature"] = FlittSignature.Compute(flitt.SecretKey, request);
    var response = await PostFlitt(http, "/api/status/order_id", request);
    return Results.Content(response?.ToJsonString() ?? "{}", "application/json");
});

// Browser returns are navigation only: never mark an order paid from return parameters.
app.MapMethods("/payment-return", new[] { "GET", "POST" }, (HttpRequest req) =>
{
    var orderId = req.Query["orderId"].ToString();
    return Results.Redirect($"/?order={Uri.EscapeDataString(orderId)}");
});

app.MapGet("/api/orders/{orderId}", (string orderId) =>
    orders.TryGetValue(orderId, out var order) ? Results.Ok(order) : Results.NotFound());

app.Run();

static async Task<JsonNode?> PostFlitt(IHttpClientFactory http, string path, Dictionary<string, object?> request)
{
    var client = http.CreateClient("flitt");
    using var res = await client.PostAsJsonAsync(path, new { request });
    res.EnsureSuccessStatusCode();
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
