using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;

namespace DshShell;

/// <summary>
/// Application entry. A named mutex enforces one shell instance per user: the
/// shell owns the dsh web server process, so a second window would kill the
/// server out from under the first one when it closed.
/// </summary>
public partial class App : Application
{
    private static Mutex? s_singleInstanceMutex;

    /// <summary>The single shell window, or null before launch completes.</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        s_singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\DshShell.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // The first instance owns the server; exit without creating a
            // window, and say why: a silent exit looks like a failed launch
            // when the user starts a rebuilt shell while the old one runs.
            s_singleInstanceMutex.Dispose();
            s_singleInstanceMutex = null;
            MessageBox(IntPtr.Zero, "DeepSeek Harness is already running. Close its window before starting another one.", "DeepSeek Harness", 0x00000040);
            Environment.Exit(0);
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    /// <summary>Report the single-instance refusal before any window exists.</summary>
    /// <param name="hWnd">Owner window; none exists at this point.</param>
    /// <param name="text">Message body.</param>
    /// <param name="caption">Message box title.</param>
    /// <param name="type">Win32 message box flags.</param>
    /// <returns>The message box result.</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
