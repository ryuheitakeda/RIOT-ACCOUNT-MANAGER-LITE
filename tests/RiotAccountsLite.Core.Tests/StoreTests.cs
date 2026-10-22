using System.Security.Cryptography;
using System.Text;
using RiotAccountsLite.Core;
using Xunit;

public sealed class StoreTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
    private static RiotAccount Account(string label = "Main") => new(Guid.NewGuid(), label, "Player", "JP1", "JP1");
    private static RankSnapshot Ranks() => new(Now, [new(Queues.Solo, "GOLD", "II", 45, 30, 25)], Now.AddHours(-3));
    private static OpggIdentity Lookup() => new("jp", "Player", "JP1", "sid-1");

    [Fact]
    public void AccountsCredentialsRanksAndLookupSurviveARestartWithoutPlaintextSecrets()
    {
        using var fixture = new StoreFixture(); var account = Account() with { Note = "メモ" };
        fixture.Store.Save(account, new("login-private", "password-private-123"));
        fixture.Store.SaveRanks(account.Id, Ranks());
        fixture.Store.Write("opgg", account.Id.ToString(), Lookup());
        var restarted = new Store(fixture.File, fixture.Protector);
        Assert.Equal(account, Assert.Single(restarted.Accounts()));
        Assert.Equal(new Credentials("login-private", "password-private-123"), restarted.Credentials(account.Id));
        var ranks = restarted.LatestRanks(account.Id);
        Assert.NotNull(ranks);
        Assert.Equal(Now, ranks.ObservedAt);
        Assert.Equal(Now.AddHours(-3), ranks.OpggUpdatedAt);
        Assert.Equal(new RankEntry(Queues.Solo, "GOLD", "II", 45, 30, 25), Assert.Single(ranks.Entries));
        Assert.Equal(Lookup(), OpggRankProvider.Identity(restarted, account.Id));
        foreach (var file in Directory.EnumerateFiles(fixture.Directory))
        {
            var content = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.DoesNotContain("password-private-123", content);
            Assert.DoesNotContain("login-private", content);
        }
    }

    [Fact]
    public void LabelEditKeepsRanksButRiotIdOrServerEditClearsThem()
    {
        using var fixture = new StoreFixture(); var account = Account();
        fixture.Store.Save(account, new("login", "pass"));
        void Seed() { fixture.Store.SaveRanks(account.Id, Ranks()); fixture.Store.Write("opgg", account.Id.ToString(), Lookup()); }
        Seed();
        fixture.Store.Save(account with { Label = "New label", Note = "memo" }, new("login2", "pass2"));
        Assert.NotNull(fixture.Store.LatestRanks(account.Id));
        Assert.NotNull(OpggRankProvider.Identity(fixture.Store, account.Id));
        foreach (var edited in new[] { account with { GameName = "Different" }, account with { GameName = "Different", TagLine = "JP2" }, account with { GameName = "Different", TagLine = "JP2", Platform = "KR" } })
        {
            fixture.Store.Save(edited);
            Assert.Null(fixture.Store.LatestRanks(account.Id));
            Assert.Null(OpggRankProvider.Identity(fixture.Store, account.Id));
            Seed();
        }
        Assert.Equal("pass2", fixture.Store.Credentials(account.Id).Password);
    }

    [Fact]
    public void EncryptionFailureSavesNothing()
    {
        using var fixture = new StoreFixture(); var account = Account(); fixture.Store.Save(account, new("login", "pass"));
        fixture.Protector.Fail = true;
        Assert.Throws<CryptographicException>(() => fixture.Store.Save(account with { Label = "should-not-save" }, new("new", "new")));
        Assert.Equal("Main", Assert.Single(fixture.Store.Accounts()).Label);
        fixture.Protector.Fail = false;
        Assert.Equal("pass", fixture.Store.Credentials(account.Id).Password);
    }

    [Fact]
    public void DeleteRemovesOnlyThatAccountsCredentialsRanksAndLookup()
    {
        using var fixture = new StoreFixture(); var account = Account(); var other = Account("Other");
        fixture.Store.Save(account, new("login", "pass")); fixture.Store.Save(other, new("login2", "pass2"));
        fixture.Store.SaveRanks(account.Id, Ranks()); fixture.Store.SaveRanks(other.Id, Ranks());
        fixture.Store.Write("opgg", account.Id.ToString(), Lookup());
        fixture.Store.Delete(account.Id);
        Assert.Equal(other.Id, Assert.Single(fixture.Store.Accounts()).Id);
        Assert.Null(fixture.Store.LatestRanks(account.Id));
        Assert.Null(OpggRankProvider.Identity(fixture.Store, account.Id));
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Credentials(account.Id));
        Assert.NotNull(fixture.Store.LatestRanks(other.Id));
        Assert.Equal("pass2", fixture.Store.Credentials(other.Id).Password);
    }

    [Theory]
    [InlineData("PBE1")]
    [InlineData("")]
    public void UnknownServerIsRejectedAndNothingIsWritten(string platform)
    {
        using var fixture = new StoreFixture();
        Assert.Throws<ArgumentException>(() => fixture.Store.Save(Account() with { Platform = platform }, new("login", "pass")));
        Assert.Empty(fixture.Store.Accounts());
        using var c = fixture.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM secrets";
        Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void SchemaHasOnlyDocumentsAndSecretsAtVersionOne()
    {
        using var fixture = new StoreFixture();
        using var c = fixture.Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        Assert.Equal((long)Store.SchemaVersion, (long)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT group_concat(name, ',') FROM (SELECT name FROM sqlite_master WHERE type='table' ORDER BY name)";
        Assert.Equal("documents,secrets", (string)cmd.ExecuteScalar()!);
    }

    [Fact]
    public void DatabaseOfTheFullAppIsNeitherOpenedNorChanged()
    {
        var directory = Path.Combine(Path.GetTempPath(), "riot-lite-foreign-" + Guid.NewGuid());
        var file = Path.Combine(directory, "accounts.db");
        Directory.CreateDirectory(directory);
        try
        {
            using (var c = StoreFixture.OpenFile(file))
            using (var cmd = c.CreateCommand())
            {
                // Same table name and schema version as the full Riot Accounts app's database.
                cmd.CommandText = "CREATE TABLE documents(kind TEXT NOT NULL,id TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(kind,id)); PRAGMA user_version=2;";
                cmd.ExecuteNonQuery();
            }
            var error = Assert.Throws<InvalidOperationException>(() => new Store(file, new TestProtector()));
            Assert.Contains("このアプリのデータではない", error.Message);
            using (var c = StoreFixture.OpenFile(file))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA user_version";
                Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
                cmd.CommandText = "PRAGMA journal_mode";
                Assert.Equal("delete", (string)cmd.ExecuteScalar()!);
                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name='secrets'";
                Assert.Equal(0L, (long)cmd.ExecuteScalar()!);
            }
        }
        finally { Directory.Delete(directory, true); }
    }
}
