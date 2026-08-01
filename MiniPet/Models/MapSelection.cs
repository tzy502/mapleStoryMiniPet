namespace MiniPet.Models;

/// <summary>地图选择结果（crop 为地图世界坐标；全零 = 整图，由 MapService.CropFull 归一化）。见 ADR-0004。</summary>
public record MapSelection(string Id, string Name, int CropX, int CropY, int CropW, int CropH);

/// <summary>地图列表条目。</summary>
public record MapEntry(string Id, string Name)
{
    public string Display => $"  {Name}  [{Id}]";
}
