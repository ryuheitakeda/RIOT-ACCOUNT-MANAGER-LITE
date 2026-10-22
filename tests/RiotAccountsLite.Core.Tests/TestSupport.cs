using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RiotAccountsLite.Core;

sealed class InlineProgress(Action<string> action) : IProgress<string> { public void Report(string value) => action(value); }
// Test-only protector: production uses Windows DPAPI. AES lets portable tests inspect SQLite for plaintext leakage.
sealed class TestProtector : ISecretProtector
{
    private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    public bool Fail { get; set; }
    public byte[] Protect(byte[] data)
    {
        if (Fail) throw new CryptographicException("Test encryption failure");
        var result = new byte[28 + data.Length]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16); aes.Encrypt(result.AsSpan(0, 12), data, result.AsSpan(28), result.AsSpan(12, 16)); return result;
    }
    public byte[] Unprotect(byte[] data)
    {
        var result = new byte[data.Length - 28]; using var aes = new AesGcm(key, 16);
        aes.Decrypt(data.AsSpan(0, 12), data.AsSpan(28), data.AsSpan(12, 16), result); return result;
    }
}
sealed class StoreFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "riot-lite-tests-" + Guid.NewGuid());
    public string File => Path.Combine(Directory, "accounts.db");
    public TestProtector Protector { get; } = new();
    public Store Store { get; }
    public StoreFixture() { Store = new(File, Protector); }
    public SqliteConnection Open() => OpenFile(File);
    public static SqliteConnection OpenFile(string file) { var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()); c.Open(); return c; }
    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}
