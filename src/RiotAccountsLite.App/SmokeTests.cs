using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace RiotAccountsLite.App;

/// <summary>Offline checks using a temporary database, generated dummy secrets and a fake OP.GG only.</summary>
internal static class SmokeTests
{
    public static string Run(string? artifactDirectory = null)
    {
        var report = new List<string>
        {
            $"Riot Accounts Lite offline Windows checks — {DateTimeOffset.UtcNow:O}",
            $"OS: {Environment.OSVersion.VersionString}; runtime: {Environment.Version}"
        };
        var directory = Path.Combine(Path.GetTempPath(), "RiotAccountsLite-self-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            Check(report, "DPAPI CurrentUser roundtrip, tamper rejection and separation from the full app", TestDpapi);
            Check(report, "SQLite persist/reopen/edit/delete and encrypted database/WAL", () => TestStore(directory));
            Check(report, "WPF main window and account dialogs construct with dummy data", () => TestWindows(directory, artifactDirectory));
            Check(report, "Full rank refresh through a fake OP.GG continues past a failure and calls only the OP.GG host", () => TestRefresh(directory, artifactDirectory));
        }
        catch (Exception exception)
        {
            report.Add($"FAIL test setup ({exception.GetType().Name})");
        }
        finally
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception)
            {
                report.Add($"FAIL temporary test data cleanup ({exception.GetType().Name})");
            }
        }
        report.Add("MANUAL: clipboard expiry, OP.GGで開く (browser), live OP.GG comparison, visual/DPI check.");
        return string.Join(Environment.NewLine, report) + Environment.NewLine;
    }

    private static void Check(List<string> report, string name, Action test)
    {
        try { test(); report.Add("PASS " + name); }
        catch (Exception exception) { report.Add($"FAIL {name} ({exception.GetType().Name}): {exception.Message}"); }
    }

    private static void Require(bool condition, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(condition))] string? expression = null)
    {
        if (!condition) throw new InvalidOperationException("Self-test assertion failed: " + expression);
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    private static T Control<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException("Missing self-test control: " + name);

    private static void TestDpapi()
    {
        Require(Path.GetFileName(App.DataFolder) == "RiotAccountsLite");
        var protector = new WindowsProtector();
        var plain = RandomNumberGenerator.GetBytes(64);
        byte[]? restored = null;
        try
        {
            var cipher = protector.Protect(plain);
            Require(!cipher.AsSpan().SequenceEqual(plain));
            restored = protector.Unprotect(cipher);
            Require(restored.AsSpan().SequenceEqual(plain));
            // The full Riot Accounts app's entropy must not open this app's secrets.
            try
            {
                var foreign = ProtectedData.Unprotect(cipher, Encoding.UTF8.GetBytes("RiotAccounts/v1"), DataProtectionScope.CurrentUser);
                CryptographicOperations.ZeroMemory(foreign);
                throw new InvalidOperationException("The full app's entropy decrypted this app's data.");
            }
            catch (CryptographicException) { }
            cipher[cipher.Length / 2] ^= 0x40;
            try
            {
                var unexpected = protector.Unprotect(cipher);
                CryptographicOperations.ZeroMemory(unexpected);
                throw new InvalidOperationException("Modified DPAPI data was accepted.");
            }
            catch (CryptographicException) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (restored != null) CryptographicOperations.ZeroMemory(restored);
        }
    }

    private static void TestStore(string directory)
    {
        var database = Path.Combine(directory, "accounts.db");
        var protector = new WindowsProtector();
        var store = new Store(database, protector);
        var marker = Guid.NewGuid().ToString("N");
        var credentials = new Credentials("dummy-user-" + marker, "dummy-password-" + marker);
        var replacement = new Credentials("dummy-new-user-" + marker, "dummy-new-password-" + marker);
        var account = new RiotAccount(Guid.NewGuid(), "検証アカウント", "Dummy Player", "JP1", "JP1");

        // Keep an independent connection alive so the WAL can be inspected before checkpoint/cleanup.
        using (var observer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        {
            observer.Open();
            using (var keepWal = observer.CreateCommand())
            {
                // Opening SQLite alone does not initialize its pager. Hold a read snapshot
                // so the real writes remain in WAL even when Store disables pooling.
                keepWal.CommandText = "BEGIN; SELECT COUNT(*) FROM documents;";
                _ = keepWal.ExecuteScalar();
            }
            using (var version = observer.CreateCommand())
            {
                version.CommandText = "PRAGMA user_version";
                Require(Convert.ToInt32(version.ExecuteScalar()) == Store.SchemaVersion);
            }
            store.Save(account, credentials);
            store.SaveRanks(account.Id, new(DateTimeOffset.UtcNow, [new(Queues.Solo, "GOLD", "IV", 25, 3, 2)]));
            SqliteConnection.ClearAllPools();
            var reopened = new Store(database, protector);
            Require(reopened.Accounts().Single() == account);
            Require(reopened.Credentials(account.Id) == credentials);
            Require(reopened.LatestRanks(account.Id)?.Entries.Single().Tier == "GOLD");

            var edited = account with { Label = "編集したアカウント" };
            reopened.Save(edited);
            Require(reopened.Accounts().Single().Label == edited.Label);
            Require(reopened.Credentials(account.Id) == credentials);
            Require(reopened.LatestRanks(account.Id) != null);
            reopened.Save(edited, replacement);
            Require(new Store(database, protector).Credentials(account.Id) == replacement);

            var files = Directory.GetFiles(directory, "accounts.db*");
            Require(files.Any(file => file.EndsWith("-wal", StringComparison.Ordinal)));
            foreach (var file in files)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var bytes = buffer.ToArray();
                foreach (var secret in new[] { credentials.Username, credentials.Password, replacement.Username, replacement.Password })
                {
                    Require(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(secret)) < 0);
                    Require(bytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret)) < 0);
                }
            }

            reopened.Delete(account.Id);
            var afterDelete = new Store(database, protector);
            Require(afterDelete.Accounts().Count == 0);
            Require(afterDelete.LatestRanks(account.Id) == null);
            Require(Throws<InvalidOperationException>(() => afterDelete.Credentials(account.Id)));
        }
    }

    private static void TestWindows(string directory, string? artifactDirectory)
    {
        var application = System.Windows.Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var store = new Store(Path.Combine(directory, "ui.db"), new WindowsProtector());
            Construct(() => new MainWindow(store, directory), artifactDirectory, "self-test-empty.png", window =>
            {
                Require(Control<Border>(window, "EmptyPanel").Visibility == Visibility.Visible);
                Require(Control<ScrollViewer>(window, "DetailPanel").Visibility == Visibility.Collapsed);
            });
            Construct(() => new AccountDialog(store, null), artifactDirectory, "self-test-account-add.png");
            var account = new RiotAccount(Guid.NewGuid(), "表示検証", "Dummy", "JP1", "JP1", "サブ用のメモ");
            store.Save(account, new Credentials("dummy-ui-user", "dummy-ui-password"));
            store.SaveRanks(account.Id, new(DateTimeOffset.UtcNow, [new(Queues.Solo, "GOLD", "IV", 25, 3, 2)], DateTimeOffset.UtcNow.AddHours(-2)));
            Construct(() => new MainWindow(store, directory), artifactDirectory, "self-test-sample.png", window =>
            {
                Require(Control<ScrollViewer>(window, "DetailPanel").Visibility == Visibility.Visible);
                Require(((AccountItem)Control<ListBox>(window, "AccountsList").SelectedItem).Summary == "Solo GOLD IV  25 LP");
                Require(Control<TextBlock>(window, "AccountTitle").Text == "表示検証");
                Require(Control<TextBlock>(window, "AccountIdentity").Text == "Dummy#JP1  /  JP1");
                Require(Control<TextBlock>(window, "SoloRank").Text == "GOLD IV  25 LP");
                Require(Control<TextBlock>(window, "SoloRecord").Text.StartsWith("3勝 2敗", StringComparison.Ordinal));
                Require(Control<TextBlock>(window, "FlexRank").Text == "UNRANKED");
                Require(Control<TextBlock>(window, "RankTime").Text.Contains("OP.GG側のデータ更新", StringComparison.Ordinal));
            });
            Construct(() => new AccountDialog(store, account), artifactDirectory, "self-test-account-edit.png");
        }
        finally { application.ShutdownMode = shutdownMode; }
    }

    private static void TestRefresh(string directory, string? artifactDirectory)
    {
        var application = System.Windows.Application.Current;
        var shutdownMode = application.ShutdownMode;
        application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var store = new Store(Path.Combine(directory, "refresh.db"), new WindowsProtector());
            var found = new RiotAccount(Guid.NewGuid(), "01 メイン", "Dummy", "JP1", "JP1");
            var missing = new RiotAccount(Guid.NewGuid(), "02 サブ", "Missing", "JP1", "JP1");
            store.Save(found, new Credentials("dummy-user-1", "dummy-password-1"));
            store.Save(missing, new Credentials("dummy-user-2", "dummy-password-2"));
            var opgg = new FakeOpgg();
            Construct(() => new MainWindow(store, directory, opgg, TimeSpan.Zero), artifactDirectory, "self-test-refresh.png", window =>
            {
                Wait(((MainWindow)window).RefreshAllAsync());
                var items = Control<ListBox>(window, "AccountsList").Items.Cast<AccountItem>().ToList();
                Require(items.Count == 2);
                Require(items[0].Summary == "Solo GOLD II  45 LP" && items[0].Failure == null);
                Require(items[1].Summary == "ランク未取得" && items[1].Failure?.Contains("見つかりません", StringComparison.Ordinal) == true);
                var status = Control<TextBlock>(window, "StatusText").Text;
                Require(status.Contains("1/2", StringComparison.Ordinal) && status.Contains("02 サブ", StringComparison.Ordinal));
                Require(Control<Button>(window, "CancelButton").Visibility == Visibility.Collapsed);
                Require(opgg.Hosts.Count == 3 && opgg.Hosts.All(host => host == OpggApi.SummonerHost));
                Require(store.LatestRanks(found.Id)?.OpggUpdatedAt != null);
            });
        }
        finally { application.ShutdownMode = shutdownMode; }
    }

    private static void Construct(Func<Window> create, string? artifactDirectory, string? screenshot = null, Action<Window>? verify = null)
    {
        var window = create();
        try
        {
            Require(window.Content != null);
            verify?.Invoke(window);
            if (artifactDirectory != null && screenshot != null)
            {
                Directory.CreateDirectory(artifactDirectory);
                Render(window, Path.Combine(artifactDirectory, screenshot));
            }
        }
        finally { window.Close(); }
    }

    // Pumps the dispatcher instead of blocking it: the window's async work continues on this thread.
    private static void Wait(Task task)
    {
        var frame = new DispatcherFrame();
        var dispatcher = Dispatcher.CurrentDispatcher;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var stop = timeout.Token.Register(() => dispatcher.InvokeAsync(() => frame.Continue = false));
        _ = task.ContinueWith(_ => dispatcher.InvokeAsync(() => frame.Continue = false), TaskScheduler.Default);
        if (!task.IsCompleted) Dispatcher.PushFrame(frame);
        if (!task.IsCompleted) throw new TimeoutException("The refresh did not finish within 30 seconds.");
        task.GetAwaiter().GetResult();
    }

    // Answers like OP.GG with synthetic data: "Dummy#JP1" exists with Solo GOLD II, every other Riot ID is unknown.
    private sealed class FakeOpgg : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Hosts.Add(uri.Host);
            var body = uri.AbsolutePath switch
            {
                "/api/v3/jp/summoners" => uri.Query.Contains("riot_id=Dummy%23JP1", StringComparison.Ordinal)
                    ? """{"data":[{"game_name":"Dummy","tagline":"JP1","summoner_id":"dummy-sid"}]}"""
                    : """{"data":[]}""",
                "/api/jp/summoners/dummy-sid/summary" =>
                    """{"data":{"summoner":{"updated_at":"2026-10-22T09:00:00+09:00","league_stats":[{"game_type":"SOLORANKED","win":30,"lose":25,"tier_info":{"tier":"GOLD","division":2,"lp":45}}]}}}""",
                _ => null
            };
            return Task.FromResult(body == null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static void Render(Window window, string file)
    {
        var width = (int)window.Width;
        var maximumHeight = window is MainWindow ? (int)window.Height : (int)Math.Min(850, window.MaxHeight);
        var content = (FrameworkElement)window.Content;
        // Render an isolated visual tree; no native window, screen capture, or focus changes.
        window.Content = null;
        var surface = new Border { Background = window.Background, Child = content, Width = width, FlowDirection = window.FlowDirection, Language = window.Language };
        try
        {
            System.Windows.Documents.TextElement.SetFontFamily(surface, window.FontFamily);
            System.Windows.Documents.TextElement.SetFontSize(surface, window.FontSize);
            System.Windows.Documents.TextElement.SetForeground(surface, window.Foreground);
            System.Windows.Documents.TextElement.SetFontWeight(surface, window.FontWeight);
            System.Windows.Documents.TextElement.SetFontStyle(surface, window.FontStyle);
            System.Windows.Documents.TextElement.SetFontStretch(surface, window.FontStretch);
            if (window is MainWindow) surface.Height = maximumHeight;
            surface.ApplyTemplate();
            surface.Measure(new Size(width, maximumHeight));
            var height = Math.Max(1, Math.Min(maximumHeight, (int)Math.Ceiling(surface.DesiredSize.Height)));
            surface.Height = height;
            surface.Measure(new Size(width, height));
            surface.Arrange(new Rect(0, 0, width, height));
            surface.UpdateLayout();
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            image.Render(surface);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            using var output = File.Create(file);
            encoder.Save(output);
        }
        finally
        {
            surface.Child = null;
            window.Content = content;
        }
    }
}
