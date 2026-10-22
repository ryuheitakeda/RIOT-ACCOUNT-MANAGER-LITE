using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace RiotAccountsLite.App;
public sealed record AccountItem(RiotAccount Account,RankSnapshot? Ranks,string? Failure)
{
    public string Label=>Account.Label;
    public string RiotId=>Account.RiotId;
    public string Platform=>Account.Platform;
    public string Summary=>RankText.Summary(Ranks);
    public Visibility FailureVisibility=>Failure==null?Visibility.Collapsed:Visibility.Visible;
}
public partial class MainWindow:Window
{
    private readonly Store store;
    private readonly string folder;
    private readonly HttpClient http;
    private readonly OpggApi opggApi;
    private readonly OpggRankProvider provider;
    private readonly DiagnosticLog log;
    private CancellationTokenSource? operation;
    private bool initialized;
    private bool closeRequested;
    // The last failure per account in this session; a later success, an edit or a deletion clears it.
    private readonly Dictionary<Guid,string> failures=[];
    private RiotAccount? Selected=>(AccountsList.SelectedItem as AccountItem)?.Account;
    public MainWindow(Store store,string folder):this(store,folder,new HttpClientHandler{AllowAutoRedirect=false,AutomaticDecompression=DecompressionMethods.All},null){}
    // The handler and the request spacing are replaceable for the offline self-test; the app itself only ever calls OP.GG.
    internal MainWindow(Store store,string folder,HttpMessageHandler handler,TimeSpan? opggInterval)
    {
        this.store=store;this.folder=folder;
        http=new(handler){Timeout=TimeSpan.FromSeconds(30)};
        log=new(Path.Combine(folder,"logs","diagnostics.log"));
        opggApi=new(http,opggInterval,log);provider=new(store,opggApi);
        InitializeComponent();initialized=true;Reload();
        Closing+=(_,e)=>{if(operation!=null){operation.Cancel();closeRequested=true;e.Cancel=true;StatusText.Text="処理を中止しています。終了後に閉じます。";}};
        Closed+=(_,_)=>{ClipboardLease.ClearOwned();opggApi.Dispose();http.Dispose();};
    }
    private void Reload(Guid? select=null)
    {
        var selectedId=select??Selected?.Id;
        var list=store.Accounts().Select(a=>new AccountItem(a,store.LatestRanks(a.Id),failures.GetValueOrDefault(a.Id))).ToList();
        AccountsList.ItemsSource=list;AccountsList.SelectedItem=list.FirstOrDefault(a=>a.Account.Id==selectedId)??list.FirstOrDefault();Render();
    }
    private void Render()
    {
        if(!initialized)return;
        var item=AccountsList.SelectedItem as AccountItem;
        EmptyPanel.Visibility=item==null?Visibility.Visible:Visibility.Collapsed;DetailPanel.Visibility=item==null?Visibility.Collapsed:Visibility.Visible;if(item==null)return;
        var a=item.Account;
        AccountTitle.Text=a.Label;AccountIdentity.Text=$"{a.RiotId}  /  {a.Platform}";
        AccountNote.Text=a.Note??"";AccountNote.Visibility=a.Note==null?Visibility.Collapsed:Visibility.Visible;
        (SoloRank.Text,SoloRecord.Text)=RankText.Queue(item.Ranks,Queues.Solo);
        (FlexRank.Text,FlexRecord.Text)=RankText.Queue(item.Ranks,Queues.Flex);
        RankTime.Text=item.Ranks is not{} ranks?"まだ取得していません。「ランク更新」でOP.GGから取得します。"
            :$"取得 {ranks.ObservedAt.LocalDateTime:yyyy/MM/dd HH:mm}"+(ranks.OpggUpdatedAt is{} updated?$"（OP.GG側のデータ更新 {updated.LocalDateTime:yyyy/MM/dd HH:mm}）":"");
        RankError.Text=item.Failure??"";RankError.Visibility=item.FailureVisibility;
    }
    private async Task Run(Func<IProgress<string>,CancellationToken,Task> action)
    {
        if(operation!=null){StatusText.Text="処理中です。終了するか「中止」を押してください。";return;}
        operation=new();CancelButton.Visibility=Visibility.Visible;
        var status=new StatusProgress(StatusText);
        try{await action(status,operation.Token);}
        catch(OperationCanceledException){StatusText.Text=operation.IsCancellationRequested?"処理を中止しました。取得済みのランクは保存されています。":"処理が中止またはタイムアウトしました。保存済みのランクは保持しています。";}
        catch(HttpRequestException){StatusText.Text="OP.GGに接続できません。接続を確認してください。保存済みのランクは保持しています。";}
        catch(CryptographicException){StatusText.Text="ログイン情報を復号できません。登録したWindowsユーザーで起動してください。";}
        catch(Microsoft.Data.Sqlite.SqliteException){StatusText.Text="データを保存できません。保存先の空き容量・アクセス権を確認してください。";}
        catch(System.Text.Json.JsonException){StatusText.Text="保存データを読み取れません。保存先のファイルを確認してください。";}
        catch(Exception ex) when(ex is OpggException or InvalidOperationException or ArgumentException)
        {StatusText.Text=ex is ArgumentException?"操作に失敗しました。入力内容を確認してください。":ex.Message;}
        finally{status.Active=false;operation.Dispose();operation=null;CancelButton.Visibility=Visibility.Collapsed;Reload();if(closeRequested)Close();}
    }
    // Progress<T> posts asynchronously, so a report queued just before a synchronous failure
    // would overwrite the error message. Apply UI-thread reports immediately and drop late ones.
    private sealed class StatusProgress(TextBlock target):IProgress<string>
    {
        public bool Active{get;set;}=true;
        public void Report(string message)
        {
            if(target.Dispatcher.CheckAccess()){if(Active)target.Text=message;}
            else target.Dispatcher.BeginInvoke(()=>{if(Active)target.Text=message;});
        }
    }
    private void Account_Changed(object sender,SelectionChangedEventArgs e)=>Render();
    private void Add_Click(object sender,RoutedEventArgs e)=>Edit(null);
    private void Edit_Click(object sender,RoutedEventArgs e){if(Selected is{} a)Edit(a);}
    private void Edit(RiotAccount? account)
    {
        if(operation!=null)return;
        try
        {
            var dialog=new AccountDialog(store,account){Owner=this};
            if(dialog.ShowDialog()==true){failures.Remove(dialog.SavedId);Reload(dialog.SavedId);}
        }
        catch(Exception ex) when(ex is CryptographicException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {StatusText.Text="登録情報を開けません。保存先と、登録したWindowsユーザーを確認してください。";}
    }
    private void Delete_Click(object sender,RoutedEventArgs e)
    {
        if(operation!=null||Selected is not{} a)return;
        if(MessageBox.Show(this,$"「{a.Label}」のログイン情報と保存済みランクを削除します。","アカウントを削除",MessageBoxButton.OKCancel,MessageBoxImage.Question)==MessageBoxResult.OK)
        {
            try{store.Delete(a.Id);failures.Remove(a.Id);Reload();}
            catch(Microsoft.Data.Sqlite.SqliteException){StatusText.Text="削除できませんでした。保存先のアクセス権を確認してください。";}
        }
    }
    private void CopyUser_Click(object sender,RoutedEventArgs e){if(Selected is{} a)Copy(a,false);}
    private void CopyPassword_Click(object sender,RoutedEventArgs e){if(Selected is{} a)Copy(a,true);}
    private void OpenOpgg_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not{} a)return;
        try{Process.Start(new ProcessStartInfo(OpggRegions.ProfileUrl(a).AbsoluteUri){UseShellExecute=true});StatusText.Text="OP.GGをブラウザで開きました。";}
        catch(ArgumentException ex){StatusText.Text=ex.Message;}
        catch(Win32Exception){StatusText.Text="ブラウザを起動できませんでした。";}
    }
    private void Copy(RiotAccount account,bool password)
    {
        try
        {
            var c=store.Credentials(account.Id);ClipboardLease.Copy(password?c.Password:c.Username,password);
            StatusText.Text=password?"パスワードをコピーしました。30秒後にクリップボードを消去します。":"ログインIDをコピーしました。";
        }
        catch(Exception ex) when(ex is CryptographicException or ExternalException or InvalidOperationException){StatusText.Text="コピーできませんでした。再試行してください。";}
    }
    private async void RefreshAll_Click(object sender,RoutedEventArgs e)=>await RefreshAllAsync();
    // Internal so the offline self-test can await a full run against a fake OP.GG.
    internal Task RefreshAllAsync()=>Run(async(progress,ct)=>
    {
        if(store.Accounts().Count==0){progress.Report("アカウントを追加してください。");return;}
        var result=await new RankRefresh(store,provider).RunAsync(progress,ct);
        foreach(var outcome in result.Outcomes){if(outcome.Ok)failures.Remove(outcome.AccountId);else failures[outcome.AccountId]=outcome.Message;}
        progress.Report(result.Summary+(result.Succeeded<result.Outcomes.Count?"（理由は一覧に表示しています）":""));
    });
    private async void RefreshOne_Click(object sender,RoutedEventArgs e)
    {
        if(Selected is not{} a)return;
        await Run(async(progress,ct)=>
        {
            try{await provider.RefreshRanksAsync(a,ct,progress);}
            catch(OpggException ex){failures[a.Id]=ex.Message;throw;}
            failures.Remove(a.Id);progress.Report($"「{a.Label}」のランクを更新しました。");
        });
    }
    private void Cancel_Click(object sender,RoutedEventArgs e)=>operation?.Cancel();
    private void About_Click(object sender,RoutedEventArgs e)
    {
        var version=typeof(MainWindow).Assembly.GetName().Version?.ToString(3)??"";
        MessageBox.Show(this,
            $"Riot Accounts Lite {version}\n\n"+
            "ログイン情報の管理と、OP.GG（非公式）からのLoLランク表示に絞った簡易版です。Riot APIやその他の外部サーバーには接続しません。\n\n"+
            "OP.GGからの取得は、OP.GGが公開していないWeb用の通信を利用します。予告なく使えなくなることがあります。個人利用の範囲でお使いください。\n\n"+
            $"保存先：{folder}\nログ：{log.FilePath}\n\n"+
            "Riot Games・OP.GGの公式アプリではなく、両社の承認・推奨を受けたものではありません。",
            "このアプリについて",MessageBoxButton.OK,MessageBoxImage.Information);
    }
}
