using System;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using DevExpress.Xpf.Core;
using Microsoft.Web.WebView2.Core;
using Nito.AsyncEx;
using PvfCode.Compatibility;
using PvfCode.Localization;
using PvfCode.LoggerBase;
using PvfCode.Models.Images;
using PvfCode.Services;
using PvfCode.Services.TimeServices;
using PvfCode.SplashScreen;
using PvfCode.ViewModels;
using PvfCode.Views;
using ServiceLocator;
using SevenZip;
using ThemedDemo;

namespace PvfCode;

public class App : Application
{
	private static bool IsRecoveredXamlSelfTest =>
		string.Equals(
			Environment.GetEnvironmentVariable("PVFUTILITY_RECOVERED_XAML_SELF_TEST"),
			"1",
			StringComparison.Ordinal);

	private static readonly AsyncLock UnhandledExceptionLock = new AsyncLock();

	private static readonly AsyncLock AppErrorLock = new AsyncLock();

	private CancellationToken backgroundServiceCancellationToken;

	private CleanMemoryTimeService cleanMemoryService;

	private PvfAutoBackupService autoBackupService;

	private GameProcessTimeService gameProcessTimeService;

	private bool backgroundServicesStarted;

	private bool contentLoaded;

	static App()
	{
		RecoveredAssemblyResolver.Register();
		DevExpressNet10Compatibility.Apply();
		if (IsRecoveredXamlSelfTest)
		{
			Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
			ServiceInjector.InjectServices();
			RegisterRecoveredServices();
			return;
		}

		try
		{
			string path = Path.Combine(AppContext.BaseDirectory, "7z64.dll");
			if (!File.Exists(path))
			{
				File.WriteAllBytes(path, Resource1._7z64);
			}
			path = Path.Combine(AppContext.BaseDirectory, "e_sqlite3.dll");
			if (File.Exists(path))
			{
				File.Delete(path);
			}
			path = Path.Combine(AppContext.BaseDirectory, "WebView2Loader.dll");
			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}
		catch (Exception)
		{
		}
		SplashScreenManager.Create(() => new PvfCodeSplashScreenWindow()).ShowOnStartup();
		ServiceInjector.InjectServices();
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		RegisterRecoveredServices();
	}

	private static void RegisterRecoveredServices()
	{
		AppCore.Logger = new LoggerViewModel();
		ServiceContainer.Instance.AddService((Ilogger)AppCore.Logger);
		ServiceContainer.Instance.AddService((IRes)Res.Instance);
	}

	private async void OnStartup(object sender, StartupEventArgs eventArgs)
	{
		RegisterGlobalExceptionHandlers();
		try
		{
			ThemeSwitcher.Instance.SwitchTheme(AppSetting.Instance.NowThemeType);
			await LanguageResourceManager.ApplyLanguageAsync(AppSetting.Instance.CurrentLang);
			MainWindow mainWindow = new MainWindow();
			Application.Current.MainWindow = mainWindow;
			base.MainWindow = mainWindow;
			mainWindow.Show();
			EnsureWebView2Runtime();
			StartBackgroundServices();
		}
		catch (Exception ex)
		{
			ShowErrorMessage(ex.ToString());
		}
	}

	public static void OnExit()
	{
		Environment.Exit(0);
	}

	private static async void ShowErrorMessage(string message)
	{
		try
		{
			if (message.Contains("OpenClipboard 失败 (0x800401D0 (CLIPBRD_E_CANT_OPEN))"))
			{
				return;
			}
			await ((DispatcherObject)Application.Current).Dispatcher.BeginInvoke((Delegate)(Action)(() =>
			{
				WinErrorPanel winErrorPanel = new WinErrorPanel(message);
				winErrorPanel.WindowStartupLocation = WindowStartupLocation.CenterScreen;
				winErrorPanel.Topmost = true;
				winErrorPanel.ShowDialog();
			}), Array.Empty<object>());
		}
		catch (Exception)
		{
		}
	}

	private void RegisterGlobalExceptionHandlers()
	{
		TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
		base.DispatcherUnhandledException += OnDispatcherUnhandledException;
		AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
	}

	private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs eventArgs)
	{
		try
		{
			Exception exception = eventArgs.Exception;
			if (exception != null)
			{
				ReportUnhandledException(exception);
			}
		}
		catch (Exception ex)
		{
			ReportUnhandledException(ex);
		}
		finally
		{
			eventArgs.SetObserved();
		}
	}

	private static void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs eventArgs)
	{
		try
		{
			if (eventArgs.ExceptionObject is Exception ex)
			{
				ReportUnhandledException(ex);
			}
		}
		catch (Exception ex2)
		{
			ReportUnhandledException(ex2);
		}
	}

	private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs eventArgs)
	{
		try
		{
			ReportUnhandledException(eventArgs.Exception);
		}
		catch (Exception ex)
		{
			ReportUnhandledException(ex);
		}
		finally
		{
			eventArgs.Handled = true;
		}
	}

	private static async void ReportUnhandledException(Exception exception)
	{
		using (await UnhandledExceptionLock.LockAsync())
		{
			LogUnhandledException(exception);
			ShowErrorMessage(await BuildExceptionMessageAsync(exception));
		}
	}

	/// <summary>
	/// 未处理异常落盘。原来只弹窗不写文件，用户反馈问题时拿不到堆栈。
	/// 日志失败不能影响提示流程，所以整体吞掉异常。
	/// </summary>
	private static void LogUnhandledException(Exception exception)
	{
		try
		{
			string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pvfUtility", "logs");
			Directory.CreateDirectory(directory);
			string path = Path.Combine(directory, "unhandled-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
			StringBuilder builder = new StringBuilder();
			builder.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(' ');
			builder.Append(exception.ToStringDemystified());
			builder.AppendLine();
			builder.AppendLine(new string('-', 60));
			File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
		}
		catch
		{
		}
	}

	/// <summary>
	/// WebView2 运行时是唯一不能随包分发的外部依赖（Win11 自带，Win10 可能被精简掉）。
	/// 缺失时给出明确提示，避免编辑器页签白屏且无任何线索。只提示一次。
	/// </summary>
	private static void EnsureWebView2Runtime()
	{
		try
		{
			string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
			if (!string.IsNullOrEmpty(version))
			{
				return;
			}
		}
		catch (Exception)
		{
		}
		try
		{
			string marker = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "pvfUtility", "webview2-warned");
			if (File.Exists(marker))
			{
				return;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(marker));
			File.WriteAllText(marker, DateTime.Now.ToString("o"));
			MessageBox.Show(
				"未检测到 Microsoft Edge WebView2 运行时。\n\n代码编辑器、文件对比等页签依赖该组件，缺失时会显示空白；其余功能不受影响。\n\n请安装微软官方 “WebView2 Runtime (Evergreen)” 后重新打开程序。",
				"缺少运行组件",
				MessageBoxButton.OK,
				MessageBoxImage.Warning);
		}
		catch (Exception)
		{
		}
	}

	public static async void ShowAppError(Exception e, string caption)
	{
		using (await AppErrorLock.LockAsync())
		{
			ShowErrorMessage(await BuildExceptionMessageAsync(e, caption));
		}
	}

	private static async Task<string> BuildExceptionMessageAsync(Exception exception, string? caption = null)
	{
		string message = ((caption == null) ? string.Format(AppSetting.Instance.GetIlogger()?.GetStr("mess_Exception_2"), "QQ：812143836", exception.ToStringDemystified()) : string.Format(AppSetting.Instance.GetIlogger()?.GetStr("mess_Exception"), "QQ：812143836", caption, exception.ToStringDemystified()));
		return message;
	}

	private async void StartBackgroundServices()
	{
		if (!backgroundServicesStarted)
		{
			backgroundServicesStarted = true;
			backgroundServiceCancellationToken = default(CancellationToken);
			cleanMemoryService = new CleanMemoryTimeService();
			await cleanMemoryService.StartAsync(backgroundServiceCancellationToken);
			autoBackupService = new PvfAutoBackupService();
			await autoBackupService.StartAsync(backgroundServiceCancellationToken);
			gameProcessTimeService = new GameProcessTimeService();
			await gameProcessTimeService.StartAsync(backgroundServiceCancellationToken);
		}
	}

	[DebuggerNonUserCode]
	[GeneratedCode("PresentationBuildTasks", "10.0.1.0")]
	public void InitializeComponent()
	{
		if (!contentLoaded)
		{
			contentLoaded = true;
			base.Startup += OnStartup;
			Uri resourceLocator = new Uri("/pvfUtility;V2026.1.22.2;component/app.xaml", UriKind.Relative);
			System.Windows.Application.LoadComponent(this, resourceLocator);
		}
	}

	[GeneratedCode("PresentationBuildTasks", "10.0.1.0")]
	[DebuggerNonUserCode]
	[STAThread]
	public static int Main()
	{
		App app = new App();
		app.InitializeComponent();
		if (IsRecoveredXamlSelfTest)
		{
			return RecoveredXamlSelfTest.Run(app);
		}
		app.Run();
		return 0;
	}
}
