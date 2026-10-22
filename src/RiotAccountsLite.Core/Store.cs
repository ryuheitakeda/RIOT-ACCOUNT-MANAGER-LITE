using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RiotAccountsLite.Core;

public interface ISecretProtector { byte[] Protect(byte[] data); byte[] Unprotect(byte[] data); }

public sealed class Store
{
    // The full Riot Accounts app's database is version 2; this app neither opens nor changes such a file.
    public const int SchemaVersion = 1;
    private readonly string connectionString;
    private readonly ISecretProtector protector;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string AccountOrderKey = "accountOrder";
    public Store(string file, ISecretProtector protector)
    {
        this.protector = protector;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString();
        using var c = Open();
        // Checked before anything is written, so another app's database keeps its journal mode and version.
        using (var version = c.CreateCommand())
        {
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar()) is not (0 or SchemaVersion))
                throw new InvalidOperationException("このアプリのデータではないため開けません。");
        }
        Execute(c, null, $"""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS documents(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS secrets(id TEXT PRIMARY KEY,cipher BLOB NOT NULL);
            PRAGMA user_version={SchemaVersion};
            """);
    }

    private SqliteConnection Open() { var c = new SqliteConnection(connectionString); c.Open(); return c; }
    private static void Execute(SqliteConnection c, SqliteTransaction? tx, string sql, params (string Key, object Value)[] parameters)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (key, value) in parameters) cmd.Parameters.AddWithValue(key, value);
        cmd.ExecuteNonQuery();
    }

    public List<RiotAccount> Accounts()
    {
        using var c = Open();
        return ReadAccounts(c, null);
    }

    private static List<RiotAccount> ReadAccounts(SqliteConnection c, SqliteTransaction? tx)
    {
        var result = new List<RiotAccount>();
        using (var cmd = c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT json FROM documents WHERE kind='account'";
            using var r = cmd.ExecuteReader();
            while (r.Read()) result.Add(JsonSerializer.Deserialize<RiotAccount>(r.GetString(0), Json)!);
        }
        var positions = (ReadAccountOrder(c, tx) ?? []).Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);
        // Every save appends to the order, so it is the registration order; labels only break ties for accounts missing from it.
        return result.OrderBy(a => positions.GetValueOrDefault(a.Id, int.MaxValue)).ThenBy(a => a.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static List<Guid>? ReadAccountOrder(SqliteConnection c, SqliteTransaction? tx)
    {
        using var cmd = c.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT json FROM documents WHERE kind='setting' AND id=$id";
        cmd.Parameters.AddWithValue("$id", AccountOrderKey);
        if (cmd.ExecuteScalar() is not string json) return null;
        List<Guid>? order;
        try { order = JsonSerializer.Deserialize<List<Guid>>(json, Json); }
        catch (JsonException error) { throw new InvalidOperationException("保存されたアカウントの並び順が不正です。", error); }
        if (order == null || order.Contains(Guid.Empty) || order.Distinct().Count() != order.Count)
            throw new InvalidOperationException("保存されたアカウントの並び順が不正です。");
        return order;
    }

    public T? Read<T>(string kind, string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT json FROM documents WHERE kind=$kind AND id=$id";
        cmd.Parameters.AddWithValue("$kind", kind); cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string value ? JsonSerializer.Deserialize<T>(value, Json) : default;
    }

    public void Write<T>(string kind, string id, T value)
    {
        using var c = Open();
        WriteDocument(c, null, kind, id, value);
    }

    public void Remove(string kind, string id)
    {
        using var c = Open();
        Execute(c, null, "DELETE FROM documents WHERE kind=$kind AND id=$id", ("$kind", kind), ("$id", id));
    }

    private static void WriteDocument<T>(SqliteConnection c, SqliteTransaction? tx, string kind, string id, T value) =>
        Execute(c, tx, "INSERT INTO documents VALUES($kind,$id,$json) ON CONFLICT(kind,id) DO UPDATE SET json=excluded.json",
            ("$kind", kind), ("$id", id), ("$json", JsonSerializer.Serialize(value, Json)));

    // Only the latest observation is kept: this app shows the current rank, not a history.
    public RankSnapshot? LatestRanks(Guid id) => Read<RankSnapshot>("rank", id.ToString());
    public void SaveRanks(Guid id, RankSnapshot ranks) => Write("rank", id.ToString(), ranks);

    public void Save(RiotAccount account, Credentials? credentials = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account.Label);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.GameName);
        ArgumentException.ThrowIfNullOrWhiteSpace(account.TagLine);
        Regions.Validate(account.Platform);
        byte[]? cipher = null;
        if (credentials != null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Username);
            ArgumentException.ThrowIfNullOrWhiteSpace(credentials.Password);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(credentials, Json);
            try { cipher = protector.Protect(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        using var c = Open(); using var tx = c.BeginTransaction();
        var key = account.Id.ToString();
        var previous = ReadAccounts(c, tx).SingleOrDefault(a => a.Id == account.Id);
        var identityChanged = previous != null &&
            (!string.Equals(previous.Platform, account.Platform, StringComparison.OrdinalIgnoreCase) ||
             previous.GameName != account.GameName || previous.TagLine != account.TagLine);
        WriteDocument(c, tx, "account", key, account);
        // The saved rank and the OP.GG lookup belong to the old Riot ID or server.
        if (identityChanged) Execute(c, tx, "DELETE FROM documents WHERE kind IN ('rank','opgg') AND id=$id", ("$id", key));
        if (cipher != null) Execute(c, tx, "INSERT INTO secrets VALUES($id,$cipher) ON CONFLICT(id) DO UPDATE SET cipher=excluded.cipher",
            ("$id", key), ("$cipher", cipher));
        var order = ReadAccountOrder(c, tx) ?? [];
        if (!order.Contains(account.Id))
        {
            order.Add(account.Id);
            WriteDocument(c, tx, "setting", AccountOrderKey, order);
        }
        tx.Commit();
    }

    public Credentials Credentials(Guid id) => JsonSerializer.Deserialize<Credentials>(GetSecret(id.ToString()) ?? throw new InvalidOperationException("ログイン情報を登録してください。"), Json)!;
    private string? GetSecret(string id)
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT cipher FROM secrets WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        if (cmd.ExecuteScalar() is not byte[] cipher) return null;
        var bytes = protector.Unprotect(cipher);
        try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    // Removes the account with its credentials, saved rank and OP.GG lookup (every document keyed by its id).
    public void Delete(Guid id)
    {
        using var c = Open(); using var tx = c.BeginTransaction();
        Execute(c, tx, "DELETE FROM documents WHERE id=$id; DELETE FROM secrets WHERE id=$id;", ("$id", id.ToString()));
        var order = ReadAccountOrder(c, tx);
        if (order != null && order.Remove(id)) WriteDocument(c, tx, "setting", AccountOrderKey, order);
        tx.Commit();
    }
}
