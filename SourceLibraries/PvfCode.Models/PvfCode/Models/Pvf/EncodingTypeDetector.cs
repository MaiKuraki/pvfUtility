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
/// - UTF-8：字节结构严格（首字节 0xC0-0xF7 + 续字节 0x80-0xBF），GBK/Big5 文本几乎不可能全部通过校验
/// - UTF-16：ASCII 字符的高字节为 0，字节流里 0x00 的比例明显偏高
///
/// 不可靠、因此**不做**判断：
/// - 简体(936) 与 繁体(950)：双字节范围高度重叠，用错编码照样解出合法汉字，
///   纯统计无法区分。强行猜只会把用户带进沟里。
///
/// 因此本类返回的 suggested 可能为 null（表示无法判断），由调用方输出样例文本供人工确认。
/// </summary>
public static class EncodingTypeDetector
{
	private const int MaxSamples = 120;
	private const int MaxSampleBytes = 96;

	/// <summary>探测结果。</summary>
	public readonly struct Result
	{
		/// <summary>建议使用的编码；null 表示无法可靠判断。</summary>
		public EncodingType? Suggested { get; init; }

		/// <summary>当前配置编码解出的样例（最多 3 条），用于人工判断是否乱码。</summary>
		public string[] Samples { get; init; }

		/// <summary>是否明确指向 UTF-8 / Unicode 这类结构性编码。</summary>
		public bool Structural { get; init; }
	}

	public static Result Detect(byte[] stringTableBytes, EncodingType current)
	{
		string[] samples = BuildReadableSamples(stringTableBytes, current);
		if (stringTableBytes == null || stringTableBytes.Length < 16)
		{
			return new Result { Suggested = null, Samples = samples, Structural = false };
		}
		List<(int Start, int Length)> ranges = ExtractRanges(stringTableBytes);
		if (ranges.Count == 0)
		{
			return new Result { Suggested = null, Samples = samples, Structural = false };
		}

		// 1) UTF-16LE：ASCII 在 UTF-16 里高字节为 0，样本中 0x00 占比会明显偏高。
		//    必须先判这个：UTF-16 的字节流（如 "U\0n\0"）同时也是一串合法 UTF-8，会被下一步误判。
		if (LooksLikeUtf16(stringTableBytes, ranges))
		{
			return new Result { Suggested = EncodingType.Unicode, Samples = samples, Structural = true };
		}

		// 2) UTF-8：严格解码全部通过，且样本里存在非 ASCII 字节，才认为是 UTF-8。
		//    纯 ASCII 的样本对所有编码都合法，不能作为判据。
		if (HasNonAsciiBytes(stringTableBytes, ranges) && IsStrictlyDecodable(stringTableBytes, ranges, new UTF8Encoding(false, true)))
		{
			return new Result { Suggested = EncodingType.UTF8, Samples = samples, Structural = true };
		}

		// 3) 其余双字节编码：只有"某个编码完全无解码失败、而其他编码有失败"时才给建议。
		//    简体/繁体通常都无失败，此时返回 null，交给用户看样例判断。
		EncodingType? onlyClean = FindSingleCleanEncoding(stringTableBytes, ranges);
		return new Result { Suggested = onlyClean, Samples = samples, Structural = false };
	}

	/// <summary>用指定编码解出前几条字符串，供人工判断是否乱码。</summary>
	private static string[] BuildReadableSamples(byte[] bytes, EncodingType encodingType)
	{
		List<(int Start, int Length)> ranges = ExtractRanges(bytes);
		if (ranges.Count == 0)
		{
			return Array.Empty<string>();
		}
		Encoding encoding;
		try
		{
			encoding = Encoding.GetEncoding((int)encodingType, EncoderFallback.ReplacementFallback, new DecoderReplacementFallback("\uFFFD"));
		}
		catch
		{
			return Array.Empty<string>();
		}
		int take = Math.Min(3, ranges.Count);
		string[] samples = new string[take];
		int written = 0;
		for (int i = 0; i < ranges.Count && written < take; i++)
		{
			try
			{
				string text = encoding.GetString(bytes, ranges[i].Start, ranges[i].Length).Replace("\0", string.Empty).Trim();
				if (text.Length == 0)
				{
					continue;
				}
				if (text.Length > 24)
				{
					text = text.Substring(0, 24) + "…";
				}
				samples[written++] = text;
			}
			catch
			{
			}
		}
		if (written == take)
		{
			return samples;
		}
		string[] trimmed = new string[written];
		Array.Copy(samples, trimmed, written);
		return trimmed;
	}

	private static List<(int Start, int Length)> ExtractRanges(byte[] bytes)
	{
		List<(int, int)> ranges = new List<(int, int)>(MaxSamples);
		if (bytes == null || bytes.Length < 8)
		{
			return ranges;
		}
		int count = BitConverter.ToInt32(bytes, 0);
		if (count <= 0)
		{
			return ranges;
		}
		int limit = Math.Min(count, MaxSamples);
		for (int i = 0; i < limit; i++)
		{
			int indexOffset = i * 4 + 4;
			if (indexOffset + 8 > bytes.Length)
			{
				break;
			}
			int start = BitConverter.ToInt32(bytes, indexOffset);
			int end = BitConverter.ToInt32(bytes, indexOffset + 4);
			int length = end - start;
			if (length <= 0 || start < 0 || start + 4 + length > bytes.Length)
			{
				continue;
			}
			if (length > MaxSampleBytes)
			{
				length = MaxSampleBytes;
			}
			ranges.Add((start + 4, length));
		}
		return ranges;
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
		// 纯 ASCII 的 UTF-16 文本，约一半字节是 0；中文 UTF-16 也会有大量 0 出现在高字节
		return total > 0 && zeroCount * 100 / total >= 25;
	}

	/// <summary>
	/// 找出"唯一一个能干净解码"的双字节编码。
	/// 简体/繁体通常都干净，此时返回 null（不猜）。
	/// </summary>
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
