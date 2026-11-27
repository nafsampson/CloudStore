//summary//
// Persists small demo datasets and a retryable queue in SQLite; never opens an Azure connection.
//summary//
//-------------------Start Of File------------------//
using Azure;
using Azure.Data.Tables;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CloudStore.LocalDemo;

public sealed class DemoDatabase(DemoSettings settings)
{
    private readonly object gate = new();
    private static readonly JsonSerializerOptions Json = new() { Converters = { new ETagConverter() } };
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = settings.RecordsFile, Mode = SqliteOpenMode.ReadWrite, DefaultTimeout = 5 }.ToString());
        connection.Open();
        return connection;
    }
    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string, object?)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string, object?)[] parameters)
    {
        var command = Command(connection, sql, parameters); command.Transaction = transaction; return command;
    }
    public void Initialize()
    {
        System.IO.Directory.CreateDirectory(settings.Directory);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = settings.RecordsFile }.ToString());
        connection.Open();
        using var command = Command(connection, """
            CREATE TABLE IF NOT EXISTS Records (
                Kind TEXT NOT NULL, PartitionKey TEXT NOT NULL, RowKey TEXT NOT NULL,
                Payload TEXT NOT NULL, Version TEXT NOT NULL,
                PRIMARY KEY (Kind, PartitionKey, RowKey));
            CREATE TABLE IF NOT EXISTS Messages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, Kind TEXT NOT NULL, Payload TEXT NOT NULL,
                Attempts INTEGER NOT NULL DEFAULT 0, Due INTEGER NOT NULL DEFAULT 0, Poison INTEGER NOT NULL DEFAULT 0);
            """);
        command.ExecuteNonQuery();
    }
    public List<T> Read<T>(string kind) where T : class, ITableEntity, new()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, "SELECT Payload, Version FROM Records WHERE Kind=$kind ORDER BY RowKey LIMIT 10000", ("$kind", kind));
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                var entity = JsonSerializer.Deserialize<T>(reader.GetString(0), Json) ?? throw new InvalidDataException("Invalid local record.");
                entity.ETag = new ETag(reader.GetString(1));
                rows.Add(entity);
            }
            return rows;
        }
    }
    public void Add<T>(string kind, T entity) where T : class, ITableEntity
    {
        lock (gate)
        {
            using var connection = Open();
            var version = Guid.NewGuid().ToString("N");
            entity.Timestamp = DateTimeOffset.UtcNow;
            using var command = Command(connection, "INSERT INTO Records VALUES ($kind,$partition,$id,$payload,$version)",
                ("$kind", kind), ("$partition", entity.PartitionKey), ("$id", entity.RowKey),
                ("$payload", JsonSerializer.Serialize(entity, Json)), ("$version", version));
            try { command.ExecuteNonQuery(); entity.ETag = new ETag(version); }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19) { throw new RequestFailedException(409, "The record already exists."); }
        }
    }
    public void Update<T>(string kind, T entity, ETag expected, TableUpdateMode mode) where T : class, ITableEntity
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var select = Command(connection, transaction, "SELECT Payload,Version FROM Records WHERE Kind=$kind AND PartitionKey=$partition AND RowKey=$id",
                ("$kind", kind), ("$partition", entity.PartitionKey), ("$id", entity.RowKey));
            string payload, version;
            using (var reader = select.ExecuteReader())
            {
                if (!reader.Read()) throw new RequestFailedException(404, "Record not found.");
                payload = reader.GetString(0); version = reader.GetString(1);
            }
            if (expected != ETag.All && expected.ToString() != version) throw new RequestFailedException(412, "Record version changed.");
            JsonObject updated;
            if (entity is TableEntity patch)
            {
                updated = JsonNode.Parse(payload)!.AsObject();
                foreach (var pair in patch)
                    if (pair.Key is not ("PartitionKey" or "RowKey" or "ETag" or "Timestamp")) updated[pair.Key] = JsonSerializer.SerializeToNode(pair.Value, Json);
            }
            else
            {
                var replacement = JsonSerializer.SerializeToNode(entity, Json)!.AsObject();
                updated = mode == TableUpdateMode.Replace ? replacement : JsonNode.Parse(payload)!.AsObject();
                if (mode == TableUpdateMode.Merge) foreach (var pair in replacement) updated[pair.Key] = pair.Value?.DeepClone();
            }
            updated["Timestamp"] = DateTimeOffset.UtcNow;
            var next = Guid.NewGuid().ToString("N");
            using var update = Command(connection, transaction, "UPDATE Records SET Payload=$payload,Version=$next WHERE Kind=$kind AND PartitionKey=$partition AND RowKey=$id AND Version=$expected",
                ("$payload", updated.ToJsonString()), ("$next", next), ("$kind", kind), ("$partition", entity.PartitionKey), ("$id", entity.RowKey), ("$expected", version));
            if (update.ExecuteNonQuery() != 1) throw new RequestFailedException(412, "Record version changed.");
            transaction.Commit();
        }
    }
    public void Delete(string kind, string partition, string id, ETag expected)
    {
        lock (gate)
        {
            using var connection = Open();
            var versioned = expected != default && expected != ETag.All;
            using var command = Command(connection, "DELETE FROM Records WHERE Kind=$kind AND PartitionKey=$partition AND RowKey=$id" + (versioned ? " AND Version=$version" : ""),
                ("$kind", kind), ("$partition", partition), ("$id", id));
            if (versioned) command.Parameters.AddWithValue("$version", expected.ToString());
            if (command.ExecuteNonQuery() == 0)
            {
                using var exists = Command(connection, "SELECT 1 FROM Records WHERE Kind=$kind AND PartitionKey=$partition AND RowKey=$id", ("$kind", kind), ("$partition", partition), ("$id", id));
                throw new RequestFailedException(exists.ExecuteScalar() is null ? 404 : 412, "Record absent or changed.");
            }
        }
    }
    public void Enqueue(string kind, string payload)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, "INSERT INTO Messages(Kind,Payload) VALUES($kind,$payload)", ("$kind", kind), ("$payload", payload));
            command.ExecuteNonQuery();
            if (kind == "operations")
            {
                using var trim = Command(connection, "DELETE FROM Messages WHERE Kind='operations' AND Id NOT IN (SELECT Id FROM Messages WHERE Kind='operations' ORDER BY Id DESC LIMIT 1000)");
                trim.ExecuteNonQuery();
            }
        }
    }
    public List<string> PeekOperations()
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = Command(connection, "SELECT Payload FROM Messages WHERE Kind='operations' ORDER BY Id DESC LIMIT 10");
            using var reader = command.ExecuteReader();
            var items = new List<string>(); while (reader.Read()) items.Add(reader.GetString(0)); return items;
        }
    }
    public (long Id, string Payload, int Attempts)? Claim(DateTimeOffset now)
    {
        lock (gate)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            using var poison = Command(connection, transaction, "UPDATE Messages SET Poison=1 WHERE Kind='orders' AND Attempts>=5 AND Due<=$now", ("$now", now.ToUnixTimeSeconds()));
            poison.ExecuteNonQuery();
            using var select = Command(connection, transaction, "SELECT Id,Payload,Attempts FROM Messages WHERE Kind='orders' AND Poison=0 AND Due<=$now AND Attempts<5 ORDER BY Id LIMIT 1", ("$now", now.ToUnixTimeSeconds()));
            long id; string payload; int attempts;
            using (var reader = select.ExecuteReader())
            {
                if (!reader.Read()) return null;
                id = reader.GetInt64(0); payload = reader.GetString(1); attempts = reader.GetInt32(2) + 1;
            }
            using var update = Command(connection, transaction, "UPDATE Messages SET Attempts=$attempts,Due=$due WHERE Id=$id", ("$attempts", attempts), ("$due", now.AddSeconds(30).ToUnixTimeSeconds()), ("$id", id));
            update.ExecuteNonQuery(); transaction.Commit(); return (id, payload, attempts);
        }
    }
    public void Complete(long id)
    {
        lock (gate) { using var connection = Open(); using var command = Command(connection, "DELETE FROM Messages WHERE Id=$id AND Kind='orders'", ("$id", id)); command.ExecuteNonQuery(); }
    }
    private sealed class ETagConverter : JsonConverter<ETag>
    {
        public override ETag Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => new(reader.GetString() ?? "");
        public override void Write(Utf8JsonWriter writer, ETag value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
//-------------------End Of File------------------//
