namespace MiniPet.Models
{
    /// <summary>
    /// 聊天气泡瓦片图
    /// </summary>
    public class BalloonTiles
    {
        public byte[] NW { get; set; } = System.Array.Empty<byte>();
        public byte[] N { get; set; } = System.Array.Empty<byte>();
        public byte[] NE { get; set; } = System.Array.Empty<byte>();
        public byte[] W { get; set; } = System.Array.Empty<byte>();
        public byte[] C { get; set; } = System.Array.Empty<byte>();
        public byte[] E { get; set; } = System.Array.Empty<byte>();
        public byte[] SW { get; set; } = System.Array.Empty<byte>();
        public byte[] S { get; set; } = System.Array.Empty<byte>();
        public byte[] Arrow { get; set; } = System.Array.Empty<byte>();
        public byte[] SE { get; set; } = System.Array.Empty<byte>();
        public byte[] Head { get; set; } = System.Array.Empty<byte>();

        // 各切片 origin（MapleSalon2 拼装参考：origin-aware 定位，源节点 origin，PNG 在 outlink 目标）
        public (int X, int Y) NWOrigin { get; set; }
        public (int X, int Y) NOrigin { get; set; }
        public (int X, int Y) NEOrigin { get; set; }
        public (int X, int Y) WOrigin { get; set; }
        public (int X, int Y) COrigin { get; set; }
        public (int X, int Y) EOrigin { get; set; }
        public (int X, int Y) SWOrigin { get; set; }
        public (int X, int Y) SOrigin { get; set; }
        public (int X, int Y) SEOrigin { get; set; }
        public (int X, int Y) ArrowOrigin { get; set; }
        public (int X, int Y) HeadOrigin { get; set; }
    }

    /// <summary>
    /// 聊天气泡数据
    /// </summary>
    public class BalloonData
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public BalloonTiles Tiles { get; set; } = new BalloonTiles();
        public int Clr { get; set; }
        public string DefaultText { get; set; } = string.Empty;
    }

    /// <summary>把磁盘读出的 origin 字典应用到 BalloonTiles（缓存持久化用）。</summary>
    public static class BalloonTilesExtensions
    {
        public static BalloonTiles WithOrigins(this BalloonTiles tiles, System.Collections.Generic.Dictionary<string, (int X, int Y)>? origins)
        {
            if (origins == null) return tiles;
            if (origins.TryGetValue("nw", out var v)) tiles.NWOrigin = v;
            if (origins.TryGetValue("n", out v)) tiles.NOrigin = v;
            if (origins.TryGetValue("ne", out v)) tiles.NEOrigin = v;
            if (origins.TryGetValue("w", out v)) tiles.WOrigin = v;
            if (origins.TryGetValue("c", out v)) tiles.COrigin = v;
            if (origins.TryGetValue("e", out v)) tiles.EOrigin = v;
            if (origins.TryGetValue("sw", out v)) tiles.SWOrigin = v;
            if (origins.TryGetValue("s", out v)) tiles.SOrigin = v;
            if (origins.TryGetValue("se", out v)) tiles.SEOrigin = v;
            if (origins.TryGetValue("arrow", out v)) tiles.ArrowOrigin = v;
            if (origins.TryGetValue("head", out v)) tiles.HeadOrigin = v;
            return tiles;
        }
    }
}