//summary//
// Provides a read-only cart presentation with current catalog price estimates; checkout still revalidates prices.
//summary//
//-------------------Start Of File------------------//
namespace CloudStore.Models;

public sealed record CartLine(string RowKey, string ProductId, string ProductName, int Quantity, long? UnitPriceCents)
{
    public long? TotalCents => UnitPriceCents is long price && Quantity is >= 1 and <= 1000
        ? checked(price * Quantity) : null;
}

public sealed record CartPage(IReadOnlyList<CartLine> Lines)
{
    public int ItemCount => Lines.Sum(line => line.Quantity);
    public long? EstimatedTotalCents => Lines.All(line => line.TotalCents.HasValue)
        ? Lines.Sum(line => line.TotalCents!.Value) : null;
}
//-------------------End Of File------------------//
