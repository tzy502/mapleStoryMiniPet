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
    }

    /// <summary>
    /// 聊天气泡数据
    /// </summary>
    public class BalloonData
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public BalloonTiles Tiles { get; set; } = new BalloonTiles();
        public int Clr { get; set; }
        public string DefaultText { get; set; } = string.Empty;
    }
}