using System.Security.Cryptography;
using System.Text;

namespace RiotAccountsLite.App;
public sealed class WindowsProtector:ISecretProtector
{
    // Differs from the full Riot Accounts app, so neither app can decrypt the other's secrets.
    private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("RiotAccountsLite/v1");
    public byte[] Protect(byte[] data)=>ProtectedData.Protect(data,Entropy,DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] data)=>ProtectedData.Unprotect(data,Entropy,DataProtectionScope.CurrentUser);
}
public partial class App:System.Windows.Application
{
    // Separate from the full app's %LOCALAPPDATA%\RiotAccounts, which this app never reads or writes.
    internal static string DataFolder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RiotAccountsLite");
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if(e.Args.Contains("--self-test"))
            {
                ShutdownMode=ShutdownMode.OnExplicitShutdown;
                var output=e.Args.SkipWhile(a=>a!="--self-test").Skip(1).FirstOrDefault();
                var report=SmokeTests.Run(output==null?null:Path.GetDirectoryName(Path.GetFullPath(output)));
                if(output!=null)File.WriteAllText(output,report);
                Shutdown(report.Contains("FAIL",StringComparison.Ordinal)?1:0);return;
            }
            var folder=DataFolder;
            var store=new Store(Path.Combine(folder,"accounts.db"),new WindowsProtector());
            var window=new MainWindow(store,folder);MainWindow=window;window.Show();
        }
        catch(Exception)
        {
            MessageBox.Show("アプリを起動できません。保存先へのアクセス権とWindowsユーザーを確認してください。","Riot Accounts Lite",MessageBoxButton.OK,MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
