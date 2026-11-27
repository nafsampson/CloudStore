//summary//
// Validates required connection settings at host startup, after all configuration providers are applied.
//summary//
//-------------------Start Of File------------------//
namespace CloudStore.Security;

public sealed class RuntimeConnections
{
    public string Sql { get; set; } = "";
    public string Storage { get; set; } = "";

    public bool HasValidStorage()
    {
        try
        {
            _ = new Azure.Data.Tables.TableServiceClient(Storage);
            _ = new Azure.Storage.Files.Shares.ShareClient(Storage, "contracts");
            return true;
        }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException)
        { return false; }
    }
}
//-------------------End Of File------------------//
