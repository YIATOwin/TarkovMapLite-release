namespace TarkovMapLite;

internal enum LayerIcon
{
    Label, Btr, Loot, Dot, ClearMarks,
    Spawn, Boss, Sniper, Hazard, Weapon, Door, Switch, Container,
    Key, Meds, Food, Poster, Storage, Junk, Docs,
    Crate, Bag, Drawer, Hanger, Safe, Register, Computer, Body,
    Ammo, Grenade, Toolbox, Barrel, Cache, MedicalBag,
    Extract, Transit, ShowAll, HideAll,
}

internal sealed record LayerIconInfo(LayerIcon Kind, Color Color, bool Toggle);

/// <summary>菜单使用与地图相同的矢量轮廓，20px 逻辑尺寸，无外部图片依赖。</summary>
internal static class LayerMenuArt
{
    public static Bitmap Create(LayerIcon kind, Color color, int size = 20) => MarkerIcons.Create(kind,color,size);
}
