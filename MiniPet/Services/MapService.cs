using System;
using System.Collections.Generic;
using System.Linq;
using MiniPet.Models;
using SkiaSharp;

namespace MiniPet.Services;

public class MapService
{
    private readonly WzService _wz;
    private readonly CacheManager _cache;

    public MapService(WzService wz, CacheManager cache)
    {
        _wz = wz;
        _cache = cache;
    }

    public MapInfo? LoadMap(string mapId)
    {
        var mi = new MapInfo { Id = int.TryParse(mapId, out var id) ? id : 0 };
        
        // Load layers 0-7
        for (int layerIdx = 0; layerIdx < 8; layerIdx++)
        {
            var layer = new MapLayer();
            var layerPath = GetMapWzPath(mapId) + $"/{layerIdx}";
            
            // Load tiles
            var tilePath = layerPath + "/tile";
            var tileData = _wz.FindNodeByPath(tilePath);
            if (tileData != null)
            {
                // Parse tile nodes
                layer.Tiles = ParseTiles(layerPath, mapId);
            }
            
            // Load objs  
            var objPath = layerPath + "/obj";
            var objData = _wz.FindNodeByPath(objPath);
            if (objData != null)
            {
                layer.Objs = ParseObjs(layerPath, mapId);
            }
            
            mi.Layers[layerIdx] = layer;
        }
        
        // Load backs
        var backPath = GetMapWzPath(mapId) + "/back";
        var backNode = _wz.FindNodeByPath(backPath);
        if (backNode != null)
        {
            mi.Backs = ParseBacks(backPath, mapId);
        }
        
        // Load bounds
        var infoPath = GetMapWzPath(mapId) + "/info";
        var infoNode = _wz.FindNodeByPath(infoPath);
        if (infoNode != null)
        {
            // bounds are usually VRLeft/VRRight/VRBottom/VTTop or calculated from tiles
            CalculateBounds(mi);
        }
        
        return mi;
    }

    private void CalculateBounds(MapInfo mi)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        
        foreach (var layer in mi.Layers)
        {
            foreach (var tile in layer.Tiles)
            {
                if (tile.Resource != null)
                {
                    minX = Math.Min(minX, tile.X);
                    minY = Math.Min(minY, tile.Y);
                    maxX = Math.Max(maxX, tile.X + tile.Resource.Width);
                    maxY = Math.Max(maxY, tile.Y + tile.Resource.Height);
                }
            }
            foreach (var obj in layer.Objs)
            {
                if (obj.Resource?.Frames?.FirstOrDefault() is Frame f)
                {
                    minX = Math.Min(minX, obj.X);
                    minY = Math.Min(minY, obj.Y);
                    maxX = Math.Max(maxX, obj.X + f.Width);
                    maxY = Math.Max(maxY, obj.Y + f.Height);
                }
            }
        }
        
        if (minX < int.MaxValue)
        {
            mi.MinX = minX; mi.MinY = minY;
            mi.MaxX = maxX; mi.MaxY = maxY;
        }
    }

    private List<MapTile> ParseTiles(string layerPath, string mapId)
    {
        var tiles = new List<MapTile>();
        for (int i = 0; ; i++)
        {
            var tilePath = $"{layerPath}/tile/{i}";
            var node = _wz.FindNodeByPath(tilePath);
            if (node == null) break;
            
            var x = _wz.GetIntProperty(tilePath + "/x");
            var y = _wz.GetIntProperty(tilePath + "/y");
            var u = _wz.GetStringProperty(tilePath + "/u") ?? "";
            var no = _wz.GetIntProperty(tilePath + "/no");
            
            tiles.Add(new MapTile { Id = i, X = x, Y = y });
        }
        return tiles;
    }

    private List<MapObj> ParseObjs(string layerPath, string mapId)
    {
        var objs = new List<MapObj>();
        for (int i = 0; ; i++)
        {
            var objPath = $"{layerPath}/obj/{i}";
            var node = _wz.FindNodeByPath(objPath);
            if (node == null) break;
            
            var x = _wz.GetIntProperty(objPath + "/x");
            var y = _wz.GetIntProperty(objPath + "/y");
            var z = _wz.GetIntProperty(objPath + "/z");
            var f = _wz.GetIntProperty(objPath + "/f");
            
            objs.Add(new MapObj { Id = i, X = x, Y = y, Z = z, FlipX = f != 0 });
        }
        return objs;
    }

    private List<MapBack> ParseBacks(string backPath, string mapId)
    {
        var backs = new List<MapBack>();
        for (int i = 0; ; i++)
        {
            var bp = $"{backPath}/{i}";
            var node = _wz.FindNodeByPath(bp);
            if (node == null) break;
            
            var bS = _wz.GetStringProperty(bp + "/bS") ?? "";
            var no = _wz.GetIntProperty(bp + "/no");
            var cx = _wz.GetIntProperty(bp + "/cx");
            var cy = _wz.GetIntProperty(bp + "/cy");
            var rx = _wz.GetIntProperty(bp + "/rx");
            var ry = _wz.GetIntProperty(bp + "/ry");
            var alpha = _wz.GetIntProperty(bp + "/a");
            var front = _wz.GetIntProperty(bp + "/front");
            var ani = _wz.GetIntProperty(bp + "/ani");
            var type = _wz.GetIntProperty(bp + "/type");
            var x = _wz.GetIntProperty(bp + "/x");
            var y = _wz.GetIntProperty(bp + "/y");
            var flip = _wz.GetIntProperty(bp + "/f");
            
            backs.Add(new MapBack
            {
                Id = i, X = x, Y = y, Cx = cx, Cy = cy, Rx = rx, Ry = ry,
                Alpha = alpha, Front = front != 0, FlipX = flip != 0,
                Ani = ani != 0, Type = type
            });
        }
        return backs;
    }

    private string GetMapWzPath(string mapId)
    {
        var firstDigit = mapId.Length > 0 ? mapId[0] : '0';
        return $"Map/Map/Map{firstDigit}/{mapId}.img";
    }

    public byte[]? CompositeMapFrame(MapInfo mapInfo, int viewX, int viewY, int w, int h)
    {
        if (mapInfo == null) return null;

        if (w <= 0 || h <= 0) return null;

        using var result = new SKBitmap(w, h);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        // Draw backs (background layers with parallax)
        foreach (var back in mapInfo.Backs.OrderBy(b => b.Front ? 1 : 0))
        {
            // Simple rendering - draw at position with offset
            var offset = CalculateParallaxOffset(back, viewX, viewY);
            // Back layers are drawn relative to viewport
        }

        // Draw layers (0-7)
        for (int i = 0; i < 8; i++)
        {
            var layer = mapInfo.Layers[i];

            // Draw tiles first (bottom)
            foreach (var tile in layer.Tiles)
            {
                var screenX = tile.X - viewX;
                var screenY = tile.Y - viewY;
                if (screenX >= -100 && screenX < w && screenY >= -100 && screenY < h)
                {
                    // Draw tile placeholder
                    using var paint = new SKPaint { Color = SKColors.Green };
                    canvas.DrawRect(screenX, screenY, 32, 32, paint);
                }
            }

            // Draw objs on top
            foreach (var obj in layer.Objs.OrderBy(o => o.Z))
            {
                var screenX = obj.X - viewX;
                var screenY = obj.Y - viewY;
                if (screenX >= -100 && screenX < w && screenY >= -100 && screenY < h)
                {
                    using var paint = new SKPaint { Color = SKColors.Brown };
                    canvas.DrawRect(screenX, screenY, 16, 16, paint);
                }
            }
        }

        return SKImage.FromBitmap(result).Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    public (int x, int y) CalculateParallaxOffset(MapBack back, int cameraX, int cameraY)
    {
        return (
            cameraX * back.Cx * (100 + back.Rx) / 10000,
            cameraY * back.Cy * (100 + back.Ry) / 10000
        );
    }

    public List<(string id, string name)> GetMapList()
    {
        return new List<(string, string)>
        {
            ("100000000", "射手村"),
            ("101000000", "魔法密林"),
            ("102000000", "废弃都市"),
            ("103000000", "天空之城"),
            ("240010500", "神木村")
        };
    }
}
