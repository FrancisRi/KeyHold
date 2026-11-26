using System;
using System.Globalization;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Forms = System.Windows.Forms;

namespace KeyHoldMacro;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        L.Initialize(L.PreferencePath, CultureInfo.CurrentUICulture.Name);
        using var mutex = new Mutex(true, @"Local\KeyHoldMacro-76D6DF29", out bool created);
        if (!created)
        {
            Forms.MessageBox.Show(L.T("程序已在运行，请双击托盘图标打开。"), "KeyHold");
            return;
        }

        try
        {
            Forms.Application.EnableVisualStyles();
            Forms.Application.SetCompatibleTextRenderingDefault(false);
            Forms.Application.SetUnhandledExceptionMode(Forms.UnhandledExceptionMode.CatchException);
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(initialization =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            Forms.MessageBox.Show(ex.ToString(), L.T("KeyHold 启动失败"));
        }
    }
}

public partial class App : Application
{
    private MainWindow window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            ReportUiException(e.Exception);
            e.Handled = true;
        };
        Forms.Application.ThreadException += (_, e) => ReportUiException(e.Exception);
    }

    private void ReportUiException(Exception exception)
    {
        window?.EmergencyStop(false);
        Forms.MessageBox.Show(L.T("已请求停止宏。\r\n") + exception.Message, L.T("程序错误"));
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            window = new MainWindow();
            window.Activate();
        }
        catch (Exception ex)
        {
            window?.Dispose();
            Forms.MessageBox.Show(ex.ToString(), L.T("KeyHold 启动失败"));
            Exit();
        }
    }
}
