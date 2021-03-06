﻿using CairoDesktop.Application.Interfaces;
using CairoDesktop.Common;
using CairoDesktop.Configuration;
using CairoDesktop.SupportingClasses;
using ManagedShell.Common.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CairoDesktop.Infrastructure.Services;
using ManagedShell.Common.SupportingClasses; // Required for StartupRunner; excluded from debug builds

namespace CairoDesktop
{
    public partial class CairoApplication : System.Windows.Application, ICairoApplication
    {
        private readonly ILogger<CairoApplication> _logger;
        public new static CairoApplication Current => System.Windows.Application.Current as CairoApplication;

        private CommandLineParser _commandLineParser;
        private bool _isRestart;
        private bool _isTour;
        private bool _forceEnableShellMode;
        private bool _forceDisableShellMode;

        // Parameter-less constructor required for WPF
        public CairoApplication()
        {
        }

        public CairoApplication(IHost host, ILogger<CairoApplication> logger)
        {
            Host = host;
            _logger = logger;

            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            ProcessCommandLineArgs(Environment.GetCommandLineArgs());

            Extensions = new List<IShellExtension>();

            Commands = new List<ICommand>();
            CairoMenu = new List<IMenuItem>();
            Places = new List<IMenuItem>();
            MenuBarExtensions = new List<IMenuBarExtension>();

            InitializeComponent();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Initialize current shell information here, since it won't be accurate if we wait until after we create our own windows
            SetIsCairoRunningAsShell();

            Host.Start();

            WriteApplicationDebugInfoToConsole();

            LoadExtensions();

            SetTheme();

            if (Settings.Instance.ForceSoftwareRendering)
            {
                RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            }

            base.OnStartup(e);

            SetupWindowServices();

            ShellHelper.SetShellReadyEvent();

#if ENABLEFIRSTRUN
            FirstRun();
#endif

#if !DEBUG
            // login items only necessary if Explorer didn't start them
            if (EnvironmentHelper.IsAppRunningAsShell && !_isRestart)
            {
                StartupRunner runner = new StartupRunner();
                runner.Run();
            }
#endif
        }

        private void FirstRun()
        {
            try
            {
                if (Settings.Instance.IsFirstRun || _isTour)
                {
                    Welcome welcome = new Welcome();
                    welcome.Show();
                }
            }
            catch (Exception ex)
            {
                CairoMessage.Show(
                    $"Whoops! Something bad happened in the startup process.\nCairo will probably run, but please report the following details (preferably as a screen shot...)\n\n{ex}",
                    "Unexpected error!",
                    CairoMessageImage.Error);
            }
        }

        private void SetTheme()
        {
            // Themes are very UI centric. We should devise a way of having Plugins/Extensions contribute to this.
            string theme = Settings.Instance.CairoTheme;
            if (theme != "Default")
            {
                string themeFilePath = AppDomain.CurrentDomain.BaseDirectory + theme;
                if (File.Exists(themeFilePath))
                {
                    var newRes = new ResourceDictionary
                    {
                        Source = new Uri(themeFilePath, UriKind.RelativeOrAbsolute)
                    };
                    Resources.MergedDictionaries.Add(newRes);
                }
            }

            Settings.Instance.PropertyChanged += (s, e) =>
            {
                if (e == null || string.IsNullOrWhiteSpace(e.PropertyName) || e.PropertyName != "CairoTheme")
                {
                    return;
                }

                Resources.MergedDictionaries.Clear();
                var cairoResource = new ResourceDictionary
                {
                    Source = new Uri("Cairo.xaml", UriKind.RelativeOrAbsolute)
                };
                Resources.MergedDictionaries.Add(cairoResource);

                string newTheme = Settings.Instance.CairoTheme;
                if (newTheme == "Default")
                {
                    return;
                }

                string newThemeFilePath = AppDomain.CurrentDomain.BaseDirectory + newTheme;
                if (!File.Exists(newThemeFilePath))
                {
                    return;
                }

                var newRes = new ResourceDictionary
                {
                    Source = new Uri(newThemeFilePath, UriKind.RelativeOrAbsolute)
                };
                Resources.MergedDictionaries.Add(newRes);
            };
        }

        protected override async void OnSessionEnding(SessionEndingCancelEventArgs e)
        {
            base.OnSessionEnding(e);

            await GracefullyExit();
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            if (EnvironmentHelper.IsAppRunningAsShell)
            {
                // WinLogon will automatically launch the local machine shell if AutoRestartShell is enabled and the shell window process exits with code 0.
                // Setting the exit code to 1 indicates that we are shutting down intentionally and do not want the local machine shell to restart
                e.ApplicationExitCode = 1;
            }

            base.OnExit(e);

            await GracefullyExit();
        }

        private async Task GracefullyExit()
        {
            if (Host != null)
            {
                await Host.StopAsync();
                Host.Dispose();
            }
        }

        private bool _errorVisible;

        private void Application_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            FileVersionInfo fvi = FileVersionInfo.GetVersionInfo(assembly.Location);
            string version = fvi.FileVersion;

            string inner = "";
            if (e.Exception.InnerException != null)
                inner = "\r\n\r\nInner exception:\r\nMessage: " + e.Exception.InnerException.Message + "\r\nTarget Site: " + e.Exception.InnerException.TargetSite + "\r\n\r\n" + e.Exception.InnerException.StackTrace;

            string msg = "Would you like to restart Cairo?\r\n\r\nPlease submit a bug report with a screenshot of this error. Thanks! \r\nMessage: " + e.Exception.Message + "\r\nTarget Site: " + e.Exception.TargetSite + "\r\nVersion: " + version + "\r\n\r\n" + e.Exception.StackTrace + inner;

            _logger.LogError(msg, e.Exception);

            string dMsg;

            if (msg.Length > 1000)
                dMsg = msg.Substring(0, 999) + "...";
            else
                dMsg = msg;

            try
            {
                if (!_errorVisible)
                {
                    _errorVisible = true;

                    // Automatically restart for known render thread failure messages.
                    if (e.Exception.Message.StartsWith("UCEERR_RENDERTHREADFAILURE"))
                    {
                        RestartCairo();
                        Environment.FailFast("Automatically restarted Cairo due to a render thread failure.");
                    }
                    else
                    {
                        if (MessageBox.Show(dMsg, "Cairo Desktop Error", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                        {
                            // it's like getting a morning coffee.
                            RestartCairo();
                            Environment.FailFast("User restarted Cairo due to an exception.");
                        }
                    }

                    _errorVisible = false;
                }
            }
            catch
            {
                // If this fails we're probably up the creek. Abandon ship!
                ExitCairo();
            }

            e.Handled = true;
        }


        /// <summary>
        /// Compatibility System.Windows.Forms.Application.DoEvents
        /// </summary>
        public static void DoEvents()
        {
            DispatcherFrame frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                new DispatcherOperationCallback(ExitFrame), frame);
            Dispatcher.PushFrame(frame);
        }

        private static object ExitFrame(object f)
        {
            ((DispatcherFrame)f).Continue = false;
            return null;
        }

        public void RestartCairo()
        {
            try
            {
                // run the program again
                Process current = new Process();
                current.StartInfo.FileName = AppDomain.CurrentDomain.BaseDirectory + "CairoDesktop.exe";
                current.StartInfo.Arguments = "/restart";
                current.Start();

                // close this instance
                ExitCairo();
            }
            catch
            { }
        }

        public void ExitCairo()
        {
            IsShuttingDown = true;
            Host.Services.GetService<ShellManagerService>()?.ShellManager.AppBarManager.SignalGracefulShutdown();

            Dispatcher.Invoke(Shutdown, DispatcherPriority.Normal);
        }

        public void Dispatch(Action action)
        {
            Dispatcher.BeginInvoke(action);
        }

        public bool IsShuttingDown { get; private set; }

        public static string StartupPath => Path.GetDirectoryName((Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).Location);

        public static string ProductName => (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Name;

        public static Version ProductVersion => (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly()).GetName().Version;

        public static string CairoApplicationDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cairo_Development_Team");

        public static string LogsFolder => Path.Combine(CairoApplicationDataFolder, "Logs");

        public IHost Host { get; }

        public List<IShellExtension> Extensions { get; }

        public List<ICommand> Commands { get; }

        public List<IMenuItem> CairoMenu { get; }

        public List<IMenuItem> Places { get; }

        public List<IMenuBarExtension> MenuBarExtensions { get; }
    }
}