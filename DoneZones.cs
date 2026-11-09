// ============================================================================
// DoneZones.cs — 手动标记"这块任务区域我已经做完了"
//
// 为什么是手动的：游戏日志只给任务级状态（开始/失败/完成），子目标级的进度
// 在日志和公开接口里都不存在（查过：QuestConditionValueChanged 0 行、
// completedConditions 0 行、ConditionCounter 全是堆栈帧）。
// 一个任务要去两张图各做一件事时，只能靠玩家自己标一下。
//
// 存储：%LOCALAPPDATA%\TarkovMapLite\done.json，内容是
//   { "marks": ["<taskId>|<objectiveId>", ...] }
// key 用 taskId + objectiveId，**不用坐标**——数据刷新后区域坐标会变。
//
// 文件坏了/写不进去都当空处理：这个功能出问题不能影响地图本身。
// ============================================================================
using System.Drawing;
using System.Text.Json.Nodes;

namespace TarkovMapLite;

internal static class DoneZones
{
    private static readonly HashSet<string> Marks = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>标记变化时触发（窗口据此刷新列表和画布）。</summary>
    public static event Action? Changed;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TarkovMapLite");
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // 建不出目录就只在内存里记
            }
            return Path.Combine(dir, "done.json");
        }
    }

    private static string Key(ZoneShape zone) =>
        string.IsNullOrEmpty(zone.ObjectiveId)
            ? $"{zone.TaskId}|{zone.Floor}|{(int)zone.Center.X},{(int)zone.Center.Y}"
            : $"{zone.TaskId}|{zone.ObjectiveId}";

    public static bool IsDone(ZoneShape zone) => Marks.Contains(Key(zone));

    /// <summary>返回标记之后的状态（true = 已完成）。</summary>
    public static bool Toggle(ZoneShape zone)
    {
        EnsureLoaded();
        var key = Key(zone);
        var now = !Marks.Contains(key);
        if (now)
        {
            Marks.Add(key);
        }
        else
        {
            Marks.Remove(key);
        }
        Save();
        Changed?.Invoke();
        return now;
    }

    public static int Count => Marks.Count;

    public static void ClearAll()
    {
        EnsureLoaded();
        if (Marks.Count == 0)
        {
            return;
        }
        Marks.Clear();
        Save();
        Changed?.Invoke();
    }

    /// <summary>某个任务在这张图上还有几块没做完（用来在列表里写"本图已做完"）。</summary>
    public static (int Done, int Total) Progress(IEnumerable<ZoneShape> zones, string taskId)
    {
        EnsureLoaded();
        var done = 0;
        var total = 0;
        foreach (var zone in zones)
        {
            if (!string.Equals(zone.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            total++;
            if (IsDone(zone))
            {
                done++;
            }
        }
        return (done, total);
    }

    private static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        try
        {
            var path = FilePath;
            if (!File.Exists(path))
            {
                return;
            }
            if (JsonNode.Parse(File.ReadAllText(path))?["marks"] is JsonArray marks)
            {
                foreach (var item in marks)
                {
                    var text = item?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Marks.Add(text);
                    }
                }
            }
        }
        catch
        {
            // 文件坏了就当没有标记
        }
    }

    private static void Save()
    {
        try
        {
            var array = new JsonArray();
            foreach (var key in Marks)
            {
                array.Add(key);
            }
            var root = new JsonObject { ["marks"] = array };
            File.WriteAllText(FilePath, root.ToJsonString());
        }
        catch
        {
            // 写不进去就只活在内存里
        }
    }

    /// <summary>
    /// 屏幕点 → 落在哪块任务区域上（右键标记用）。取面积最小的那个：
    /// 大区域套小区域时，玩家想点的通常是小块。
    /// </summary>
    public static ZoneShape? HitTest(IEnumerable<ZoneShape> zones, PointF screen, Func<PointF, PointF> project)
    {
        ZoneShape? best = null;
        var bestArea = float.MaxValue;
        foreach (var zone in zones)
        {
            if (zone.Points.Length < 3)
            {
                continue;
            }
            var polygon = new PointF[zone.Points.Length];
            for (var i = 0; i < zone.Points.Length; i++)
            {
                polygon[i] = project(zone.Points[i]);
            }
            if (!Contains(polygon, screen))
            {
                continue;
            }
            var area = Math.Abs(zone.Bounds.Width * zone.Bounds.Height);
            if (area < bestArea)
            {
                bestArea = area;
                best = zone;
            }
        }
        return best;
    }

    /// <summary>射线法判断点是否在多边形内（屏幕坐标）。</summary>
    private static bool Contains(PointF[] polygon, PointF point)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var yi = polygon[i].Y;
            var yj = polygon[j].Y;
            if ((yi > point.Y) == (yj > point.Y))
            {
                continue;
            }
            var x = (polygon[j].X - polygon[i].X) * (point.Y - yi) / (yj - yi) + polygon[i].X;
            if (point.X < x)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
