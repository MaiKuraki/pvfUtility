using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PvfCode.LoggerBase;
using PvfCode.Models.Pvf;
using PvfCode.Models.Pvf.Enums;
using PvfCode.Models.Pvf.Enums.Stackable;
using PvfCode.Models.Pvf.ScriptEnums;

namespace PvfCode;

/// <summary>
/// PvfFile 的脚本节查询部分：按节 ID 定位、读取节内数值/字符串/数组。
///
/// 拆出原因：PvfFile 原本 2400 行，元数据、图标解析、属性读取、节查询混在一个类里。
/// 先做物理拆分（partial），保持方法签名与调用点完全不变，便于后续按需做逻辑抽取。
/// 节偏移由 PvfFile.GetSectionOffsets 统一缓存，所以这里所有查询都是"扫节表"而不是"扫全部 token"。
/// </summary>
public partial class PvfFile
{
	public bool FindSectionIndex(int sectionId, out int indexOut)
	{
		indexOut = -1;
		if (Data == null || DataLen < 7)
		{
			return false;
		}
		int[] offsets = GetSectionOffsets();
		for (int k = 0; k < offsets.Length; k++)
		{
			int offset = offsets[k];
			if (BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(offset + 1, 4)) == sectionId)
			{
				indexOut = offset;
				return true;
			}
		}
		return false;
	}

	public bool GetSectionValue(string sectionName, PvfPack pvf, out string? val)
	{
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		val = null;
		if (stringTableId == -1)
		{
			return false;
		}
		if (!FindSectionIndex(stringTableId, out var indexOut))
		{
			return false;
		}
		if (indexOut + 5 < DataLen)
		{
			val = GetNumericValue(indexOut);
			return true;
		}
		return false;
	}

	public bool GetSectionIntValue(string sectionName, PvfPack pvf, out int val)
	{
		val = -1;
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		if (stringTableId == -1)
		{
			return false;
		}
		if (!FindSectionIndex(stringTableId, out var indexOut))
		{
			return false;
		}
		if (indexOut + 5 < DataLen && Data[indexOut + 5] == 2)
		{
			val = BitConverter.ToInt32(Data, indexOut + 6);
			return true;
		}
		return false;
	}

	public bool GetSectionStringValue(string sectionName, PvfPack pvf, out string val)
	{
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		val = null;
		if (stringTableId == -1)
		{
			return false;
		}
		if (!FindSectionIndex(stringTableId, out var indexOut))
		{
			return false;
		}
		if (indexOut + 5 < DataLen)
		{
			val = pvf.Strtable.GetStringItem(BitConverter.ToInt32(Data, indexOut + 6));
			return true;
		}
		return false;
	}

	public bool GetSectionIntValue2(string sectionName, PvfPack pvf, out KeyValuePair<string?, string?>? val)
	{
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		val = null;
		if (stringTableId == -1)
		{
			return false;
		}
		if (!FindSectionIndex(stringTableId, out var indexOut))
		{
			return false;
		}
		if (indexOut + 10 < DataLen)
		{
			val = new KeyValuePair<string, string>(GetNumericValue(indexOut), GetNumericValue(indexOut + 5));
			return true;
		}
		return false;
	}

	public bool GetSectionIntArray(string sectionName, PvfPack pvf, out List<int> list)
	{
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		list = null;
		if (stringTableId == -1)
		{
			return false;
		}
		if (!FindSectionIndex(stringTableId, out var sectionIndex))
		{
			return false;
		}
		byte[] data = Data;
		List<int> values = new List<int>();
		list = values;
		// 修正：原实现误用 stringTableId 作为字节偏移，应从找到的节头位置继续扫描
		for (int i = sectionIndex + 5; i + 5 <= DataLen; i += 5)
		{
			byte type = data[i];
			if (type == 5)
			{
				break;
			}
			if (type == 2)
			{
				values.Add(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i + 1, 4)));
				continue;
			}
			return false;
		}
		return true;
	}

	public bool GetNameText(PvfPack pvf, string sectionName, out string? name)
	{
		name = null;
		if (Data == null || DataLen < 7)
		{
			return false;
		}
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		if (stringTableId == -1)
		{
			return false;
		}
		ReadOnlySpan<int> offsets = SectionOffsets;
		int limit = DataLen - 9;
		for (int k = 0; k < offsets.Length; k++)
		{
			int offset = offsets[k];
			if (offset >= limit)
			{
				break;
			}
			if (BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(offset + 1, 4)) != stringTableId)
			{
				continue;
			}
			if (offset < DataLen - 14 && Data[offset + 5] == 9 && Data[offset + 10] == 10)
			{
				name = pvf.Strview.GetStrText(Data[offset + 6], pvf.Strtable.GetStringItem(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(offset + 11, 4))), autoConvertStr: true);
				return true;
			}
			if (Data[offset + 5] == 7)
			{
				name = pvf.Strtable.GetStringItem(BinaryPrimitives.ReadInt32LittleEndian(Data.AsSpan(offset + 6, 4)), autoConvertStr: true);
				return true;
			}
		}
		return false;
	}

	private string? GetNumericValue(int index)
	{
		return (ScriptType)Data[index + 5] switch
		{
			ScriptType.Int => BitConverter.ToInt32(Data, index + 6).ToString(),
			ScriptType.Float => DataHelper.FormatFloat(BitConverter.ToSingle(Data, index + 6)),
			_ => null, 
		};
	}

	public bool GetSectionIntArray(PvfPack pvf, string sectionName, out List<int> items)
	{
		items = new List<int>();
		if (Data == null || DataLen < 7)
		{
			return false;
		}
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		if (stringTableId == -1)
		{
			return false;
		}
		for (int i = 2; i < DataLen - 4; i += 5)
		{
			if (Data[i] != 5)
			{
				continue;
			}
			int startIndex = i + 1;
			if (BitConverter.ToInt32(Data, startIndex) != stringTableId)
			{
				continue;
			}
			for (int j = i + 5; j < DataLen - 4; j += 5)
			{
				if (Data[j] == 5)
				{
					return items.Count > 0;
				}
				if (j < DataLen && Data[j] == 2)
				{
					items.Add(BitConverter.ToInt32(Data, j + 1));
				}
			}
			return items.Count > 0;
		}
		return items.Count > 0;
	}

	private bool GetSectionStringArray(PvfPack pvf, string sectionName, out List<string> items)
	{
		items = new List<string>();
		if (Data == null || DataLen < 7)
		{
			return false;
		}
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		if (stringTableId == -1)
		{
			return false;
		}
		for (int i = 2; i < DataLen - 4; i += 5)
		{
			if (Data[i] != 5)
			{
				continue;
			}
			int startIndex = i + 1;
			if (BitConverter.ToInt32(Data, startIndex) != stringTableId)
			{
				continue;
			}
			for (int j = i + 5; j < DataLen - 4; j += 5)
			{
				if (Data[j] == 5)
				{
					return items.Count > 0;
				}
				if (j < DataLen && Data[j] == 7)
				{
					items.Add(pvf.Strtable.GetStringItem(BitConverter.ToInt32(Data, j + 1)));
				}
			}
			return items.Count > 0;
		}
		return items.Count > 0;
	}

	public bool GetSectionTypeIsStrArray(PvfPack pvf, string sectionName, out List<string> items)
	{
		items = new List<string>();
		if (Data == null || DataLen < 7)
		{
			return false;
		}
		int stringTableId = pvf.Strtable.GetStringTableId(sectionName);
		if (stringTableId == -1)
		{
			return false;
		}
		for (int i = 2; i < DataLen - 4; i += 5)
		{
			if (Data[i] != 5)
			{
				continue;
			}
			int startIndex = i + 1;
			if (BitConverter.ToInt32(Data, startIndex) != stringTableId)
			{
				continue;
			}
			for (int j = i + 5; j < DataLen - 4; j += 5)
			{
				if (Data[j] == 5)
				{
					return items.Count > 0;
				}
				byte b = Data[j];
				if (j < DataLen && b == 7)
				{
					items.Add(pvf.Strtable.GetStringItem(BitConverter.ToInt32(Data, j + 1), autoConvertStr: true));
				}
				else if (j < DataLen && b == 10)
				{
					int strid = BitConverter.ToInt32(Data, j - 4);
					string stringItem = pvf.Strtable.GetStringItem(BitConverter.ToInt32(Data, j + 1));
					items.Add(pvf.Strview.GetStrText(strid, stringItem, autoConvertStr: true).Replace("\\n", "\r\n"));
				}
			}
			return items.Count > 0;
		}
		return items.Count > 0;
	}

	public bool GetStackableType(PvfPack pvf, out StackableType? type)
	{
		type = null;
		if (Data == null || DataLen < 7)
		{
			return false;
		}
		int stringTableId = pvf.Strtable.GetStringTableId("[stackable type]");
		if (stringTableId == -1)
		{
			return false;
		}
		for (int i = 2; i < DataLen - 4; i += 5)
		{
			if (Data[i] == 5 && BitConverter.ToInt32(Data, i + 1) == stringTableId && i + 10 < DataLen && Data[i + 5] == 7 && Data[i + 10] == 2)
			{
				string stringItem = pvf.Strtable.GetStringItem(BitConverter.ToInt32(Data, i + 6));
				int num = BitConverter.ToInt32(Data, i + 11);
				if (PvfFileHelper.ScriptContentToStackableType(stringItem, num, out type, out string _))
				{
					return true;
				}
				Ilogger ilogger = AppSetting.Instance.GetIlogger();
				if (ilogger != null)
				{
					ilogger.Error($"未能识别的道具类型 {stringItem}\t{num}");
				}
				return false;
			}
		}
		return false;
	}
}
