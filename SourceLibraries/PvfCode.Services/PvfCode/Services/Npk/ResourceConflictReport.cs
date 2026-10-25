using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;
using PvfCode.NPK.Utils.Models;

namespace PvfCode.Services.Npk;

/// <summary>
/// ImagePacks2 资源冲突报告。
///
/// 回答两个问题：
/// 1. 同一个图片路径被多个 NPK 提供时，两边是**同一个文件**还是**内容不同**；
/// 2. 两个 NPK 之间有交集时，是"小包被大包完全覆盖"还是"各有独有资源"。
///
/// 为什么不直接给"可删除"结论：DNF 客户端里常见作者做小补丁包覆盖官方资源，
/// 即使两边内容相同，删除后加载顺序变化也可能改变表现。
/// 所以报告只做到"按证据分类 + 给出风险提示"，删不删由使用者决定。
/// </summary>
public sealed class ResourceConflictReport
{
	/// <summary>参与统计的图片条目总数。</summary>
	public int TotalImageCount { get; init; }

	/// <summary>同一路径被多个 NPK 提供的条目。</summary>
	public IReadOnlyList<DuplicateImageEntry> MultiSourceImages { get; init; }

	/// <summary>存在交集的 NPK 组合。</summary>
	public IReadOnlyList<NpkOverlapEntry> NpkOverlaps { get; init; }

	/// <summary>交集里内容不同的条数（覆盖关系）。</summary>
	public int DifferentContentCount { get; init; }

	/// <summary>交集里内容一致的条数（疑似重复打包）。</summary>
	public int IdenticalContentCount { get; init; }

	public static ResourceConflictReport Empty { get; } = new ResourceConflictReport
	{
		TotalImageCount = 0,
		MultiSourceImages = Array.Empty<DuplicateImageEntry>(),
		NpkOverlaps = Array.Empty<NpkOverlapEntry>()
	};

	public static ResourceConflictReport Build(
		ConcurrentDictionary<string, ConcurrentBag<NpkImageSourceInfo>> tracker,
		IReadOnlyDictionary<int, string> npkNames,
		ICollection<UtImgFile> images)
	{
		if (tracker == null || tracker.Count == 0)
		{
			return new ResourceConflictReport
			{
				TotalImageCount = images?.Count ?? 0,
				MultiSourceImages = Array.Empty<DuplicateImageEntry>(),
				NpkOverlaps = Array.Empty<NpkOverlapEntry>()
			};
		}

		// 每个 NPK 各有多少张图，用于判断"小包是否被完全包含"和算占比
		Dictionary<int, int> countByNpk = new Dictionary<int, int>();
		if (images != null)
		{
			foreach (UtImgFile image in images)
			{
				int id = image.FileNameId;
				countByNpk.TryGetValue(id, out int existingCount);
				countByNpk[id] = existingCount + 1;
			}
		}

		List<DuplicateImageEntry> multiSource = new List<DuplicateImageEntry>();
		Dictionary<long, int> pairOverlap = new Dictionary<long, int>();
		Dictionary<long, int> pairDifferent = new Dictionary<long, int>();
		int differentTotal = 0;
		int identicalTotal = 0;

		foreach (KeyValuePair<string, ConcurrentBag<NpkImageSourceInfo>> item in tracker)
		{
			// 按 NPK 去重（同一个包可能被记录多次），再按 id 排序保证输出稳定
			Dictionary<int, NpkImageSourceInfo> byNpk = new Dictionary<int, NpkImageSourceInfo>();
			foreach (NpkImageSourceInfo source in item.Value)
			{
				byNpk[source.NpkId] = source;
			}
			if (byNpk.Count < 2)
			{
				continue;
			}
			int[] ids = new int[byNpk.Count];
			byNpk.Keys.CopyTo(ids, 0);
			Array.Sort(ids);

			string[] names = new string[ids.Length];
			int[] lengths = new int[ids.Length];
			bool sameContent = true;
			int firstLength = 0;
			for (int i = 0; i < ids.Length; i++)
			{
				names[i] = GetNpkName(npkNames, ids[i]);
				lengths[i] = byNpk[ids[i]].Length;
				if (i == 0)
				{
					firstLength = lengths[i];
				}
				else if (lengths[i] != firstLength)
				{
					sameContent = false;
				}
			}
			if (sameContent)
			{
				identicalTotal++;
			}
			else
			{
				differentTotal++;
			}

			multiSource.Add(new DuplicateImageEntry
			{
				ImagePath = item.Key,
				NpkFiles = names,
				SourceLengths = lengths,
				SameContent = sameContent
			});

			for (int i = 0; i < ids.Length; i++)
			{
				for (int j = i + 1; j < ids.Length; j++)
				{
					long key = ((long)ids[i] << 32) | (uint)ids[j];
					pairOverlap.TryGetValue(key, out int count);
					pairOverlap[key] = count + 1;
					if (!sameContent)
					{
						pairDifferent.TryGetValue(key, out int diff);
						pairDifferent[key] = diff + 1;
					}
				}
			}
		}

		// 内容不同的排在前面：那才是改补丁时真正会踩坑的地方
		multiSource.Sort(delegate(DuplicateImageEntry left, DuplicateImageEntry right)
		{
			if (left.SameContent != right.SameContent)
			{
				return left.SameContent ? 1 : -1;
			}
			int byCount = right.NpkFiles.Count.CompareTo(left.NpkFiles.Count);
			return byCount != 0 ? byCount : string.CompareOrdinal(left.ImagePath, right.ImagePath);
		});

		List<NpkOverlapEntry> overlaps = new List<NpkOverlapEntry>(pairOverlap.Count);
		foreach (KeyValuePair<long, int> pair in pairOverlap)
		{
			int idA = (int)(pair.Key >> 32);
			int idB = (int)(pair.Key & 0xFFFFFFFFL);
			countByNpk.TryGetValue(idA, out int countA);
			countByNpk.TryGetValue(idB, out int countB);
			pairDifferent.TryGetValue(pair.Key, out int diffCount);
			overlaps.Add(new NpkOverlapEntry
			{
				NpkA = GetNpkName(npkNames, idA),
				NpkB = GetNpkName(npkNames, idB),
				OverlapCount = pair.Value,
				DifferentContentCount = diffCount,
				CountA = countA,
				CountB = countB
			});
		}
		overlaps.Sort(delegate(NpkOverlapEntry left, NpkOverlapEntry right)
		{
			return right.OverlapCount.CompareTo(left.OverlapCount);
		});

		return new ResourceConflictReport
		{
			TotalImageCount = images?.Count ?? 0,
			MultiSourceImages = multiSource,
			NpkOverlaps = overlaps,
			DifferentContentCount = differentTotal,
			IdenticalContentCount = identicalTotal
		};
	}

	private static string GetNpkName(IReadOnlyDictionary<int, string> npkNames, int id)
	{
		if (npkNames != null && npkNames.TryGetValue(id, out string name))
		{
			return name;
		}
		return "#" + id;
	}

	public string BuildMarkdown(string imagePacks2Path)
	{
		StringBuilder builder = new StringBuilder(16384);
		builder.AppendLine("# 资源冲突报告");
		builder.AppendLine();
		builder.AppendLine("| 项目 | 值 |");
		builder.AppendLine("| --- | --- |");
		builder.AppendLine("| 生成时间 | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " |");
		builder.AppendLine("| ImagePacks2 | `" + (imagePacks2Path ?? "(未设置)") + "` |");
		builder.AppendLine("| 图片条目 | " + TotalImageCount + " |");
		builder.AppendLine("| 存在交集的包对 | " + NpkOverlaps.Count + " 组 |");
		builder.AppendLine("| 交集里内容不同 | **" + DifferentContentCount + "** 条 |");
		builder.AppendLine("| 交集里内容一致 | " + IdenticalContentCount + " 条 |");
		builder.AppendLine();
		builder.AppendLine("> 本报告只陈述事实，不给「删除建议」。常见做法是用小补丁包覆盖官方资源，");
		builder.AppendLine("> 即使两边内容相同，删除后加载顺序变化也可能改变表现。**删除前务必备份并在游戏里实测。**");
		builder.AppendLine();

		WriteDifferentSection(builder);
		WriteIdenticalSection(builder);
		WriteOverlapSummary(builder);
		return builder.ToString();
	}

	/// <summary>
	/// 生成机器可读的 JSON。字段名与文档固定，便于外部脚本消费（例如跨目录补齐工具）。
	/// 关系判断用英文枚举码，中文说明只放在 Markdown 里。
	/// </summary>
	public string BuildJson(string imagePacks2Path)
	{
		// 手写 List 而不是 LINQ：这里是一次性构建，但保持与项目其余热路径一致的风格
		List<object> duplicateItems = new List<object>(MultiSourceImages.Count);
		for (int i = 0; i < MultiSourceImages.Count; i++)
		{
			DuplicateImageEntry entry = MultiSourceImages[i];
			List<object> sources = new List<object>(entry.NpkFiles.Count);
			for (int k = 0; k < entry.NpkFiles.Count; k++)
			{
				sources.Add(new
				{
					npk = entry.NpkFiles[k],
					bytes = k < entry.SourceLengths.Count ? entry.SourceLengths[k] : 0
				});
			}
			duplicateItems.Add(new
			{
				path = entry.ImagePath,
				sources,
				sameContent = entry.SameContent
			});
		}

		List<object> overlapItems = new List<object>(NpkOverlaps.Count);
		for (int i = 0; i < NpkOverlaps.Count; i++)
		{
			NpkOverlapEntry entry = NpkOverlaps[i];
			overlapItems.Add(new
			{
				a = entry.NpkA,
				b = entry.NpkB,
				overlap = entry.OverlapCount,
				differentContent = entry.DifferentContentCount,
				countA = entry.CountA,
				countB = entry.CountB,
				overlapPercent = Math.Round(entry.OverlapPercent, 2),
				relationship = entry.RelationshipCode
			});
		}

		object payload = new
		{
			schemaVersion = 1,
			generatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
			imagePacks2 = imagePacks2Path,
			imageCount = TotalImageCount,
			duplicates = new
			{
				total = MultiSourceImages.Count,
				differentContent = DifferentContentCount,
				identicalContent = IdenticalContentCount,
				items = duplicateItems
			},
			overlaps = new
			{
				total = NpkOverlaps.Count,
				items = overlapItems
			}
		};
		return JsonConvert.SerializeObject(payload, Formatting.Indented);
	}

	/// <summary>第一段：内容不同的重叠——覆盖关系，改动必须两边同步。</summary>
	private void WriteDifferentSection(StringBuilder builder)
	{
		builder.AppendLine("## 一、内容不同的重叠（覆盖关系）");
		builder.AppendLine();
		List<DuplicateImageEntry> items = FilterByContent(false);
		if (items.Count == 0)
		{
			builder.AppendLine("无。");
			builder.AppendLine();
			return;
		}
		builder.AppendLine("同一条路径在两个包里是**不同的文件**。游戏显示哪一个取决于客户端加载顺序，");
		builder.AppendLine("删掉任何一方都可能改变表现；修改时两个包都要改。");
		builder.AppendLine();

		Dictionary<string, List<DuplicateImageEntry>> groups = GroupByNpkPair(items, out List<string> order);
		for (int i = 0; i < order.Count; i++)
		{
			List<DuplicateImageEntry> group = groups[order[i]];
			IReadOnlyList<string> npks = group[0].NpkFiles;
			builder.AppendLine("### " + (i + 1) + ". " + DescribePair(npks) + "（" + group.Count + " 条）");
			builder.AppendLine();
			builder.AppendLine("| # | 路径 | `" + npks[0] + "` 中大小 | " + (npks.Count > 1 ? "`" + npks[1] + "` 中大小" : "-") + " |");
			builder.AppendLine("| --- | --- | --- | --- |");
			for (int k = 0; k < group.Count; k++)
			{
				DuplicateImageEntry entry = group[k];
				builder.AppendLine("| " + (k + 1) + " | `" + entry.ImagePath + "` | " + FormatBytes(entry.SourceLengths[0])
					+ " | " + (entry.SourceLengths.Count > 1 ? FormatBytes(entry.SourceLengths[1]) : "-") + " |");
			}
			builder.AppendLine();
		}
	}

	/// <summary>第二段：内容一致的重叠——疑似重复打包，但仍需实测。</summary>
	private void WriteIdenticalSection(StringBuilder builder)
	{
		builder.AppendLine("## 二、内容一致的重叠（疑似重复打包）");
		builder.AppendLine();
		List<DuplicateImageEntry> items = FilterByContent(true);
		if (items.Count == 0)
		{
			builder.AppendLine("无。");
			builder.AppendLine();
			return;
		}
		builder.AppendLine("这些路径在两个包里字节长度相同，很可能是同一张图被重复打包。");
		builder.AppendLine("**但不要据此直接删除**：如果小包是作者的替换包，删掉后加载顺序改变仍可能影响表现。");
		builder.AppendLine();

		Dictionary<string, List<DuplicateImageEntry>> groups = GroupByNpkPair(items, out List<string> order);
		for (int i = 0; i < order.Count; i++)
		{
			List<DuplicateImageEntry> group = groups[order[i]];
			IReadOnlyList<string> npks = group[0].NpkFiles;
			builder.AppendLine("### " + (i + 1) + ". " + DescribePair(npks) + "（" + group.Count + " 条）");
			builder.AppendLine();
			builder.AppendLine("| # | 路径 | 字节长度 |");
			builder.AppendLine("| --- | --- | --- |");
			for (int k = 0; k < group.Count; k++)
			{
				DuplicateImageEntry entry = group[k];
				builder.AppendLine("| " + (k + 1) + " | `" + entry.ImagePath + "` | " + FormatBytes(entry.SourceLengths[0]) + " |");
			}
			builder.AppendLine();
		}
	}

	/// <summary>第三段：包对关系总表，给出"小包是否被完全包含"的判断依据。</summary>
	private void WriteOverlapSummary(StringBuilder builder)
	{
		builder.AppendLine("## 三、包对关系总表");
		builder.AppendLine();
		if (NpkOverlaps.Count == 0)
		{
			builder.AppendLine("无。");
			return;
		}
		builder.AppendLine("| # | NPK A | NPK B | 交集 | 其中内容不同 | A 总数 | B 总数 | 占较小包 | 关系判断 |");
		builder.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- |");
		for (int i = 0; i < NpkOverlaps.Count; i++)
		{
			NpkOverlapEntry entry = NpkOverlaps[i];
			builder.AppendLine("| " + (i + 1) + " | `" + entry.NpkA + "` | `" + entry.NpkB + "` | " + entry.OverlapCount
				+ " | " + entry.DifferentContentCount + " | " + entry.CountA + " | " + entry.CountB
				+ " | " + entry.OverlapPercent.ToString("F1") + "% | " + entry.Verdict + " |");
		}
	}

	private List<DuplicateImageEntry> FilterByContent(bool sameContent)
	{
		List<DuplicateImageEntry> result = new List<DuplicateImageEntry>();
		for (int i = 0; i < MultiSourceImages.Count; i++)
		{
			if (MultiSourceImages[i].SameContent == sameContent)
			{
				result.Add(MultiSourceImages[i]);
			}
		}
		return result;
	}

	private static Dictionary<string, List<DuplicateImageEntry>> GroupByNpkPair(List<DuplicateImageEntry> items, out List<string> order)
	{
		Dictionary<string, List<DuplicateImageEntry>> groups = new Dictionary<string, List<DuplicateImageEntry>>(StringComparer.Ordinal);
		order = new List<string>();
		for (int i = 0; i < items.Count; i++)
		{
			DuplicateImageEntry entry = items[i];
			string key = string.Join(" | ", entry.NpkFiles);
			if (!groups.TryGetValue(key, out List<DuplicateImageEntry> list))
			{
				list = new List<DuplicateImageEntry>();
				groups[key] = list;
				order.Add(key);
			}
			list.Add(entry);
		}
		order.Sort(delegate(string left, string right)
		{
			return groups[right].Count.CompareTo(groups[left].Count);
		});
		return groups;
	}

	private static string DescribePair(IReadOnlyList<string> npks)
	{
		if (npks.Count == 2)
		{
			return "`" + npks[0] + "` 与 `" + npks[1] + "`";
		}
		StringBuilder builder = new StringBuilder();
		for (int i = 0; i < npks.Count; i++)
		{
			if (i > 0)
			{
				builder.Append(" + ");
			}
			builder.Append('`').Append(npks[i]).Append('`');
		}
		return builder.ToString();
	}

	private static string FormatBytes(int bytes)
	{
		if (bytes <= 0)
		{
			return "-";
		}
		if (bytes < 1024)
		{
			return bytes + " B";
		}
		if (bytes < 1024 * 1024)
		{
			return (bytes / 1024.0).ToString("F1") + " KB";
		}
		return (bytes / 1048576.0).ToString("F1") + " MB";
	}
}

/// <summary>同一路径被多个 NPK 提供。</summary>
public sealed class DuplicateImageEntry
{
	public string ImagePath { get; init; }

	public IReadOnlyList<string> NpkFiles { get; init; }

	/// <summary>各来源的字节长度，顺序与 <see cref="NpkFiles"/> 一致。</summary>
	public IReadOnlyList<int> SourceLengths { get; init; }

	/// <summary>各来源长度是否一致（一致即"疑似同一张图"）。</summary>
	public bool SameContent { get; init; }
}

/// <summary>两个 NPK 之间的重叠。</summary>
public sealed class NpkOverlapEntry
{
	public string NpkA { get; init; }

	public string NpkB { get; init; }

	public int OverlapCount { get; init; }

	/// <summary>交集里内容不同的条数。</summary>
	public int DifferentContentCount { get; init; }

	public int CountA { get; init; }

	public int CountB { get; init; }

	/// <summary>重叠条数占较小一方的比例（0~100）。</summary>
	public double OverlapPercent
	{
		get
		{
			int smaller = Math.Min(CountA, CountB);
			return smaller <= 0 ? 0.0 : OverlapCount * 100.0 / smaller;
		}
	}

	/// <summary>小包是否被大包完全包含。</summary>
	public bool SmallerIsSubset
	{
		get
		{
			int smaller = Math.Min(CountA, CountB);
			return smaller > 0 && OverlapCount >= smaller;
		}
	}

	/// <summary>机器可读的关系码，供外部脚本判断，不要翻译。</summary>
	public string RelationshipCode
	{
		get
		{
			if (OverlapCount <= 0)
			{
				return "none";
			}
			if (!SmallerIsSubset)
			{
				return "partialOverlap";
			}
			return DifferentContentCount > 0 ? "cover" : "possibleCopy";
		}
	}

	/// <summary>基于事实的关系判断，不给删除结论。</summary>
	public string Verdict
	{
		get
		{
			if (OverlapCount <= 0)
			{
				return "无交集";
			}
			if (!SmallerIsSubset)
			{
				return "各有独有资源，都不能删";
			}
			if (DifferentContentCount > 0)
			{
				return "小包是覆盖补丁（内容不同），不要删";
			}
			return "小包可能是纯副本，但仍需实测确认";
		}
	}
}
