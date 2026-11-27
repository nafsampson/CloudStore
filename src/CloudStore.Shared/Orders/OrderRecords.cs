//summary//
// Defines shared lifecycle rules, stable request keys and the versioned queue reference contract.
//summary//
//-------------------Start Of File------------------//
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudStore.Models;
namespace CloudStore.Orders;

public static class OrderRules
{
    public const string Pending = "Pending", Ready = "Ready", Processed = "Processed";
    public const string Sent = "Sent", Failed = "Failed";
    public const int MaximumAttempts = 5;
    public static bool IsOwner(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
    public static bool IsId(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static string ManualId(string account, string requestId)
    {
        if (!Guid.TryParse(requestId, out var id) || id == Guid.Empty)
            throw new OrderValidationException("Open a new order form before submitting.");
        return Key(account, "manual", id.ToString("N"));
    }
    public static string CartId(string account, string rowKey) => Key(account, "cart", rowKey);
    private static string Key(string account, string source, string key)
    {
        if (!int.TryParse(account, NumberStyles.None, CultureInfo.InvariantCulture, out var owner) || owner <= 0
            || string.IsNullOrWhiteSpace(key)) throw new OrderValidationException("A valid account and request are required.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{owner}:{source}:{key}"))).ToLowerInvariant();
    }
    public static long PriceCents(string price)
    {
        if (!decimal.TryParse(price, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || amount <= 0 || amount > 1_000_000m || decimal.Round(amount, 2) != amount)
            throw new OrderValidationException("This product does not have a valid price. Please contact an administrator.");
        return checked((long)(amount * 100));
    }
    public static bool IsTransient(Exception error) => error is HttpRequestException or IOException or TimeoutException
        || error is Azure.RequestFailedException request && (request.Status is 408 or 429 || request.Status >= 500);
    public static void CheckSnapshot(Order order)
    {
        if (!IsId(order.RowKey) || order.PartitionKey != "Order" || !IsOwner(order.CustomerId)
            || string.IsNullOrWhiteSpace(order.ProductId) || order.Quantity is < 1 or > 1000
            || order.UnitPriceCents is <= 0 or > 100_000_000
            || order.TotalPriceCents != order.UnitPriceCents * order.Quantity)
            throw new InvalidDataException("The saved order is not valid for processing.");
    }
}
public sealed class OrderValidationException(string message) : Exception(message);
public sealed class OrderConflictException() : Exception("This request was already used for a different order. Open a new order form.");
public sealed record OrderPatch(string? Status = null, string? QueueStatus = null, int? DispatchAttempts = null,
    DateTimeOffset? LastDispatchAttemptUtc = null, DateTimeOffset? ReadyAtUtc = null, DateTimeOffset? ProcessedAtUtc = null);
public sealed record DispatchResult(bool Accepted, string State);
public sealed record SubmissionResult(Order Order, bool Accepted);
public sealed record RecoveryResult(int Examined, int Accepted, int Awaiting);
public sealed record OrderMessage(int Version, string OrderId)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public string Encode() => JsonSerializer.Serialize(this, Options);
    public static OrderMessage Parse(string body)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(body) || body.Length > 512) throw new JsonException();
            var value = JsonSerializer.Deserialize<OrderMessage>(body, Options);
            if (value is null || value.Version != 1 || !OrderRules.IsId(value.OrderId)) throw new JsonException();
            return value;
        }
        // Do not include malformed message contents in poison/error logs.
        catch (JsonException) { throw new InvalidDataException("The order queue reference is invalid."); }
    }
}
//-------------------End Of File------------------//
