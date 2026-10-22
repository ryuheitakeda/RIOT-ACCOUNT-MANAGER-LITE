using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace RiotAccountsLite.App;

internal static class ClipboardLease
{
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(30) };
    private static uint? ownedSequence;

    static ClipboardLease() => Timer.Tick += (_, _) => ClearOwned();

    internal static void Copy(string text, bool sensitive)
    {
        System.Windows.Clipboard.SetText(text);
        Timer.Stop();
        ownedSequence = sensitive ? Win.GetClipboardSequenceNumber() : null;
        Timer.Interval = TimeSpan.FromSeconds(30);
        if (sensitive) Timer.Start();
    }

    internal static bool ClearIfOwned(uint sequence)
    {
        if (Win.GetClipboardSequenceNumber() != sequence) return false;
        System.Windows.Clipboard.Clear();
        return true;
    }

    internal static void ClearOwned()
    {
        Timer.Stop();
        try
        {
            if (ownedSequence is { } sequence) ClearIfOwned(sequence);
            ownedSequence = null;
        }
        catch (ExternalException)
        {
            // Another process may briefly hold the clipboard open. Retry while we still own it.
            Timer.Interval = TimeSpan.FromSeconds(1);
            Timer.Start();
        }
    }
}

internal static class Win
{
    // Changes whenever anything is copied, so a later copy by the user is never cleared.
    [DllImport("user32.dll")]internal static extern uint GetClipboardSequenceNumber();
}
