using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.Generic;
using System.Linq;

namespace MiniPet.Views;

public partial class MaterialBrowserWindow : Window
{
    private List<MaterialItem> _items = new();
    private int _page = 0;
    private const int PageSize = 10;
    private MaterialItem? _selected;

    public delegate void MaterialSelectedHandler(string id, string name);
    public event MaterialSelectedHandler? OnMaterialSelected;

    public MaterialBrowserWindow()
    {
        InitializeComponent();
        LoadMockData();
        RenderPage();
    }

    private void LoadMockData()
    {
        _items = new List<MaterialItem>
        {
            new("8880150", "路西德", "mob"),
            new("8860000", "品克缤", "mob"),
            new("9300573", "狐狸", "mob"),
            new("8600001", "龙", "mob"),
            new("2230000", "机器人", "mob"),
            new("11000", "达夫", "npc"),
            new("12000", "希拉", "npc"),
            new("1002554", "暗夜帽", "equip"),
            new("1042031", "暗夜上衣", "equip"),
            new("1062015", "暗夜裤子", "equip"),
            new("30000", "基本发型", "hair"),
            new("20000", "基本脸型", "face"),
        };
    }

    private void RenderPage()
    {
        var page = _items.Skip(_page * PageSize).Take(PageSize).ToList();
        MaterialList.ItemsSource = page;
        PageInfo.Text = $"第 {_page + 1}/{TotalPages} 页";
        PrevBtn.IsEnabled = _page > 0;
        NextBtn.IsEnabled = _page < TotalPages - 1;
    }

    private int TotalPages => (_items.Count + PageSize - 1) / PageSize;

    private void OnSearch(object? sender, RoutedEventArgs e)
    {
        var q = (SearchBox.Text ?? "").ToLower();
        var filtered = string.IsNullOrEmpty(q) ? _items : _items.Where(i => i.Name.Contains(q) || i.Id.Contains(q)).ToList();
        var page = filtered.Take(PageSize).ToList();
        MaterialList.ItemsSource = page;
    }

    private void OnItemSelected(object? sender, SelectionChangedEventArgs e)
    {
        _selected = MaterialList.SelectedItem as MaterialItem;
        if (_selected == null) return;
        DetailName.Text = _selected.Name;
        DetailId.Text = _selected.Id;
        DetailType.Text = _selected.Type;
    }

    private void OnCategorySelected(object? sender, SelectionChangedEventArgs e)
    {
        if (CategoryTree.SelectedItem is not TreeViewItem tvi || tvi.Tag == null) return;
        var type = tvi.Tag.ToString()!;
        var filtered = _items.Where(i => i.Type == type).ToList();
        MaterialList.ItemsSource = filtered;
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (_selected == null) return;
        OnMaterialSelected?.Invoke(_selected.Id, _selected.Name);
        Close();
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnPrevPage(object? sender, RoutedEventArgs e) { if (_page > 0) { _page--; RenderPage(); } }
    private void OnNextPage(object? sender, RoutedEventArgs e) { if (_page < TotalPages - 1) { _page++; RenderPage(); } }
}

public record MaterialItem(string Id, string Name, string Type);
