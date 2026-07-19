using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using MiniPet.Models;

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

    public Bitmap? CompositeFrame(string action, int frameIndex)
    {
        if (_current == null) return null;
        
        var layers = new List<(Bitmap img, int ox, int oy, int z)>();
        
        for (int zi = 0; zi < ZOrder.Length; zi++)
        {
            var slot = ZOrder[zi];
            var (category, itemId) = GetPartForSlot(slot, _current);
            if (category == null || itemId == null) continue;
            
            var wzPath = $"Character/{category}/{itemId.PadLeft(8, '0')}.img/{action}/{frameIndex}/{slot}";
            var pngData = _wz.ExtractPng(wzPath);
            if (pngData == null) continue;
            
            var (ox, oy) = _wz.GetOrigin(wzPath);
            using var ms = new System.IO.MemoryStream(pngData);
            var bmp = new Bitmap(ms);
            layers.Add((bmp, ox, oy, zi));
        }
        
        if (layers.Count == 0) return null;
        
        // Calculate bounds
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
        
        var result = new Bitmap(w, h);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.Transparent);
        
        foreach (var (img, ox, oy, _) in layers.OrderBy(l => l.z))
        {
            g.DrawImage(img, -minX - ox, -minY - oy);
        }
        
        return result;
    }

    private (string? category, string? itemId) GetPartForSlot(string slot, CharacterAppearance a)
    {
        string? capId = a.Cap?.Id.ToString(), capeId = a.Cape?.Id.ToString(), coatId = a.Coat?.Id.ToString();
        string? pantsId = a.Pants?.Id.ToString(), shoesId = a.Shoes?.Id.ToString(), weaponId = a.Weapon?.Id.ToString();
        string? shieldId = a.Shield?.Id.ToString(), gloveId = a.Glove?.Id.ToString(), earId = a.Earring?.Id.ToString();
        string hairId = a.Hair.Id.ToString(), faceId = a.Face.Id.ToString(), skinId = a.Skin.ToString();
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

    public List<string> GetActionList() => new() { "stand", "walk", "jump", "alert", "swingOF", "swingOB", "stabOF", "stabOB" };

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

    public void SavePreset(string name, CharacterAppearance appearance)
    {
        _presets[name] = appearance;
    }

    public CharacterAppearance? LoadPreset(string name)
    {
        return _presets.TryGetValue(name, out var p) ? p : null;
    }

    public Bitmap? CompositeWithMount(MountInfo mount, string action, int frame) => CompositeFrame(action, frame);
    public Bitmap? CompositeWithChair(ChairInfo chair, string action, int frame) => CompositeFrame(action, frame);
}

public class EquippedItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
}
