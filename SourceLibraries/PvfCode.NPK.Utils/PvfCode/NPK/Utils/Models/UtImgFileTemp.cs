using Newtonsoft.Json;

namespace PvfCode.NPK.Utils.Models;

[JsonObject(MemberSerialization.OptOut)]
public class UtImgFileTemp : UtImgFile
{
	[JsonIgnore]
	public int? Offset { get; }

	/// <summary>索引里记录的字节长度，用于判断同名资源在不同 NPK 中是否同一份内容。</summary>
	public int Length { get; }

	public UtImgFileTemp(int offset, int length, int fileNameId)
		: base(fileNameId)
	{
		Offset = offset;
		Length = length;
	}
}
