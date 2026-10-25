using System;
using System.Collections.Generic;
using System.IO;
using PvfCode.NPK.Utils;
using PvfCode.NPK.Utils.Models;

namespace PvfCode.Services.Npk;

/// <summary>
/// NPK 图片索引缓存：图片路径 -> (offset, length)。
///
/// 设计取舍：
/// - 实例持有，不用进程级静态状态（静态可变缓存会让测试互相污染、也无法随窗口释放）；
/// - 只缓存索引，不缓存像素，内存与条目数成正比、与图片大小无关；
/// - 容量按"包个数"控制，超限整体清空，不做 LRU（单机工具，命中集中在少数几个包）。
/// </summary>
internal sealed class NpkIndexCache
{
	private const int DefaultMaxArchiveCount = 6;

	private readonly Dictionary<string, Dictionary<string, long>> archives;
	private readonly Dictionary<string, long> emptyIndex = new Dictionary<string, long>(0);
	private readonly object syncRoot = new object();
	private readonly int maxArchiveCount;

	internal NpkIndexCache(int maxArchiveCount = DefaultMaxArchiveCount)
	{
		archives = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
		this.maxArchiveCount = maxArchiveCount > 0 ? maxArchiveCount : DefaultMaxArchiveCount;
	}

	/// <summary>取图片在 NPK 中的字节区间。缓存未命中时读一次索引表；失败也缓存空表，避免反复打开坏包。</summary>
	internal bool TryGetLocation(string npkPath, string imagePath, out int offset, out int length)
	{
		offset = 0;
		length = 0;
		if (string.IsNullOrEmpty(npkPath) || string.IsNullOrEmpty(imagePath))
		{
			return false;
		}
		Dictionary<string, long> index;
		lock (syncRoot)
		{
			if (!archives.TryGetValue(npkPath, out index) || index == null)
			{
				if (archives.Count >= maxArchiveCount)
				{
					archives.Clear();
				}
				index = Build(npkPath) ?? emptyIndex;
				archives[npkPath] = index;
			}
		}
		if (!index.TryGetValue(imagePath, out long packed))
		{
			return false;
		}
		offset = (int)(uint)(packed >> 32);
		length = (int)(uint)packed;
		return length > 0;
	}

	/// <summary>清空缓存。切换 ImagePacks2 目录后应调用，避免旧包索引残留。</summary>
	internal void Clear()
	{
		lock (syncRoot)
		{
			archives.Clear();
		}
	}

	private static Dictionary<string, long> Build(string npkPath)
	{
		try
		{
			using FileStream stream = new FileStream(npkPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
			List<ImagePack> packs = NpkCoder.ReadInfo(stream);
			if (packs == null || packs.Count == 0)
			{
				return null;
			}
			Dictionary<string, long> map = new Dictionary<string, long>(packs.Count, StringComparer.OrdinalIgnoreCase);
			for (int i = 0; i < packs.Count; i++)
			{
				ImagePack pack = packs[i];
				if (pack?.Path == null || pack.Length <= 0)
				{
					continue;
				}
				// offset 与 length 都是 int32，打包进一个 long，省掉一个元组/对象
				map[pack.Path] = ((long)(uint)pack.Offset << 32) | (uint)pack.Length;
			}
			return map;
		}
		catch
		{
			return null;
		}
	}
}
