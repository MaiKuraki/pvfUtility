using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using PvfCode.Dot;

namespace PvfCode.Services;

internal class PvfAutoBackupService : BackgroundService
{
	/// <summary>
	/// 上次成功备份时的内容指纹。到点先比指纹，内容没变就完全不碰磁盘。
	/// 备份是整包重写（200MB 的 PVF 就是 200MB 写入），这一步把挂机时的无谓写入降到 0。
	/// </summary>
	private (int Count, ulong Hash)? lastBackupFingerprint;

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			int minutes = AppSetting.Instance.PvfConfig.AutoTheBackupPvfConfig.SleepTimeMinutes;
			if (minutes <= 0)
			{
				minutes = 10;
			}
			try
			{
				await Task.Delay(minutes * 60000, stoppingToken);
			}
			catch (OperationCanceledException)
			{
				return;
			}
			if (!AppSetting.Instance.PvfConfig.AutoTheBackupPvfConfig.AutoTheBackupPvfIsOpen)
			{
				lastBackupFingerprint = null;
				continue;
			}

			try
			{
				PvfGroup pvf = AppCore.ViewModelBase.PVF;
				if (pvf == null || !pvf.PvfIsOpen)
				{
					lastBackupFingerprint = null;
					continue;
				}

				// 内容没变化就不备份：代价只是遍历一次文件表（20 万文件约几毫秒），
				// 换掉的是一次完整整包写入。
				(int Count, ulong Hash) fingerprint = pvf.ComputeContentFingerprint();
				if (lastBackupFingerprint.HasValue && lastBackupFingerprint.Value == fingerprint)
				{
					continue;
				}

				string backupPath = AppSetting.Instance.PvfConfig.AutoTheBackupPvfConfig.CreateFilePath(pvf);
				ResultData result = await Task.Run(() => pvf.SavePvfPack(backupPath, isFastMode: true, null));
				if (result.IsError)
				{
					AppCore.Logger.Error(
						AppSetting.Instance.GetIlogger()?.GetStr("AutoTheBackupPvfTimeService_BackupPvfError") + result.Msg);
					continue;
				}
				// 只有写成功才记住指纹，失败下一轮还会重试
				lastBackupFingerprint = fingerprint;

				if (AppSetting.Instance.PvfConfig.AutoTheBackupPvfConfig.BackupSuccessLog)
				{
					AppCore.Logger.Success(
						AppSetting.Instance.GetIlogger()?.GetStr("AutoTheBackupPvfTimeService_BackupSuccess") + backupPath);
				}

				string backupDirectory = AppSetting.Instance.PvfConfig.AutoTheBackupPvfConfig.GetBackUpPath(pvf);
				string[] backupFiles = Directory.GetFiles(backupDirectory, "*.pvf");
				if (backupFiles == null || backupFiles.Length <= 1)
				{
					continue;
				}

				// 保留最近 N 份：先按创建时间排序，再用 HashSet 判断，避免原来 O(n²) 的 Any 扫描
				int keepCount = AppSetting.Instance.PvfConfig.AutoTheBackupPvfConfig.BackupPvfMaxCount;
				FileInfo[] fileInfos = new FileInfo[backupFiles.Length];
				for (int i = 0; i < backupFiles.Length; i++)
				{
					fileInfos[i] = new FileInfo(backupFiles[i]);
				}
				Array.Sort(fileInfos, delegate(FileInfo left, FileInfo right)
				{
					return right.CreationTime.CompareTo(left.CreationTime);
				});
				HashSet<string> keepPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				for (int i = 0; i < fileInfos.Length && i < keepCount; i++)
				{
					keepPaths.Add(fileInfos[i].FullName);
				}
				for (int i = 0; i < backupFiles.Length; i++)
				{
					if (keepPaths.Contains(backupFiles[i]))
					{
						continue;
					}
					try
					{
						File.Delete(backupFiles[i]);
					}
					catch (Exception)
					{
					}
				}
			}
			catch (Exception e)
			{
				Console.WriteLine(
					AppSetting.Instance.GetIlogger()?.GetStr("AutoTheBackupPvfTimeService_BackupPvfError") + e.Message);
			}
		}
	}
}
