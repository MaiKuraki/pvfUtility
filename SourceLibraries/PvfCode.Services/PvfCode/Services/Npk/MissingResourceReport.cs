using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json;

namespace PvfCode.Services.Npk;

/// <summary>
/// PVF 引用与 ImagePacks2 实际内容的比对结果。
///
/// 判断方式：从 PVF 各脚本里提取"看起来像资源路径"的字符串，归一化后到
/// ImagePacks2 的图片索引里查。查不到就是缺失。
///
/// 已知局限（必须在报告里说明，避免误导）：
/// - 路径识别是启发式的（前缀 + 扩展名），可能漏判特殊写法，也可能误判普通文本；
/// - 只处理 type=7 的直接字符串节值，字符串指针（type=9）暂未覆盖；
/// - 只比对图片，声音等其他资源不在范围内。
/// </summary>
public sealed class MissingResourceReport
{
	/// <summary>从 PVF 提取到的资源引用总数（去重后）。</summary>
	public int ReferenceCount { get; init; }

	/// <summary>归一化后参与比对的条数。</summary>
	public int CheckedCount { get; init; }

	/// <summary>在 ImagePacks2 里找不到的路径。</summary>
	public IReadOnlyList<MissingResourceEntry> Missing { get; init; }

	public static MissingResourceReport Empty { get; } = new MissingResourceReport
	{
		ReferenceCount = 0,
		CheckedCount = 0,
		Missing = Array.Empty<MissingResourceEntry>()
	};

	public string BuildMarkdown(string pvfPath, string imagePacks2Path)
	{
		StringBuilder builder = new StringBuilder(8192);
		builder.AppendLine("# 资源缺失检查");
		builder.AppendLine();
		builder.AppendLine("| 项目 | 值 |");
		builder.AppendLine("| --- | --- |");
		builder.AppendLine("| 生成时间 | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " |");
		builder.AppendLine("| PVF | `" + (pvfPath ?? "(未打开)") + "` |");
		builder.AppendLine("| ImagePacks2 | `" + (imagePacks2Path ?? "(未设置)") + "` |");
		builder.AppendLine("| PVF 中的资源引用 | " + ReferenceCount + " 条 |");
		builder.AppendLine("| 参与比对 | " + CheckedCount + " 条 |");
		builder.AppendLine("| **找不到的路径** | **" + Missing.Count + "** 条 |");
		builder.AppendLine();

		if (Missing.Count == 0)
		{
			builder.AppendLine("未发现缺失。");
			builder.AppendLine();
		}
		else
		{
			builder.AppendLine("## 缺失的路径");
			builder.AppendLine();
			builder.AppendLine("这些路径在 PVF 里被引用，但在当前 ImagePacks2 里找不到对应图片。");
			builder.AppendLine("游戏里通常表现为**白框、缺图或图标空白**。");
			builder.AppendLine();
			builder.AppendLine("| # | PVF 中的写法 | 比对时使用的路径 |");
			builder.AppendLine("| --- | --- | --- |");
			for (int i = 0; i < Missing.Count; i++)
			{
				MissingResourceEntry entry = Missing[i];
				builder.AppendLine("| " + (i + 1) + " | `" + entry.RawPath + "` | `" + entry.NormalizedPath + "` |");
			}
			builder.AppendLine();
		}

		builder.AppendLine("## 判读说明");
		builder.AppendLine();
		builder.AppendLine("- 路径识别是启发式的：以 `sprite/`、`image/`、`map/`、`sound/` 开头，或以 `.img`、`.ani`、`.ogg` 结尾的值会被当作资源引用。");
		builder.AppendLine("- 只覆盖直接写在节值里的字符串；通过字符串指针间接引用的路径暂未纳入。");
		builder.AppendLine("- 归一时会补 `sprite/` 前缀：PVF 里常写成相对路径，而 ImagePacks2 里是完整路径。");
		builder.AppendLine("- 若某条路径在游戏里实际正常显示，说明它命中了本工具未覆盖的引用形式，可以忽略。");
		return builder.ToString();
	}

	public string BuildJson(string pvfPath, string imagePacks2Path)
	{
		List<object> items = new List<object>(Missing.Count);
		for (int i = 0; i < Missing.Count; i++)
		{
			items.Add(new
			{
				rawPath = Missing[i].RawPath,
				normalizedPath = Missing[i].NormalizedPath
			});
		}
		object payload = new
		{
			schemaVersion = 1,
			generatedAt = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
			pvf = pvfPath,
			imagePacks2 = imagePacks2Path,
			referenceCount = ReferenceCount,
			checkedCount = CheckedCount,
			missingCount = Missing.Count,
			missing = items
		};
		return JsonConvert.SerializeObject(payload, Formatting.Indented);
	}
}

/// <summary>一条在 ImagePacks2 里找不到的资源路径。</summary>
public sealed class MissingResourceEntry
{
	/// <summary>PVF 里的原始写法。</summary>
	public string RawPath { get; init; }

	/// <summary>归一化后实际用于比对的路径。</summary>
	public string NormalizedPath { get; init; }
}
