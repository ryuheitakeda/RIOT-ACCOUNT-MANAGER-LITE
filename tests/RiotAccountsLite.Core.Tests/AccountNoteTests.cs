using RiotAccountsLite.Core;
using Xunit;

public sealed class AccountNoteTests
{
    [Fact]
    public void NoteRoundTripsAndSurvivesRiotIdChanges()
    {
        using var fixture = new StoreFixture();
        var account = new RiotAccount(Guid.NewGuid(), "Main", "Player", "JP1", "JP1", "サブ用\n2行目");
        fixture.Store.Save(account);
        Assert.Equal("サブ用\n2行目", new Store(fixture.File, fixture.Protector).Accounts().Single().Note);
        fixture.Store.Save(account with { GameName = "Renamed" });
        Assert.Equal("サブ用\n2行目", fixture.Store.Accounts().Single().Note);
        fixture.Store.Save(account with { Note = null });
        Assert.Null(fixture.Store.Accounts().Single().Note);
    }

    [Fact]
    public void AccountSavedWithoutNoteLoadsWithoutNote()
    {
        using var fixture = new StoreFixture();
        var id = Guid.NewGuid();
        fixture.Store.Save(new RiotAccount(id, "Main", "Player", "JP1", "JP1"));
        using (var c = fixture.Open())
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE documents SET json=$json WHERE kind='account' AND id=$id";
            cmd.Parameters.AddWithValue("$json", $$"""{"id":"{{id}}","label":"Main","gameName":"Player","tagLine":"JP1","platform":"JP1"}""");
            cmd.Parameters.AddWithValue("$id", id.ToString());
            Assert.Equal(1, cmd.ExecuteNonQuery());
        }
        var loaded = fixture.Store.Accounts().Single();
        Assert.Null(loaded.Note);
        Assert.Equal("Main", loaded.Label);
        Assert.Equal("Player#JP1", loaded.RiotId);
    }
}
