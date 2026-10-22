using System.Text.Json;
using RiotAccountsLite.Core;
using Xunit;

public sealed class AccountOrderTests
{
    private static RiotAccount Account(string label) => new(Guid.NewGuid(), label, "Player " + label, "JP1", "JP1");
    private static Guid[] Ids(Store store) => store.Accounts().Select(a => a.Id).ToArray();
    private static Store Reopen(StoreFixture fixture) => new(fixture.File, fixture.Protector);

    [Fact]
    public void AccountsKeepRegistrationOrderNotLabelOrder()
    {
        using var fixture = new StoreFixture();
        var accounts = new[] { Account("Delta"), Account("Alpha"), Account("Charlie"), Account("Bravo") };
        foreach (var account in accounts) fixture.Store.Save(account);
        Assert.Equal(accounts.Select(a => a.Id), Ids(Reopen(fixture)));
    }

    [Fact]
    public void EditsKeepThePositionAndDeletionKeepsTheOthersInPlace()
    {
        using var fixture = new StoreFixture();
        var accounts = new[] { Account("Delta"), Account("Alpha"), Account("Charlie") };
        foreach (var account in accounts) fixture.Store.Save(account);
        fixture.Store.Save(accounts[0] with { Label = "Aardvark" }, new("test-user", "test-password"));
        fixture.Store.Save(accounts[2] with { GameName = "Renamed", Platform = "KR" });
        Assert.Equal(accounts.Select(a => a.Id), Ids(Reopen(fixture)));
        fixture.Store.Delete(accounts[1].Id);
        var newest = Account("Zulu");
        fixture.Store.Save(newest);
        var expected = new[] { accounts[0].Id, accounts[2].Id, newest.Id };
        Assert.Equal(expected, Ids(Reopen(fixture)));
        Assert.Equal(expected, fixture.Store.Read<List<Guid>>("setting", "accountOrder"));
        Assert.Equal(new Credentials("test-user", "test-password"), fixture.Store.Credentials(accounts[0].Id));
    }

    [Fact]
    public void AccountsMissingFromTheOrderComeLastByLabel()
    {
        using var fixture = new StoreFixture();
        var accounts = new[] { Account("Charlie"), Account("Alpha"), Account("Bravo") };
        foreach (var account in accounts) fixture.Store.Save(account);
        fixture.Store.Write("setting", "accountOrder", new List<Guid> { accounts[2].Id });
        Assert.Equal(new[] { accounts[2].Id, accounts[1].Id, accounts[0].Id }, Ids(fixture.Store));
    }

    [Fact]
    public void BrokenOrderIsReported()
    {
        using var fixture = new StoreFixture();
        var account = Account("Alpha");
        fixture.Store.Save(account);
        fixture.Store.Write("setting", "accountOrder", new List<Guid> { account.Id, account.Id });
        Assert.Throws<InvalidOperationException>(() => fixture.Store.Accounts());
    }

    [Fact]
    public void OrderWriteFailureRollsBackAccountAdditionAndDeletion()
    {
        using var fixture = new StoreFixture();
        var accounts = new[] { Account("Alpha"), Account("Bravo") };
        foreach (var account in accounts) fixture.Store.Save(account, new("test-user", "test-password"));
        using (var connection = fixture.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_account_order BEFORE INSERT ON documents
                WHEN NEW.kind='setting' AND NEW.id='accountOrder'
                BEGIN SELECT RAISE(ABORT, 'simulated order write failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        var before = Snapshot(fixture);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Store.Save(Account("New"), new("new-user", "new-password")));
        Assert.Equal(before, Snapshot(fixture));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => fixture.Store.Delete(accounts[0].Id));
        Assert.Equal(before, Snapshot(fixture));
    }

    // Compare persisted rows, including encrypted bytes, rather than reserialized model values.
    private static string[] Snapshot(StoreFixture fixture)
    {
        using var connection = fixture.Open();
        var result = new List<string>();
        foreach (var query in new[] { "SELECT kind,id,json FROM documents ORDER BY kind,id", "SELECT id,hex(cipher) FROM secrets ORDER BY id" })
        {
            using var command = connection.CreateCommand(); command.CommandText = query;
            using var rows = command.ExecuteReader();
            while (rows.Read()) result.Add(JsonSerializer.Serialize(Enumerable.Range(0, rows.FieldCount).Select(rows.GetString)));
        }
        return result.ToArray();
    }
}
