using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.Win32.SafeHandles;
using PvfCode.Dot;
using PvfCode.NPK.Utils.Lib;
using PvfCode.NPK.Utils.Models;
using PvfCode.NPK.Utils.Models.Enums;
using Utools;

namespace PvfCode.NPK.Utils;

public static class NpkCoder
{
	public class UtImgFileTempWrapper
	{
		public UtImgFileTemp Value { get; }

		public UtImgFileTempWrapper(UtImgFileTemp value)
		{
			Value = value;
		}
	}

	public const string NPK_FlAG = "NeoplePack_Bill";

	public const string IMG_FLAG = "Neople Img File";

	public const string IMAGE_FLAG = "Neople Image File";

	public const string IMAGE_DIR = "ImagePacks2";

	public const string SOUND_DIR = "SoundPacks";

	private const string KEY_HEADER = "puchikon@neople dungeon and fighter ";

	public static Encoding Encoding = System.Text.Encoding.UTF8;

	private static byte[] key;

	private static int _fileNameIdCounter = 1;

	private static byte[] Key
	{
		get
		{
			if (key != null)
			{
				return key;
			}
			byte[] array = new byte[256];
			int bytes = Encoding.GetBytes("puchikon@neople dungeon and fighter ", 0, "puchikon@neople dungeon and fighter ".Length, array, 0);
			byte[] bytes2 = Encoding.GetBytes("DNF");
			for (int i = bytes; i < 255; i++)
			{
				array[i] = bytes2[i % 3];
			}
			array[255] = 0;
			return key = array;
		}
	}

	public static string ReadPath(this Stream stream)
	{
		// 原来逐字节 ReadByte 256 次；改成一次读满 256 字节再在内存里异或。
		// 消费字节数与旧实现一致（旧实现靠 Seek(255-i) 补齐到 256）。
		byte[] keyBytes = Key;
		Span<byte> buffer = stackalloc byte[256];
		int read = stream.ReadAtLeast(buffer, 256, throwOnEndOfStream: false);
		if (read <= 0)
		{
			return string.Empty;
		}
		int length = 0;
		while (length < read)
		{
			byte value = (byte)(buffer[length] ^ keyBytes[length]);
			if (value == 0)
			{
				break;
			}
			buffer[length] = value;
			length++;
		}
		return GbkEncoding.GetString(buffer.Slice(0, length));
	}

	public static async Task<string> ReadPathNewAsync(this Stream stream)
	{
		byte[] data = new byte[256];
		int i;
		for (i = 0; i < 256; i++)
		{
			int num = await stream.ReadByteAsync();
			if (num == -1)
			{
				break;
			}
			data[i] = (byte)(num ^ Key[i]);
			if (data[i] == 0)
			{
				break;
			}
		}
		stream.Seek(255 - i, SeekOrigin.Current);
		return System.Text.Encoding.GetEncoding("gbk").GetString(data, 0, i);
	}

	private static void WritePath(this Stream stream, string str)
	{
		byte[] array = new byte[256];
		Encoding.GetBytes(str, 0, str.Length, array, 0);
		for (int i = 0; i < array.Length; i++)
		{
			array[i] ^= Key[i];
		}
		stream.Write(array);
	}

	public static Bitmap ReadImage(Stream stream, ImgFile entity)
	{
		byte[] array = new byte[entity.Width * entity.Height * 4];
		for (int i = 0; i < array.Length; i += 4)
		{
			ColorBits colorBits = entity.Type;
			if (entity.Version == ImgVersion.Ver4 && colorBits == ColorBits.ARGB_1555)
			{
				colorBits = ColorBits.ARGB_8888;
			}
			PvfCode.NPK.Utils.Lib.Colors.ReadColor(stream, colorBits, array, i);
		}
		return array.FromArray(entity.Size);
	}

	private static byte[] CompileHash(byte[] data)
	{
		if (data.Length == 0)
		{
			return new byte[0];
		}
		try
		{
			using HMACSHA256 hMACSHA = new HMACSHA256();
			return hMACSHA.ComputeHash(data, 0, data.Length / 17 * 17);
		}
		catch
		{
			throw new FipsException();
		}
	}

	public static List<ImagePack> ReadInfo(Stream stream)
	{
		string text = stream.ReadString();
		List<ImagePack> list = new List<ImagePack>();
		if (text != "NeoplePack_Bill")
		{
			return list;
		}
		int num = stream.ReadInt();
		for (int i = 0; i < num; i++)
		{
			ImagePack item = new ImagePack
			{
				Offset = stream.ReadInt(),
				Length = stream.ReadInt(),
				Path = stream.ReadPath()
			};
			list.Add(item);
		}
		return list;
	}

	public static List<ImagePack> ReadNpk(Stream stream, string file)
	{
		List<ImagePack> list = new List<ImagePack>();
		if (stream.ReadString() == "NeoplePack_Bill")
		{
			stream.Seek(0L, SeekOrigin.Begin);
			list.AddRange(ReadInfo(stream));
			if (list.Count > 0)
			{
				stream.Seek(32L);
			}
		}
		else
		{
			ImagePack imagePack = new ImagePack();
			if (file != null)
			{
				imagePack.Path = file.GetSuffix();
			}
			list.Add(imagePack);
		}
		for (int i = 0; i < list.Count; i++)
		{
			long length = ((i < list.Count - 1) ? list[i + 1].Offset : stream.Length);
			ReadImg(stream, list[i], length);
		}
		return list;
	}

	public static List<ImagePack> ReadNpk(Stream stream)
	{
		return ReadNpk(stream, null);
	}

	public static List<ImagePack> ReadNpk(string filePath)
	{
		using FileStream stream = File.OpenRead(filePath);
		return ReadNpk(stream);
	}

	public static async Task<ResultData<Dictionary<string, UtImgFile>, string, Dictionary<int, string>>> ParallelReadNpkList2(string dirName)
	{
		_fileNameIdCounter = 1;
		StringBuilder buiError = new StringBuilder();
		ConcurrentDictionary<string, UtImgFileTemp> concurrentDictionary = new ConcurrentDictionary<string, UtImgFileTemp>();
		List<string> list = new List<string>(Directory.EnumerateFiles(dirName, "*.npk", SearchOption.TopDirectoryOnly));
		ConcurrentDictionary<int, string> concurrentDictionary2 = new ConcurrentDictionary<int, string>();
		// 各 NPK 之间互不依赖，真正并行；并发度按磁盘类型自适应（见 ResolveNpkReadConcurrency）
		int concurrency = ResolveNpkReadConcurrency(dirName);
		await Task.Run(delegate
		{
			ParallelOptions parallelOptions = new ParallelOptions
			{
				MaxDegreeOfParallelism = concurrency
			};
			Parallel.ForEach(list, parallelOptions, delegate(string item)
			{
				ReadNpkInfo2(item, concurrentDictionary, buiError, concurrentDictionary2);
			});
		});
		return new ResultData<Dictionary<string, UtImgFile>, string, Dictionary<int, string>>
		{
			Data = ((IEnumerable<KeyValuePair<string, UtImgFileTemp>>)concurrentDictionary).ToDictionary((Func<KeyValuePair<string, UtImgFileTemp>, string>)((KeyValuePair<string, UtImgFileTemp> it) => it.Key), (Func<KeyValuePair<string, UtImgFileTemp>, UtImgFile>)((KeyValuePair<string, UtImgFileTemp> it) => it.Value)),
			Data2 = buiError.ToString(),
			Data3 = concurrentDictionary2.ToDictionary((KeyValuePair<int, string> it) => it.Key, (KeyValuePair<int, string> it) => it.Value)
		};
	}

	/// <summary>
	/// 按目标目录所在磁盘类型决定并行度：
	/// 机械盘随机读要付寻道代价，并行反而更慢；SSD 可以放心并行；探测失败取保守值。
	/// SeekPenalty 查询走 IOCTL_STORAGE_QUERY_PROPERTY，不需要管理员权限。
	/// </summary>
	private static int ResolveNpkReadConcurrency(string directoryPath)
	{
		HardwareType hardwareType = HardwareType.Unknown;
		try
		{
			string root = Path.GetPathRoot(Path.GetFullPath(directoryPath));
			if (!string.IsNullOrEmpty(root))
			{
				hardwareType = DiskDetectionUtils.DetectDrive(root).HardwareType;
			}
		}
		catch
		{
			hardwareType = HardwareType.Unknown;
		}
		if (hardwareType == HardwareType.Hdd)
		{
			return 1;
		}
		if (hardwareType == HardwareType.Ssd)
		{
			return 4;
		}
		return 2;
	}

	/// <summary>NPK 索引记录：offset(4) + length(4) + 加密文件名(256)。</summary>
	private const int NpkIndexRecordSize = 264;
	private const int NpkIndexNameOffset = 8;

	private const int NpkIndexNameLength = 256;

	private static readonly Encoding GbkEncoding = System.Text.Encoding.GetEncoding("gbk");

	/// <summary>解密一个 256 字节的 NPK 文件名区，返回去掉结尾 0 的字符串。</summary>
	private static string DecodeNpkName(ReadOnlySpan<byte> raw)
	{
		byte[] keyBytes = Key;
		Span<byte> buffer = stackalloc byte[NpkIndexNameLength];
		int length = 0;
		for (int i = 0; i < NpkIndexNameLength; i++)
		{
			byte value = (byte)(raw[i] ^ keyBytes[i]);
			if (value == 0)
			{
				break;
			}
			buffer[length++] = value;
		}
		return GbkEncoding.GetString(buffer.Slice(0, length));
	}

	private static void ReadNpkInfo2(string filePath, ConcurrentDictionary<string, UtImgFileTemp> dic, StringBuilder buiError, ConcurrentDictionary<int, string> npkFilePathDic)
	{
		if (!File.Exists(filePath))
		{
			return;
		}
		string fileName = Path.GetFileName(filePath);
		int fileNameId = Interlocked.Increment(ref _fileNameIdCounter);
		npkFilePathDic.TryAdd(fileNameId, fileName);
		try
		{
			// 索引区一次读一条 264 字节记录，替代原来"ReadInt + Seek + ReadPath(256 次 ReadByte)"
			byte[] record = new byte[NpkIndexRecordSize];
			int entryCount;
			(int offset, string name)[] entries;
			using (FileStream fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan))
			{
				Span<byte> head = stackalloc byte[20];
				if (fileStream.ReadAtLeast(head, 20, throwOnEndOfStream: false) < 20)
				{
					return;
				}
				// 注意：SequenceEqual 要求两侧长度相同，签名 "NeoplePack_Bill" 是 15 字节（不含结尾 0）
				if (!head.Slice(0, 15).SequenceEqual("NeoplePack_Bill"u8))
				{
					return;
				}
				entryCount = BinaryPrimitives.ReadInt32LittleEndian(head.Slice(16, 4));
				if (entryCount <= 0)
				{
					return;
				}
				entries = new (int, string)[entryCount];
				for (int i = 0; i < entryCount; i++)
				{
					if (fileStream.ReadAtLeast(record, NpkIndexRecordSize, throwOnEndOfStream: false) < NpkIndexRecordSize)
					{
						entries[i] = (-1, null);
						continue;
					}
					int offset = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(0, 4));
					string name = DecodeNpkName(record.AsSpan(NpkIndexNameOffset, NpkIndexNameLength));
					entries[i] = (offset, name);
				}
			}
			// 第二遍只需要每张图的签名和帧数：一次读 40 字节，替代原来两次 Seek + 两轮逐字节读取
			using SafeFileHandle handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			byte[] imageHeader = new byte[40];
			for (int j = 0; j < entries.Length; j++)
			{
				int offset = entries[j].offset;
				string name = entries[j].name;
				if (offset <= 0 || name == null)
				{
					continue;
				}
				int read = RandomAccess.Read(handle, imageHeader, offset);
				if (read < 20)
				{
					continue;
				}
				int signatureLength;
				if (imageHeader.AsSpan(0, 15).SequenceEqual("Neople Img File"u8))
				{
					signatureLength = 15;
				}
				else if (read >= 17 && imageHeader.AsSpan(0, 17).SequenceEqual("Neople Image File"u8))
				{
					signatureLength = 17;
				}
				else
				{
					continue;
				}
				// 原实现：ReadString 消费"签名 + 结尾 0"，再相对 Seek(12)，所以帧数在 offset + 签名长 + 13
				int countOffset = signatureLength + 13;
				if (countOffset + 4 > read)
				{
					continue;
				}
				int count = BinaryPrimitives.ReadInt32LittleEndian(imageHeader.AsSpan(countOffset, 4));
				if (!dic.TryGetValue(name, out UtImgFileTemp entry))
				{
					entry = new UtImgFileTemp(offset, fileNameId);
					if (!dic.TryAdd(name, entry))
					{
						dic.TryGetValue(name, out entry);
					}
				}
				if (entry != null)
				{
					entry.Count = count;
				}
			}
		}
		catch (IOException ex) when (ex.InnerException == null || ex.InnerException is ArgumentException)
		{
			if (ex.Message.Contains("because it is being used by another process"))
			{
				lock (buiError)
				{
					buiError.AppendLine("文件被其他进程占用无法读取：" + filePath);
					return;
				}
			}
		}
		catch (Exception ex2)
		{
			lock (buiError)
			{
				StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(12, 2, buiError);
				handler.AppendLiteral("读取文件 ");
				handler.AppendFormatted(filePath);
				handler.AppendLiteral(" 时出现错误：");
				handler.AppendFormatted(ex2.Message);
				buiError.AppendLine(ref handler);
			}
		}
	}

	public static ConcurrentDictionary<string, ConcurrentDictionary<int, ImageSource>> ReadNpkTreeIcon(ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentHashSet<int>>> tsource)
	{
		ConcurrentDictionary<string, ConcurrentDictionary<int, ImageSource>> DIC = new ConcurrentDictionary<string, ConcurrentDictionary<int, ImageSource>>();
		TaskFactory taskFactory = new TaskFactory(new LimitedConcurrencyLevelTaskScheduler(10));
		List<Task> list = new List<Task>();
		foreach (KeyValuePair<string, ConcurrentDictionary<string, ConcurrentHashSet<int>>> item in tsource)
		{
			list.Add(taskFactory.StartNew(delegate
			{
				ReadNpkInfoFrom(item.Key, item.Value, DIC);
			}));
		}
		Task.WaitAll(list.ToArray());
		return DIC;
	}

	private static void ReadNpkInfoFrom(string filePath, ConcurrentDictionary<string, ConcurrentHashSet<int>> li, ConcurrentDictionary<string, ConcurrentDictionary<int, ImageSource>> DIC)
	{
		try
		{
			using Stream stream = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (stream.ReadString() == "NeoplePack_Bill")
			{
				stream.Seek(0L, SeekOrigin.Begin);
				if (ReadInfoFrom(stream, li, DIC))
				{
					stream.Seek(32L);
				}
			}
		}
		catch (Exception)
		{
		}
	}

	public static bool ReadInfoFrom(Stream stream, ConcurrentDictionary<string, ConcurrentHashSet<int>> list, ConcurrentDictionary<string, ConcurrentDictionary<int, ImageSource>> DIC)
	{
		if (stream.ReadString() != "NeoplePack_Bill")
		{
			return false;
		}
		int num = stream.ReadInt();
		ConcurrentHashSet<string> concurrentHashSet = new ConcurrentHashSet<string>();
		for (int i = 0; i < num; i++)
		{
			ImagePack imagePack = new ImagePack
			{
				Offset = stream.ReadInt(),
				Length = stream.ReadInt(),
				Path = stream.ReadPath()
			};
			if (!list.ContainsKey(imagePack.Path) || concurrentHashSet.Contains(imagePack.Path))
			{
				continue;
			}
			long position = stream.Position;
			concurrentHashSet.Add(imagePack.Path);
			stream.Seek(imagePack.Offset, SeekOrigin.Begin);
			string text = stream.ReadString();
			if (text == "Neople Img File")
			{
				imagePack.IndexLength = stream.ReadLong();
				imagePack.Version = (ImgVersion)stream.ReadInt();
				imagePack.Count = stream.ReadInt();
				ConcurrentDictionary<int, ImageSource> concurrentDictionary = new ConcurrentDictionary<int, ImageSource>();
				imagePack.IniHandle2(stream, list[imagePack.Path], concurrentDictionary);
				if (concurrentDictionary.Count > 0)
				{
					DIC.TryAdd(imagePack.Path, concurrentDictionary);
				}
			}
			else if (text == "Neople Image File")
			{
				ConcurrentDictionary<int, ImageSource> concurrentDictionary2 = new ConcurrentDictionary<int, ImageSource>();
				imagePack.Version = ImgVersion.Ver1;
				imagePack.IniHandle2(stream, list[imagePack.Path], concurrentDictionary2);
				if (concurrentDictionary2.Count > 0)
				{
					DIC.TryAdd(imagePack.Path, concurrentDictionary2);
				}
			}
			stream.Position = position;
		}
		return true;
	}

	public static void ReadImg(Stream stream, ImagePack pack, long length)
	{
		stream.Seek(pack.Offset, SeekOrigin.Begin);
		string text = stream.ReadString();
		if (text == "Neople Img File")
		{
			pack.IndexLength = stream.ReadLong();
			pack.Version = (ImgVersion)stream.ReadInt();
			pack.Count = stream.ReadInt();
			pack.InitHandle(stream);
			return;
		}
		if (text == "Neople Image File")
		{
			pack.Version = ImgVersion.Ver1;
		}
		else
		{
			if (length < 0)
			{
				length = stream.Length;
			}
			pack.Version = ImgVersion.Other;
			stream.Seek(pack.Offset, SeekOrigin.Begin);
			if (pack.Name.ToLower().EndsWith(".ogg"))
			{
				pack.Version = ImgVersion.Other;
				pack.IndexLength = length - stream.Position;
			}
		}
		pack.InitHandle(stream);
	}

	public static void ReadImg(Stream stream, ImagePack album)
	{
		ReadImg(stream, album, -1L);
	}

	public static ImagePack ReadImg(Stream stream, string path)
	{
		return ReadImg(stream, path, -1L);
	}

	public static ImagePack ReadImg(Stream stream, string path, long length)
	{
		ImagePack imagePack = new ImagePack
		{
			Path = path
		};
		ReadImg(stream, imagePack, length);
		return imagePack;
	}

	public static void ReadImg(byte[] data, ImagePack album)
	{
		ReadImg(data, album, -1L);
	}

	public static ImagePack ReadImg(byte[] data, string path)
	{
		return ReadImg(data, path, -1L);
	}

	public static void ReadImg(byte[] data, ImagePack album, long length)
	{
		using MemoryStream stream = new MemoryStream(data);
		ReadImg(stream, album, length);
	}

	public static ImagePack ReadImg(byte[] data, string path, long length)
	{
		using MemoryStream stream = new MemoryStream(data);
		return ReadImg(stream, path, length);
	}

	public static void WriteNpk(Stream stream, List<ImagePack> List)
	{
		int num = 52 + List.Count * 264;
		int num2 = 0;
		for (int i = 0; i < List.Count; i++)
		{
			List[i].Adjust();
			if (i > 0)
			{
				if (List[i].Target != null)
				{
					continue;
				}
				num += num2;
			}
			List[i].Offset = num;
			num2 = List[i].Length;
		}
		List.ForEach(delegate(ImagePack e)
		{
			if (e.Target != null)
			{
				e.Offset = e.Target.Offset;
				e.Length = e.Target.Length;
			}
		});
		MemoryStream memoryStream = new MemoryStream();
		memoryStream.WriteString("NeoplePack_Bill");
		memoryStream.WriteInt(List.Count);
		foreach (ImagePack item in List)
		{
			memoryStream.WriteInt(item.Offset);
			memoryStream.WriteInt(item.Length);
			memoryStream.WritePath(item.Path);
		}
		memoryStream.Close();
		byte[] array = memoryStream.ToArray();
		stream.Write(array);
		stream.Write(CompileHash(array));
		foreach (ImagePack item2 in List)
		{
			if (item2.Target == null)
			{
				stream.Write(item2.Data);
			}
		}
	}

	public static List<ImagePack> Find(IEnumerable<ImagePack> Items, params string[] args)
	{
		return Find(Items, allCheck: false, args);
	}

	public static List<ImagePack> Find(IEnumerable<ImagePack> Items, bool allCheck, params string[] args)
	{
		return new List<ImagePack>(Items.Where(delegate(ImagePack item)
		{
			if (!allCheck && args.Length == 0)
			{
				return true;
			}
			return (!allCheck || args[0].Equals(item.Name)) && args.All((string arg) => item.Path.Contains(arg));
		}));
	}

	public static string GetFilePath(this ImagePack file)
	{
		string text = file.Path;
		int num = text.LastIndexOf("/");
		if (num > -1)
		{
			text = text.Substring(0, num);
		}
		text = text.Replace("/", "_");
		return text + ".NPK";
	}

	public static List<ImagePack> Load(string file)
	{
		return Load(onlyPath: false, file);
	}

	public static List<ImagePack> Load(bool onlyPath, string file)
	{
		List<ImagePack> result = new List<ImagePack>();
		if (Directory.Exists(file))
		{
			return Load(onlyPath, Directory.GetFiles(file));
		}
		if (!File.Exists(file))
		{
			return result;
		}
		using FileStream stream = File.OpenRead(file);
		if (onlyPath)
		{
			return ReadInfo(stream);
		}
		return ReadNpk(stream, file);
	}

	public static List<ImagePack> Load(bool onlyPath, params string[] files)
	{
		List<ImagePack> list = new List<ImagePack>();
		foreach (string file in files)
		{
			list.AddRange(Load(onlyPath, file));
		}
		return list;
	}

	public static List<ImagePack> Load(params string[] files)
	{
		return Load(onlyPath: false, files);
	}

	public static ImagePack LoadWithPath(string file, string name)
	{
		using FileStream stream = File.OpenRead(file);
		return LoadWithPath(stream, name);
	}

	public static ImagePack LoadWithPath(Stream stream, string name)
	{
		List<ImagePack> list = LoadWithPathArray(stream, name);
		if (list.Count > 0)
		{
			return list[0];
		}
		return null;
	}

	public static List<ImagePack> LoadWithPathArray(string file, params string[] args)
	{
		using FileStream stream = File.OpenRead(file);
		return LoadWithPathArray(stream, args);
	}

	public static List<ImagePack> LoadWithPathArray(Stream stream, params string[] paths)
	{
		return LoadAll(stream, (ImagePack e) => Enumerable.Contains(paths, e.Path));
	}

	public static List<ImagePack> LoadAll(string file, Predicate<ImagePack> predicate)
	{
		using FileStream stream = File.OpenRead(file);
		return LoadAll(stream, predicate);
	}

	public static List<ImagePack> LoadAll(Stream stream, Predicate<ImagePack> predicate)
	{
		List<ImagePack> list = ReadInfo(stream);
		list = list.FindAll(predicate);
		foreach (ImagePack item in list)
		{
			stream.Seek(item.Offset, SeekOrigin.Begin);
			ReadImg(stream, item, stream.Length);
		}
		return list;
	}

	public static void Save(string file, List<ImagePack> list)
	{
		using FileStream stream = File.Open(file, FileMode.Create);
		WriteNpk(stream, list);
	}

	public static void SaveToDirectory(string dir, IEnumerable<ImagePack> array)
	{
		foreach (ImagePack item in array)
		{
			item.Save(dir + "/" + item.Name);
		}
	}

	public static void Compare(string gamePath, Action<ImagePack, ImagePack> restore, params ImagePack[] array)
	{
		Compare(gamePath, "ImagePacks2", restore, array);
	}

	public static void Compare(string gamePath, string dir, Action<ImagePack, ImagePack> restore, params ImagePack[] array)
	{
		Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>();
		ImagePack[] array2 = array;
		foreach (ImagePack imagePack in array2)
		{
			string filePath = imagePack.GetFilePath();
			filePath = $"{gamePath}/{dir}/{filePath}";
			if (!dictionary.ContainsKey(filePath))
			{
				dictionary.Add(filePath, new List<string>());
			}
			dictionary[filePath].Add(imagePack.Path);
		}
		List<ImagePack> list = new List<ImagePack>();
		foreach (string key in dictionary.Keys)
		{
			list.AddRange(LoadWithPathArray(key, dictionary[key].ToArray()));
		}
		array2 = array;
		foreach (ImagePack imagePack2 in array2)
		{
			foreach (ImagePack item in list)
			{
				if (imagePack2.Path.Equals(item.Path))
				{
					restore(item, imagePack2);
				}
			}
		}
	}
}
