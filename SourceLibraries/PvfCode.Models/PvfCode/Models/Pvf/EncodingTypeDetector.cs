using System;
using System.Collections.Generic;
using System.Text;
using PvfCode.Models.Pvf.Enums;

namespace PvfCode.Models.Pvf;

/// <summary>
/// PVF 字符串表编码探测。
///
/// 设计原则：只判断"能可靠判断的"，判断不了就老实说不知道，并给出解码样例让用户自己看。
///
/// 可靠判断：
/// - UTF-16LE：ASCII 字符的高字节为 0，字节流里 0x00 比例明显偏高（必须先于 UTF-8 判断，
///   因为 UTF-16 的字节流如 "U\0n\0" 同时也是一串合法 UTF-8）
/// - UTF-8：字节结构严格，严格解码全部通过且样本含非 ASCII 字节
///
/// 不做判断：
/// - 简体(936) 与 繁体(950)：双字节范围高度重叠，用错编码照样解出合法汉字，纯统计无法区分。
///
/// 采样策略：stringtable 开头大量是文件路径（纯 ASCII），任何编码解出来都一样，
/// 所以取样时**优先挑含 >=0x80 字节的条目**，否则样例和检测都看不出问题。
/// </summary>
public static class EncodingTypeDetector
{
	private const int MaxDetectSamples = 120;

	private const int MaxScanEntries = 300000;

	private const int MaxSampleBytes = 96;

	/// <summary>探测结果。</summary>
	public readonly struct Result
	{
		/// <summary>建议使用的编码；null 表示无法可靠判断。</summary>
		public EncodingType? Suggested { get; init; }

	}

	public static Result Detect(byte[] stringTableBytes)
	{
		List<(int Start, int Length)> ranges = ExtractRanges(stringTableBytes, MaxDetectSamples);
		if (ranges.Count == 0)
		{
			return new Result { Suggested = null };
		}

		// 1) UTF-16LE：0x00 占比明显偏高
		if (LooksLikeUtf16(stringTableBytes, ranges))
		{
			return new Result { Suggested = EncodingType.Unicode };
		}

		// 2) UTF-8：严格解码全部通过，且样本里确实存在非 ASCII 字节
		if (HasNonAsciiBytes(stringTableBytes, ranges) && IsStrictlyDecodable(stringTableBytes, ranges, new UTF8Encoding(false, true)))
		{
			return new Result { Suggested = EncodingType.UTF8 };
		}

		// 3) 其余双字节编码只在"唯一一个能干净解码"时才给建议；简体/繁体通常都干净，返回 null
		return new Result { Suggested = FindSingleCleanEncoding(stringTableBytes, ranges) };
	}

	/// <summary>
	/// 取样：优先收集含高位字节（中文/韩文/日文）的条目，不够再补 ASCII 条目。
	/// 只取前若干条会全部落在文件路径上，对判断编码毫无帮助。
	/// </summary>
	private static List<(int Start, int Length)> ExtractRanges(byte[] bytes, int maxCount)
	{
		List<(int Start, int Length)> preferred = new List<(int, int)>(maxCount);
		List<(int Start, int Length)> fallback = new List<(int, int)>(maxCount);
		if (bytes == null || bytes.Length < 8)
		{
			return preferred;
		}
		int count = BitConverter.ToInt32(bytes, 0);
		if (count <= 0)
		{
			return preferred;
		}
		int limit = Math.Min(count, MaxScanEntries);
		for (int i = 0; i < limit; i++)
		{
			int indexOffset = i * 4 + 4;
			if (indexOffset + 8 > bytes.Length)
			{
				break;
			}
			int rawStart = BitConverter.ToInt32(bytes, indexOffset);
			int end = BitConverter.ToInt32(bytes, indexOffset + 4);
			// offsets[i] 指向 4 字节长度字段，数据在其后；跨度里含这 4 字节，要减掉，
			// 否则会读到下一条的长度字段，末尾那条还会因越界被跳过。
			int length = end - rawStart - 4;
			int dataStart = rawStart + 4;
			if (length <= 0 || dataStart < 0 || dataStart + length > bytes.Length)
			{
				continue;
			}
			if (length > MaxSampleBytes)
			{
				length = MaxSampleBytes;
			}
			bool hasHighByte = false;
			for (int p = dataStart; p < dataStart + length; p++)
			{
				if (bytes[p] >= 0x80)
				{
					hasHighByte = true;
					break;
				}
			}
			if (hasHighByte)
			{
				preferred.Add((dataStart, length));
				if (preferred.Count >= maxCount)
				{
					break;
				}
			}
			else if (fallback.Count < maxCount)
			{
				fallback.Add((dataStart, length));
			}
		}
		if (preferred.Count > 0)
		{
			return preferred;
		}
		return fallback;
	}

	private static bool HasNonAsciiBytes(byte[] bytes, List<(int Start, int Length)> ranges)
	{
		for (int i = 0; i < ranges.Count; i++)
		{
			int end = ranges[i].Start + ranges[i].Length;
			for (int p = ranges[i].Start; p < end; p++)
			{
				if (bytes[p] >= 0x80)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static bool IsStrictlyDecodable(byte[] bytes, List<(int Start, int Length)> ranges, Encoding strictEncoding)
	{
		for (int i = 0; i < ranges.Count; i++)
		{
			try
			{
				strictEncoding.GetString(bytes, ranges[i].Start, ranges[i].Length);
			}
			catch (DecoderFallbackException)
			{
				return false;
			}
			catch (ArgumentException)
			{
				return false;
			}
		}
		return true;
	}

	private static bool LooksLikeUtf16(byte[] bytes, List<(int Start, int Length)> ranges)
	{
		long zeroCount = 0;
		long total = 0;
		for (int i = 0; i < ranges.Count; i++)
		{
			int end = ranges[i].Start + ranges[i].Length;
			for (int p = ranges[i].Start; p < end; p++)
			{
				total++;
				if (bytes[p] == 0)
				{
					zeroCount++;
				}
			}
		}
		return total > 0 && zeroCount * 100 / total >= 25;
	}

	private static EncodingType? FindSingleCleanEncoding(byte[] bytes, List<(int Start, int Length)> ranges)
	{
		EncodingType[] candidates = new EncodingType[4]
		{
			EncodingType.TW,
			EncodingType.CN,
			EncodingType.KR,
			EncodingType.JP
		};
		int cleanCount = 0;
		EncodingType cleanType = EncodingType.TW;
		for (int i = 0; i < candidates.Length; i++)
		{
			Encoding encoding;
			try
			{
				encoding = Encoding.GetEncoding((int)candidates[i], EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
			}
			catch
			{
				continue;
			}
			if (!IsStrictlyDecodable(bytes, ranges, encoding))
			{
				continue;
			}
			cleanCount++;
			cleanType = candidates[i];
		}
		if (cleanCount == 1)
		{
			return cleanType;
		}
		return null;
	}
}
