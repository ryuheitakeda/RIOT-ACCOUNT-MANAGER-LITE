using System.Security.Cryptography;
using System.Windows.Automation;
using Microsoft.Data.Sqlite;

namespace RiotAccountsLite.App;

internal static class DialogLayout
{
    public static StackPanel Form(Window window, string title, double width = 480)
    {
        window.Title = title;
        window.Style = (Style)System.Windows.Application.Current.FindResource(typeof(Window));
        window.Width = width;
        window.SizeToContent = SizeToContent.Height;
        window.MaxHeight = SystemParameters.WorkArea.Height - 60;
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.ResizeMode = ResizeMode.NoResize;
        var form = new StackPanel { Margin = new Thickness(24) };
        window.Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        return form;
    }

    public static void Field(Panel panel, string name, Control input)
    {
        AutomationProperties.SetName(input, name);
        panel.Children.Add(new Label { Content = name, Target = input, Padding = new Thickness(0) });
        panel.Children.Add(input);
    }

    public static TextBlock Hint(string text) => new() { Text = text, Margin = new Thickness(0, 4, 0, 16) };
}

public sealed class AccountDialog : Window
{
    public Guid SavedId { get; private set; }

    public AccountDialog(Store store, RiotAccount? account)
    {
        var form = DialogLayout.Form(this, account == null ? "アカウントを追加" : "アカウントを編集");
        var label = new TextBox { Text = account?.Label ?? "" };
        var username = new TextBox();
        var password = new PasswordBox();
        var gameName = new TextBox { Text = account?.GameName ?? "" };
        var tag = new TextBox { Text = account?.TagLine ?? "" };
        var platform = new ComboBox { ItemsSource = Regions.Platforms, SelectedItem = account?.Platform ?? "JP1" };
        var note = new TextBox { Text = account?.Note ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64, MaxHeight = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        if (account != null) username.Text = store.Credentials(account.Id).Username;
        DialogLayout.Field(form, "管理名", label);
        DialogLayout.Field(form, "ログインID（Riot IDとは別）", username);
        DialogLayout.Field(form, account == null ? "パスワード" : "パスワード（空欄なら変更しません）", password);
        DialogLayout.Field(form, "Riot ID：ゲーム内の名前", gameName);
        DialogLayout.Field(form, "Riot ID：タグ（#を除く）", tag);
        DialogLayout.Field(form, "サーバー", platform);
        DialogLayout.Field(form, "メモ（任意）", note);
        form.Children.Add(DialogLayout.Hint("メモは暗号化せずに保存します。パスワードなどのログイン情報は書かないでください。"));
        if (account != null) form.Children.Add(DialogLayout.Hint("Riot ID・サーバーを変更すると、このアカウントの保存済みランクを消去し、次回の更新で取得し直します。"));
        form.Children.Add(DialogLayout.Hint("ログイン情報は、このWindowsユーザーだけが復号できる形式で保存します。"));
        var error = DialogLayout.Hint("");
        error.Foreground = System.Windows.Media.Brushes.Firebrick;
        form.Children.Add(error);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        var save = new Button { Content = "保存", IsDefault = true, Style = (Style)FindResource("Primary") };
        cancel.Click += (_, _) => DialogResult = false;
        save.Click += (_, _) =>
        {
            try
            {
                var actualPassword = password.Password;
                if (account != null && actualPassword.Length == 0) actualPassword = store.Credentials(account.Id).Password;
                SavedId = account?.Id ?? Guid.NewGuid();
                store.Save(new RiotAccount(SavedId, label.Text.Trim(), gameName.Text.Trim(), tag.Text.Trim().TrimStart('#'), platform.SelectedItem as string ?? "",
                    string.IsNullOrWhiteSpace(note.Text) ? null : note.Text.Trim()), new Credentials(username.Text.Trim(), actualPassword));
                password.Clear();
                DialogResult = true;
            }
            catch (ArgumentException) { error.Text = "管理名・ログインID・パスワード・Riot ID・サーバーを入力してください。"; }
            catch (Exception ex) when (ex is CryptographicException or SqliteException or InvalidOperationException)
            { error.Text = "保存できませんでした。保存先の空き容量・アクセス権を確認してください。"; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); form.Children.Add(buttons);
        Loaded += (_, _) => label.Focus();
    }
}
