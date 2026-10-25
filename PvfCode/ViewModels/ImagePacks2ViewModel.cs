using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using DevExpress.Mvvm;
using DevExpress.Mvvm.DataAnnotations;
using Microsoft.WindowsAPICodePack.Dialogs;
using PvfCode.Dot;
using PvfCode.Services.Npk;

namespace PvfCode.ViewModels;

public class ImagePacks2ViewModel : ViewModelBase
{
	public bool IsLoading
	{
		get
		{
			return GetProperty(() => IsLoading);
		}
		set
		{
			SetProperty(() => IsLoading, value);
		}
	}

	[Command]
	public async void OnLoadImagePacks2()
	{
		CommonOpenFileDialog commonOpenFileDialog = new CommonOpenFileDialog
		{
			Title = AppSetting.Instance.GetIlogger().GetStr("mess_SelectDNFClientModelDir"),
			IsFolderPicker = true,
			Multiselect = false,
			AllowPropertyEditing = true,
			EnsurePathExists = true,
			EnsureValidNames = true
		};
		if (commonOpenFileDialog.ShowDialog(Application.Current.MainWindow) == CommonFileDialogResult.Ok)
		{
			AppSetting.Instance.ImagePacks2Options.ImagePacks2Path = commonOpenFileDialog.FileName;
			if (DiskDetectionUtils.DetectDrive(AppSetting.Instance.ImagePacks2Options.ImagePacks2Path).HardwareType != HardwareType.Ssd)
			{
				AppCore.Logger.Warning(AppCore.Logger.GetStrNoReplace("Mess_NotSSD"));
			}
			await Task.Run((Func<Task?>)LoadImagePacks2);
			await AppSetting.Instance.SaveSetting();
		}
	}

	public async Task LoadImagePacks2()
	{
		AppCore.Logger.Warning(AppSetting.Instance.GetIlogger()?.GetStr("mess_ImagePack2ModelPatchLoading"));
		if (string.IsNullOrEmpty(AppSetting.Instance.ImagePacks2Options.ImagePacks2Path))
		{
			AppCore.Logger.Error(AppSetting.Instance.GetIlogger()?.GetStr("mess_PleaseSelectImagePacks2ModelDir"));
			return;
		}
		if (!Directory.Exists(AppSetting.Instance.ImagePacks2Options.ImagePacks2Path))
		{
			AppCore.Logger.Error(string.Format(AppSetting.Instance.GetIlogger()?.GetStr("mess_DirNotExist"), AppSetting.Instance.ImagePacks2Options.ImagePacks2Path));
			return;
		}
		IsLoading = true;
		ResultData resultData = await ImagePack2Service.Instance.LoadImagePack2Async(AppSetting.Instance.ImagePacks2Options.ImagePacks2Path);
		if (resultData.IsError)
		{
			AppCore.Logger.Error(resultData.Msg);
		}
		if (AppCore.ViewModelBase.PVF != null && AppCore.ViewModelBase.PVF.PvfIsOpen && ImagePack2Service.Instance.Count > 0)
		{
			ImagePack2Service.Instance.LoadTreeIcons(AppCore.ViewModelBase.PVF);
			RunMissingResourceCheck();
		}
		IsLoading = false;
	}

	/// <summary>
	/// 比对 PVF 引用与 ImagePacks2 内容，输出缺失清单。
	/// 由「PVF 打开完成」和「ImagePacks2 加载完成」两处调用，内部按 PVF 路径去重，
	/// 避免同一组合被重复扫描。
	/// </summary>
	public void RunMissingResourceCheck()
	{
		PvfGroup pvf = AppCore.ViewModelBase.PVF;
		if (pvf == null || !pvf.PvfIsOpen || ImagePack2Service.Instance.Count == 0)
		{
			return;
		}
		string pvfPath = pvf.PvfPackFilePath;
		if (string.Equals(lastMissingCheckedPvfPath, pvfPath, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		try
		{
			AppSetting.Instance.GetIlogger()?.Warning("[资源检查] 正在比对 PVF 引用与 ImagePacks2 内容...");
			Stopwatch stopwatch = Stopwatch.StartNew();
			MissingResourceReport report = ImagePack2Service.Instance.CheckMissingResources(pvf);
			stopwatch.Stop();
			lastMissingCheckedPvfPath = pvfPath;
			if (report.Missing.Count == 0)
			{
				AppSetting.Instance.GetIlogger()?.Success(
					$"[资源检查] PVF 引用 {report.ReferenceCount} 条，未发现缺失（{stopwatch.ElapsedMilliseconds} ms）");
				return;
			}
			string reportPath = ImagePack2Service.Instance.WriteMissingReport(report, pvfPath);
			AppSetting.Instance.GetIlogger()?.Warning(
				$"[资源检查] PVF 引用 {report.ReferenceCount} 条，缺失 {report.Missing.Count} 条（{stopwatch.ElapsedMilliseconds} ms）。报告：{reportPath}");
		}
		catch (Exception ex)
		{
			AppSetting.Instance.GetIlogger()?.Error("[资源检查] 缺失检查失败：" + ex.Message);
		}
	}

	/// <summary>上次做过缺失检查的 PVF 路径，用于避免重复扫描。</summary>
	private string lastMissingCheckedPvfPath;

	[Command]
	public async void OnRefresh()
	{
		await Task.Run((Func<Task?>)LoadImagePacks2);
		await AppSetting.Instance.SaveSetting();
	}

	[Command]
	public void OnClear()
	{
		AppSetting.Instance.ImagePacks2Options.ImagePacks2Path = "";
		ImagePack2Service.Instance.Clear();
	}

	public ImagePacks2ViewModel()
	{
	}
}
