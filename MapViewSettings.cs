using System.Text.Json;

namespace TarkovMapLite;

/// <summary>用户的显示偏好。只保存界面选择，不恢复上局的玩家位置或任务进度。</summary>
internal sealed class MapViewSettings
{
    public bool OnlyActive { get; set; } = true;
    public bool AnyRegion { get; set; }
    public bool ShowExtracts { get; set; } = true;
    public bool ShowLabels { get; set; }
    public bool ShowBtr { get; set; }
    public bool ShowLoot { get; set; } = true;
    public bool ShowContainers { get; set; } = true;
    public bool ShowSpawns { get; set; }
    public bool ShowHazards { get; set; }
    public bool ShowStatics { get; set; }
    public bool ShowDoors { get; set; }
    public bool ShowSwitches { get; set; }
    public int[] HiddenLootKinds { get; set; } = [];
    public string[] HiddenContainerKinds { get; set; } = [];
    public string MapKey { get; set; } = "";
    public string Layer { get; set; } = "";

    public static MapViewSettings Load()
    {
        try
        {
            var json = PlayerLocator.GetSetting("view");
            return string.IsNullOrWhiteSpace(json) ? new() :
                JsonSerializer.Deserialize<MapViewSettings>(json) ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save() => PlayerLocator.SetSetting("view", JsonSerializer.Serialize(this));

    public void ApplyLayers(MapCanvas canvas)
    {
        canvas.OnlyActive = OnlyActive;
        canvas.ShowExtracts = ShowExtracts;
        canvas.ShowLabels = ShowLabels;
        canvas.ShowBtr = ShowBtr;
        canvas.ShowLoot = ShowLoot;
        canvas.ShowContainers = ShowContainers;
        canvas.ShowSpawns = ShowSpawns;
        canvas.ShowHazards = ShowHazards;
        canvas.ShowStatics = ShowStatics;
        canvas.ShowDoors = ShowDoors;
        canvas.ShowSwitches = ShowSwitches;
        canvas.HiddenLootKinds.Clear();
        canvas.HiddenLootKinds.UnionWith(HiddenLootKinds ?? []);
        canvas.HiddenContainerKinds.Clear();
        canvas.HiddenContainerKinds.UnionWith((HiddenContainerKinds ?? []).Where(s => !string.IsNullOrEmpty(s)));
        // 整组关闭时，新数据新增的分类也保持关闭。
        if (!ShowLoot) canvas.HiddenLootKinds.UnionWith(LootKinds.All.Select(kind => kind.Bit));
        if (!ShowContainers) canvas.HiddenContainerKinds.UnionWith(MapMarkers.KindsByCount().Select(kind => kind.Kind));
    }

    public static MapViewSettings Capture(MapCanvas canvas, bool onlyActive, bool anyRegion) => new()
    {
        OnlyActive = onlyActive, AnyRegion = anyRegion,
        ShowExtracts = canvas.ShowExtracts, ShowLabels = canvas.ShowLabels,
        ShowBtr = canvas.ShowBtr, ShowLoot = canvas.ShowLoot,
        ShowContainers = canvas.ShowContainers, ShowSpawns = canvas.ShowSpawns,
        ShowHazards = canvas.ShowHazards, ShowStatics = canvas.ShowStatics,
        ShowDoors = canvas.ShowDoors, ShowSwitches = canvas.ShowSwitches,
        HiddenLootKinds = canvas.HiddenLootKinds.Order().ToArray(),
        HiddenContainerKinds = canvas.HiddenContainerKinds.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        MapKey = canvas.Map?.Key ?? "", Layer = canvas.CurrentLayer,
    };
}
