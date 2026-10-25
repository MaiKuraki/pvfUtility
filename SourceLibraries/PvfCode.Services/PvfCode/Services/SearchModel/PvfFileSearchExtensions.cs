using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic;
using Microsoft.VisualBasic.CompilerServices;
using PvfCode;
using PvfCode.Models.Pvf;

namespace PvfCode.Services.SearchModel;

internal static class PvfFileSearchExtensions
{
	public static bool ContainsStringTableReference(this PvfFile file, HashSet<int> stringTableIndexes)
	{
		if (!file.IsScriptFile)
		{
			return false;
		}
		for (int i = 2; i < file.DataLen - 4; i += 5)
		{
			if ((file.Data[i] == 5 || file.Data[i] == 7 || file.Data[i] == 10) && stringTableIndexes.Contains(BitConverter.ToInt32(file.Data, i + 1)))
			{
				return true;
			}
			if (i > 4 && file.Data[i] == 10 && file.Data[i - 5] == 9)
			{
				int compositeIndex = file.Data[i - 4] * 16777216 + BitConverter.ToInt32(file.Data, i + 1);
				if (stringTableIndexes.Contains(compositeIndex))
				{
					return true;
				}
			}
		}
		return false;
	}

	public static bool ContainsIntegerValue(this PvfFile file, int value)
	{
		if (!file.IsScriptFile)
		{
			return false;
		}
		for (int i = 2; i < file.DataLen - 4; i += 5)
		{
			if ((file.Data[i] == 2 || file.Data[i] == 4) && BitConverter.ToInt32(file.Data, i + 1) == value)
			{
				return true;
			}
		}
		return false;
	}

	public static bool ContainsBinarySequence(this PvfFile file, byte[] sequence)
	{
		if (sequence == null || sequence.Length < 5 || !file.IsScriptFile)
		{
			return false;
		}
		int sequenceLength = sequence.Length;
		byte[] data = file.Data;
		int dataLen = file.DataLen;
		if (data == null || dataLen < sequenceLength)
		{
			return false;
		}
		byte first = sequence[0];
		int head = BinaryPrimitives.ReadInt32LittleEndian(sequence.AsSpan(1, 4));
		// 起点上限：保证 i + sequenceLength 不越界；同时保留 i+5 的 token 边界检查
		int limit = dataLen - sequenceLength;
		for (int i = 2; i <= limit && i + 5 <= dataLen; i += 5)
		{
			if (data[i] != first)
			{
				continue;
			}
			if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 1, 4)) != head)
			{
				continue;
			}
			if (sequenceLength == 5 || data.AsSpan(i, sequenceLength).SequenceEqual(sequence))
			{
				return true;
			}
		}
		return false;
	}

	public static bool MatchesItemName(this PvfFile file, PvfGroup group, bool startsWith, string searchText, bool useWildcard, Regex regex)
	{
		string itemName = group.GetItemName(file);
		if (itemName == null)
		{
			return false;
		}
		if (regex != null && regex.IsMatch(itemName))
		{
			return true;
		}
		if (startsWith)
		{
			if (itemName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) == 0)
			{
				return true;
			}
			return false;
		}
		if (useWildcard)
		{
			if (LikeOperator.LikeString(itemName, searchText, CompareMethod.Binary))
			{
				return true;
			}
			return itemName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
		}
		return searchText == itemName;
	}

	public static bool HasEquivalentContent(this PvfFile sourceFile, PvfGroup sourceGroup, PvfFile targetFile, PvfPack targetPack)
	{
		if (sourceFile.IsScriptFile != targetFile?.IsScriptFile)
		{
			return false;
		}
		if (!sourceFile.IsScriptFile && !targetFile.IsScriptFile)
		{
			return DataHelper.BytesEquals(targetFile.Data, sourceFile.Data);
		}
		if (targetFile.DataLen != sourceFile.DataLen)
		{
			return false;
		}
		for (int i = 2; i < sourceFile.DataLen - 4; i += 5)
		{
			byte itemType = sourceFile.Data[i];
			if ((itemType == 5 || itemType == 6 || itemType == 7 || itemType == 8 || itemType == 10) && sourceGroup.Strtable.GetStringItem(BitConverter.ToInt32(sourceFile.Data, i + 1)) != targetPack.Strtable.GetStringItem(BitConverter.ToInt32(targetFile.Data, i + 1)))
			{
				return false;
			}
			if ((itemType == 9 || itemType == 4 || itemType == 2) && BitConverter.ToInt32(sourceFile.Data, i + 1) != BitConverter.ToInt32(targetFile.Data, i + 1))
			{
				return false;
			}
		}
		return true;
	}
}
