namespace TarkovMapLite;

/// <summary>Screen-space grouping for task points; connections express association, not a travel route.</summary>
internal static class TaskMapLayout
{
    internal sealed record Place(PointF Point, int Floor, bool FloorKnown, bool Candidate, int Count);
    internal static List<Place> Build(IEnumerable<ZoneShape> zones, float scale, PointF origin, int currentFloor)
    {
        var rows = zones.ToArray();
        var alternatives = rows.Where(z => z.Type == "findQuestItem")
            .GroupBy(z => z.ObjectiveId).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var places = new List<Place>();
        foreach (var zone in rows)
        {
            var point = new PointF(origin.X + zone.Center.X*scale, origin.Y + zone.Center.Y*scale);
            var floor = zone.FloorKnown ? zone.Floor : currentFloor;
            // Missing objective IDs cannot prove that nearby quest-item spawns are separate requirements.
            var candidate = zone.Type == "findQuestItem" && (zone.ObjectiveId.Length == 0 || alternatives.Contains(zone.ObjectiveId));
            var hit = places.FindIndex(p => p.Floor == floor && p.FloorKnown == zone.FloorKnown &&
                Math.Pow(p.Point.X-point.X,2)+Math.Pow(p.Point.Y-point.Y,2) <= 22*22);
            if (hit < 0) places.Add(new(point,floor,zone.FloorKnown,candidate,1));
            else
            {
                var old = places[hit]; var count = old.Count+1;
                places[hit] = old with { Point = new((old.Point.X*old.Count+point.X)/count,(old.Point.Y*old.Count+point.Y)/count),
                    Count=count, Candidate=old.Candidate || candidate };
            }
        }
        return places;
    }
    internal static PointF[] LinkPoints(IEnumerable<Place> places, int floor) => places
        .Where(p=>p.FloorKnown && p.Floor==floor && !p.Candidate).Select(p=>p.Point).ToArray();
    internal static string Label(string name, int index, int total, bool candidate) => total > 1
        ? $"{name} · {(candidate ? "候选 " : "")}{index+1}" : name;
}
