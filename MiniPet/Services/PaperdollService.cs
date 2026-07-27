using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MiniPet.Models;
using SkiaSharp;

namespace MiniPet.Services;

public class PaperdollService
{
    private readonly WzService _wz;
    private readonly CacheManager _cache;
    private CharacterAppearance? _current;
    private readonly Dictionary<string, CharacterAppearance> _presets = new();

    private static readonly string[] ZOrder = 
    {
        "back", "hairBelowHead", "body", "head", "ear", "face", "tail",
        "capeBack", "arm", "hand", "shoe", "pants", "coat", "glove",
        "gloveOver", "shield", "weapon", "cape", "hairOverHead", "cap"
    };

    public PaperdollService(WzService wz, CacheManager cache)
    {
        _wz = wz;
        _cache = cache;
    }

    public bool LoadAppearance(CharacterAppearance appearance)
    {
        _current = appearance;
        return true;
    }

    public byte[]? CompositeFrame(string action, int frameIndex)
    {
        if (_current == null) return null;

        var layers = new List<(SKBitmap img, int ox, int oy, int z)>();

        for (int zi = 0; zi < ZOrder.Length; zi++)
        {
            var slot = ZOrder[zi];
            var (category, itemId) = GetPartForSlot(slot, _current);
            if (category == null || itemId == null) continue;

            var wzPath = $"Character/{category}/{itemId.PadLeft(8, '0')}.img/{action}/{frameIndex}/{slot}";
            var pngData = _wz.ExtractPng(wzPath);
            if (pngData == null) continue;

            var (ox, oy) = _wz.GetOrigin(wzPath);
            using var skImg = SKImage.FromEncodedData(pngData);
            var bmp = SKBitmap.FromImage(skImg);
            layers.Add((bmp, ox, oy, zi));
        }

        return CompositeLayers(layers);
    }

    private byte[]? CompositeLayers(List<(SKBitmap img, int ox, int oy, int z)> layers)
    {
        if (layers.Count == 0) return null;

        int minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var (img, ox, oy, _) in layers)
        {
            minX = Math.Min(minX, -ox);
            minY = Math.Min(minY, -oy);
            maxX = Math.Max(maxX, -ox + img.Width);
            maxY = Math.Max(maxY, -oy + img.Height);
        }

        int w = maxX - minX, h = maxY - minY;
        if (w <= 0 || h <= 0) return null;

        using var result = new SKBitmap(w, h);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);

        foreach (var (img, ox, oy, _) in layers.OrderBy(l => l.z))
        {
            canvas.DrawBitmap(img, -minX - ox, -minY - oy);
        }

        using var image = SKImage.FromBitmap(result);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private (string? category, string? itemId) GetPartForSlot(string slot, CharacterAppearance a)
    {
        string? capId = a.Cap?.Id.ToString(), capeId = a.Cape?.Id.ToString(), coatId = a.Coat?.Id.ToString();
        string? pantsId = a.Pants?.Id.ToString(), shoesId = a.Shoes?.Id.ToString(), weaponId = a.Weapon?.Id.ToString();
        string? shieldId = a.Shield?.Id.ToString(), gloveId = a.Glove?.Id.ToString(), earId = a.Earring?.Id.ToString();
        string hairId = a.Hair.Id.ToString(), faceId = a.Face.Id.ToString();
        string bodyCode = a.Gender == 0 ? "00002000" : "00012000";
        
        return slot switch
        {
            "back" or "body" or "head" or "arm" or "hand" => ("Body", bodyCode),
            "hairBelowHead" or "hairOverHead" => ("Hair", hairId),
            "face" => ("Face", faceId),
            "cap" => capId != null ? ("Cap", capId) : (null, null),
            "cape" or "capeBack" => capeId != null ? ("Cape", capeId) : (null, null),
            "coat" => coatId != null ? ("Coat", coatId) : (null, null),
            "pants" => pantsId != null ? ("Pants", pantsId) : (null, null),
            "shoe" => shoesId != null ? ("Shoes", shoesId) : (null, null),
            "weapon" => weaponId != null ? ("Weapon", weaponId) : (null, null),
            "shield" => shieldId != null ? ("Shield", shieldId) : (null, null),
            "glove" or "gloveOver" => gloveId != null ? ("Glove", gloveId) : (null, null),
            "ear" => earId != null ? ("Accessory", earId) : (null, null),
            _ => (null, null)
        };
    }

    public List<string> GetActionList() => new() { "stand", "walk", "jump", "alert", "swingOF", "stabOF" };

    public List<EquippedItem> GetEquipList(string category) => new();

    public void SetEquip(string category, string itemId)
    {
        if (_current == null) return;
        var item = new ItemInfo { Id = int.TryParse(itemId, out var id) ? id : 0 };
        switch (category)
        {
            case "Cap": _current.Cap = item; break;
            case "Cape": _current.Cape = item; break;
            case "Coat": _current.Coat = item; break;
            case "Pants": _current.Pants = item; break;
            case "Shoes": _current.Shoes = item; break;
            case "Weapon": _current.Weapon = item; break;
            case "Shield": _current.Shield = item; break;
            case "Glove": _current.Glove = item; break;
        }
    }

    public void RemoveEquip(string category)
    {
        if (_current == null) return;
        switch (category)
        {
            case "Cap": _current.Cap = null; break;
            case "Cape": _current.Cape = null; break;
            case "Coat": _current.Coat = null; break;
            case "Pants": _current.Pants = null; break;
            case "Shoes": _current.Shoes = null; break;
            case "Weapon": _current.Weapon = null; break;
            case "Shield": _current.Shield = null; break;
            case "Glove": _current.Glove = null; break;
        }
    }

    public void SavePreset(string name, CharacterAppearance appearance) { _presets[name] = appearance; }
    public CharacterAppearance? LoadPreset(string name) => _presets.TryGetValue(name, out var p) ? p : null;
    public byte[]? CompositeWithMount(MountInfo mount, string action, int frame) => CompositeFrame(action, frame);
    public byte[]? CompositeWithChair(ChairInfo chair, string action, int frame) => CompositeFrame(action, frame);
}

public class EquippedItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}
