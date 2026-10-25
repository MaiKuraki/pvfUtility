namespace PvfCode.NPK.Utils.Models;

/// <summary>
/// 一张图片在某个 NPK 中的来源信息。
///
/// Length 取自 NPK 索引里记录的字节长度，Count 是图片头里的帧数。
/// 两个包提供同一路径时，用这两个值可以低成本判断"内容是否相同"：
/// 任意一项不同，就说明是两个不同的文件（常见的"小补丁包覆盖大包"）。
/// </summary>
public readonly struct NpkImageSourceInfo
{
	public int NpkId { get; }

	public int Length { get; }

	public int Count { get; }

	public NpkImageSourceInfo(int npkId, int length, int count)
	{
		NpkId = npkId;
		Length = length;
		Count = count;
	}
}
