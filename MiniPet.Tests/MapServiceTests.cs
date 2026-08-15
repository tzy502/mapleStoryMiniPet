using Xunit;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MiniPet.Models;
using MiniPet.Services;

namespace MiniPet.Tests;

public class MapServiceTests
{
    // --- embedded minimal models ---
    public class TestMapTile
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; } = 32;
        public int Height { get; set; } = 32;
    }

    public class TestMapObj
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Z { get; set; }
        public bool FlipX { get; set; }
        public int Width { get; set; } = 16;
        public int Height { get; set; } = 16;
    }

    public class TestMapLayer
    {
        public List<TestMapTile> Tiles { get; set; } = new();
        public List<TestMapObj> Objs { get; set; } = new();
    }

    public class TestMapBack
    {
        public int Id { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Cx { get; set; }
        public int Cy { get; set; }
        public int Rx { get; set; }
        public int Ry { get; set; }
        public int Alpha { get; set; }
        public bool FlipX { get; set; }
        public bool Front { get; set; }
        public bool Ani { get; set; }
        public int Type { get; set; }
    }

    public class TestMapInfo
    {
        public int Id { get; set; }
        public TestMapLayer[] Layers { get; set; }
        public List<TestMapBack> Backs { get; set; } = new();
        public int MinX { get; set; }
        public int MinY { get; set; }
        public int MaxX { get; set; }
        public int MaxY { get; set; }

        public TestMapInfo()
        {
            Layers = new TestMapLayer[8];
            for (int i = 0; i < 8; i++)
                Layers[i] = new TestMapLayer();
        }
    }

    // --- tests: 正常路径 ---

    [Fact]
    public void MapInfo_LayersCount_Always8()
    {
        var map = new TestMapInfo();
        Assert.Equal(8, map.Layers.Length);

        for (int i = 0; i < 8; i++)
            Assert.NotNull(map.Layers[i]);
    }

    [Fact]
    public void CalculateBounds_WithTiles_ReturnsCorrectBounds()
    {
        var map = new TestMapInfo();
        map.Layers[0].Tiles.Add(new TestMapTile { X = 0, Y = 0, Width = 32, Height = 32 });
        map.Layers[0].Tiles.Add(new TestMapTile { X = 100, Y = 200, Width = 32, Height = 32 });

        CalculateBounds(map);

        Assert.Equal(0, map.MinX);
        Assert.Equal(0, map.MinY);
        Assert.Equal(132, map.MaxX);
        Assert.Equal(232, map.MaxY);
    }

    [Fact]
    public void CalculateParallaxOffset_StandardValues()
    {
        var back = new TestMapBack { Cx = 50, Cy = 50, Rx = 0, Ry = 0 };
        var camera = new Point(100, 200);

        var offset = CalculateParallaxOffset(back, camera);

        // x = 100 * 50 * 100 / 10000 = 50
        Assert.Equal(50, offset.X);
        // y = 200 * 50 * 100 / 10000 = 100
        Assert.Equal(100, offset.Y);
    }

    [Fact]
    public void CalculateParallaxOffset_WithRate()
    {
        var back = new TestMapBack { Cx = 30, Cy = 20, Rx = 50, Ry = -30 };
        var camera = new Point(200, 300);

        var offset = CalculateParallaxOffset(back, camera);

        // x = 200 * 30 * 150 / 10000 = 90
        Assert.Equal(90, offset.X);
        // y = 300 * 20 * 70 / 10000 = 42
        Assert.Equal(42, offset.Y);
    }

    // --- tests: 边界 ---

    [Fact]
    public void CalculateBounds_EmptyMap_KeepsDefaults()
    {
        var map = new TestMapInfo();

        CalculateBounds(map);

        // No tiles, bounds unchanged
        Assert.Equal(0, map.MinX);
        Assert.Equal(0, map.MinY);
        Assert.Equal(0, map.MaxX);
        Assert.Equal(0, map.MaxY);
    }

    [Fact]
    public void CalculateBounds_SingleTile_SetsExactBounds()
    {
        var map = new TestMapInfo();
        map.Layers[3].Tiles.Add(new TestMapTile { X = 64, Y = 128, Width = 32, Height = 32 });

        CalculateBounds(map);

        Assert.Equal(64, map.MinX);
        Assert.Equal(128, map.MinY);
        Assert.Equal(96, map.MaxX);
        Assert.Equal(160, map.MaxY);
    }

    [Fact]
    public void CalculateParallaxOffset_ZeroValues_ReturnsZero()
    {
        var back = new TestMapBack { Cx = 0, Cy = 0 };
        var camera = new Point(500, 500);

        var offset = CalculateParallaxOffset(back, camera);

        Assert.Equal(0, offset.X);
        Assert.Equal(0, offset.Y);
    }

    [Fact]
    public void CalculateParallaxOffset_NegativeCamera()
    {
        var back = new TestMapBack { Cx = 10, Cy = 10 };
        var camera = new Point(-100, -200);

        var offset = CalculateParallaxOffset(back, camera);

        Assert.Equal(-10, offset.X);
        Assert.Equal(-20, offset.Y);
    }

    [Fact]
    public void MapBack_FrontProperty_Preserved()
    {
        var back = new TestMapBack { Front = true };
        Assert.True(back.Front);

        var back2 = new TestMapBack { Front = false };
        Assert.False(back2.Front);
    }

    // --- tests: 异常 ---

    [Fact]
    public void CalculateBounds_NegativeTileCoordinates_HandledCorrectly()
    {
        var map = new TestMapInfo();
        map.Layers[0].Tiles.Add(new TestMapTile { X = -50, Y = -100, Width = 32, Height = 32 });

        CalculateBounds(map);

        Assert.Equal(-50, map.MinX);
        Assert.Equal(-100, map.MinY);
        Assert.Equal(-18, map.MaxX);
        Assert.Equal(-68, map.MaxY);
    }

    [Fact]
    public void CalculateBounds_OverlappingWithObjs()
    {
        var map = new TestMapInfo();
        map.Layers[0].Tiles.Add(new TestMapTile { X = 0, Y = 0, Width = 32, Height = 32 });
        map.Layers[1].Objs.Add(new TestMapObj { X = 200, Y = 300, Width = 64, Height = 64 });
        map.Layers[2].Objs.Add(new TestMapObj { X = -100, Y = -50, Width = 16, Height = 16 });

        CalculateBounds(map);

        Assert.Equal(-100, map.MinX);
        Assert.Equal(-50, map.MinY);
        Assert.Equal(264, map.MaxX);
        Assert.Equal(364, map.MaxY);
    }

    [Fact]
    public void GetMapWzPath_VariousMapIds()
    {
        Assert.Equal("Map/Map/Map1/100000000.img", GetMapWzPath(100000000));
        Assert.Equal("Map/Map/Map2/240010500.img", GetMapWzPath(240010500));
        Assert.Equal("Map/Map/Map0/0.img", GetMapWzPath(0));
    }

    [Fact]
    public void GetMapWzPath_NegativeMapId()
    {
        var path = GetMapWzPath(-1);
        Assert.Equal("Map/Map/Map-/-1.img", path);
    }

    // --- M7：GetGroundY 地面层（foothold l0==0）优先 ---

    [Fact]
    public void GetGroundY_PrefersLayer0_OverPlatform()
    {
        // 同一 x 范围：地面层(l0=0) y=100，平台层(l0=1) y=60（屋顶更高）
        var map = new MapInfo();
        map.Footholds.Add(new MapFoothold { Layer = 1, X1 = 0, Y1 = 60, X2 = 500, Y2 = 60 });
        map.Footholds.Add(new MapFoothold { Layer = 0, X1 = 0, Y1 = 100, X2 = 500, Y2 = 100 });
        // 地面层优先 → 返回地面 y=100（而非屋顶 y=60）
        Assert.Equal(100, MapService.GetGroundY(map, 250));
    }

    [Fact]
    public void GetGroundY_NoLayer0Coverage_ReturnsNull()
    {
        // 地面层只覆盖 [0,100]，查询 x=300 超出 → 返回 null（调用方回退窗口底部），
        // 绝不回退到平台层最高点（那正是「站屋顶」原 bug）。
        var map = new MapInfo();
        map.Footholds.Add(new MapFoothold { Layer = 1, X1 = 0, Y1 = 60, X2 = 500, Y2 = 60 });
        map.Footholds.Add(new MapFoothold { Layer = 0, X1 = 0, Y1 = 100, X2 = 100, Y2 = 100 });
        Assert.Null(MapService.GetGroundY(map, 300));
    }

    [Fact]
    public void GetGroundY_NoLayer0AtAll_FallsBackToHighestPlatform()
    {
        // 地图完全没有 l0==0 地面层 → 退而求其次取最高平台（保证仍能落地）
        var map = new MapInfo();
        map.Footholds.Add(new MapFoothold { Layer = 1, X1 = 0, Y1 = 60, X2 = 500, Y2 = 60 });
        map.Footholds.Add(new MapFoothold { Layer = 2, X1 = 0, Y1 = 120, X2 = 500, Y2 = 120 });
        Assert.Equal(60, MapService.GetGroundY(map, 250));
    }

    [Fact]
    public void GetGroundY_NoFootholds_ReturnsNull()
    {
        Assert.Null(MapService.GetGroundY(new MapInfo(), 100));
        Assert.Null(MapService.GetGroundY(null, 100));
    }

    [Fact]
    public void GetGroundY_SlopedFoothold_Interpolates()
    {
        // 斜地面：从 (0,100) 到 (200,200)，x=100 处 y=150
        var map = new MapInfo();
        map.Footholds.Add(new MapFoothold { Layer = 0, X1 = 0, Y1 = 100, X2 = 200, Y2 = 200 });
        Assert.Equal(150, MapService.GetGroundY(map, 100));
    }

    // --- M10：HasScrollingBacks（自动滚动 back 判定）---

    [Fact]
    public void HasScrollingBacks_True_WhenScrollTileMode()
    {
        // type 4 = Horizontal|ScrollHorizontal；type 5 = Vertical|ScrollVertical
        var map = new MapInfo();
        map.Backs.Add(new MapBack { Type = 4, Alpha = 255 });
        Assert.True(MapService.HasScrollingBacks(map));
        map.Backs.Clear();
        map.Backs.Add(new MapBack { Type = 5, Alpha = 255 });
        Assert.True(MapService.HasScrollingBacks(map));
    }

    [Fact]
    public void HasScrollingBacks_False_WhenNoScrollOrInvisible()
    {
        var map = new MapInfo();
        Assert.False(MapService.HasScrollingBacks(map));
        map.Backs.Add(new MapBack { Type = 0, Alpha = 255 });   // 无滚动
        Assert.False(MapService.HasScrollingBacks(map));
        map.Backs.Add(new MapBack { Type = 4, Alpha = 0 });     // 滚动但 a=0 不可见（M13）
        Assert.False(MapService.HasScrollingBacks(map));
    }

    // --- embedded minimal logic replicas ---

    private static void CalculateBounds(TestMapInfo map)
    {
        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;

        foreach (var layer in map.Layers)
        {
            foreach (var tile in layer.Tiles)
            {
                minX = Math.Min(minX, tile.X);
                minY = Math.Min(minY, tile.Y);
                maxX = Math.Max(maxX, tile.X + tile.Width);
                maxY = Math.Max(maxY, tile.Y + tile.Height);
            }
            foreach (var obj in layer.Objs)
            {
                minX = Math.Min(minX, obj.X);
                minY = Math.Min(minY, obj.Y);
                maxX = Math.Max(maxX, obj.X + obj.Width);
                maxY = Math.Max(maxY, obj.Y + obj.Height);
            }
        }

        if (minX < int.MaxValue)
        {
            map.MinX = minX; map.MinY = minY;
            map.MaxX = maxX; map.MaxY = maxY;
        }
    }

    private static Point CalculateParallaxOffset(TestMapBack back, Point cameraPos)
    {
        return new Point(
            cameraPos.X * back.Cx * (100 + back.Rx) / 10000,
            cameraPos.Y * back.Cy * (100 + back.Ry) / 10000
        );
    }

    private static string GetMapWzPath(int mapId)
    {
        var prefix = mapId.ToString().Length > 0 ? mapId.ToString()[0] : '0';
        return $"Map/Map/Map{prefix}/{mapId}.img";
    }
}
