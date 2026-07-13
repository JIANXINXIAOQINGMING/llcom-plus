using CrashReporterDotNET;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace llcom_plus
{
    /// <summary>
    /// App.xaml 的交互逻辑
    /// </summary>
    public partial class App : Application
    {
        private static readonly object CrashReportLock = new object();
        private static string lastCrashReportSignature = "";
        private static DateTime lastCrashReportTime = DateTime.MinValue;

        static App()
        {
            Tools.StartupProfiler.Begin();
            Tools.StartupProfiler.Mark("App static ctor");
        }

        public App()
        {
            Tools.StartupProfiler.Mark("App ctor");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            Tools.StartupProfiler.Mark("App.OnStartup enter");
#if DEBUG
#else
            AppDomain.CurrentDomain.UnhandledException += CurrentDomainOnUnhandledException;
            Application.Current.DispatcherUnhandledException += DispatcherOnUnhandledException;
#endif
            base.OnStartup(e);
            Tools.StartupProfiler.Mark("App.OnStartup base completed");
            Tools.StartupProfiler.Mark("App.OnStartup exit");
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            var darkMode = Tools.Global.setting?.darkMode ?? Tools.Global.IsDarkTheme;
            foreach (Window window in Windows)
                Tools.Win32.ApplyWindowTheme(window, darkMode, !(window is MainWindow));
        }

        private void DispatcherOnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs dispatcherUnhandledExceptionEventArgs)
        {
            SendReport(dispatcherUnhandledExceptionEventArgs.Exception);
        }

        private static void CurrentDomainOnUnhandledException(object sender, UnhandledExceptionEventArgs unhandledExceptionEventArgs)
        {
            SendReport((Exception)unhandledExceptionEventArgs.ExceptionObject);
        }

        public static void SendReport(Exception exception, string developerMessage = "", bool silent = true)
        {
            if (exception == null || IsDuplicateCrashReport(exception))
                return;

            if(exception.GetType() == typeof(System.ComponentModel.Win32Exception))
            {
                Tools.MessageBox.Show($"internal error from system!\r\n{exception.Message}\r\nexit!");
                return;
            }
            if(Tools.Global.setting?.language == "zh-CN")
                Tools.MessageBox.Show("恭喜你触发了一个BUG！\r\n" +
                    "如果条件允许，请点击“Send Report”来上报这个BUG\r\n" +
                    $"报错信息：{exception.Message}");
            if(!Tools.Global.ReportBug)
            {
                Tools.MessageBox.Show("检测到不支持的.net版本，禁止上报bug");
                return;
            }
            if(Tools.Global.HasNewVersion)
            {
                Tools.MessageBox.Show("检测到该软件不是最新版，禁止上报bug\r\n请保证软件是最新版");
                return;
            }
            var reportCrash = new ReportCrash("lxlpsp@live.com")
            {
                DeveloperMessage = developerMessage
            };
            //reportCrash.Silent = silent;
            reportCrash.CaptureScreen = true;
            try
            {
                reportCrash.Send(exception);
            }
            catch (Exception reportException)
            {
                Tools.MessageBox.Show("BUG上报窗口打开失败：\r\n" + reportException.Message);
            }
        }

        private static bool IsDuplicateCrashReport(Exception exception)
        {
            var signature = exception.GetType().FullName + "|" + exception.Message + "|" + exception.StackTrace;
            lock (CrashReportLock)
            {
                var now = DateTime.UtcNow;
                if (signature == lastCrashReportSignature &&
                    (now - lastCrashReportTime).TotalSeconds < 3)
                    return true;

                lastCrashReportSignature = signature;
                lastCrashReportTime = now;
                return false;
            }
        }
    }
}
