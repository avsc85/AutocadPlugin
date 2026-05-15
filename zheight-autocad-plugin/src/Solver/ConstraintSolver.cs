// ConstraintSolver.cs
// Validates and repairs geometry from the AI before drawing.
// Runs deterministically in C# — no AI involved.
//
// Six constraint checks:
//   1. No-overlap (AABB)
//   2. Site boundary (plot minus setbacks)
//   3. FAR / coverage
//   4. Adjacency requirements
//   5. Circulation (every room reachable via BFS from entry)
//   6. Minimum dimensions by space type

using System;
using System.Collections.Generic;
using System.Linq;
using zHeight.Plugin.Models;

namespace zHeight.Plugin.Solver
{
    public class RoomRect
    {
        public string Name     { get; init; } = "";
        public string Type     { get; init; } = "";
        public double X        { get; set; }
        public double Y        { get; set; }
        public double Width    { get; set; }
        public double Height   { get; set; }
        public int    Floor    { get; init; } = 1;
        public List<string> MustBeAdjacentTo    { get; init; } = new();
        public List<string> MustBeSeparatedFrom { get; init; } = new();

        public double Right   => X + Width;
        public double Top     => Y + Height;
        public double CenterX => X + Width  / 2;
        public double CenterY => Y + Height / 2;

        public bool Overlaps(RoomRect other, double tolerance = 50)
        {
            return !(Right  - tolerance <= other.X       ||
                     X      + tolerance >= other.Right   ||
                     Top    - tolerance <= other.Y       ||
                     Y      + tolerance >= other.Top);
        }

        public bool IsAdjacentTo(RoomRect other, double tolerance = 200)
        {
            bool xTouching = Math.Abs(Right - other.X) < tolerance ||
                             Math.Abs(other.Right - X) < tolerance;
            bool yTouching = Math.Abs(Top - other.Y) < tolerance ||
                             Math.Abs(other.Top - Y) < tolerance;
            bool xOverlap  = !(Right <= other.X || X >= other.Right);
            bool yOverlap  = !(Top   <= other.Y || Y >= other.Top);

            return (xTouching && yOverlap) || (yTouching && xOverlap);
        }
    }

    public class SolverResult
    {
        public bool           IsValid      { get; set; } = true;
        public List<string>   Warnings     { get; set; } = new();
        public List<string>   Errors       { get; set; } = new();
        public List<RoomRect> Rooms        { get; set; } = new();
        public int            RepairPasses { get; set; }
    }

    public class SiteConstraints
    {
        // US suburban residential defaults (~60 × 120 ft lot, 25/5/25 ft setbacks)
        public double PlotWidthMm    { get; init; } = 18000;
        public double PlotDepthMm    { get; init; } = 36000;
        public double FrontSetback   { get; init; } = 7500;
        public double SideSetback    { get; init; } = 1500;
        public double RearSetback    { get; init; } = 7500;
        public double MaxFar         { get; init; } = 0.5;
        public double MaxCoveragePct { get; init; } = 35.0;

        public double BuildableX    => SideSetback;
        public double BuildableY    => FrontSetback;
        public double BuildableW    => PlotWidthMm - 2 * SideSetback;
        public double BuildableH    => PlotDepthMm - FrontSetback - RearSetback;
        public double BuildableArea => BuildableW * BuildableH / 1_000_000; // sqm
    }

    public static class ConstraintSolver
    {
        private const int    MaxRepairPasses = 8;
        private const double MinRoomDimMm    = 1800;
        private const double CorridorWidthMm = 1200;

        public static SolverResult Validate(
            VariationPlan plan,
            SiteConstraints? site = null)
        {
            site ??= new SiteConstraints();

            var result = new SolverResult();
            var rooms  = ExtractRooms(plan);

            result.Rooms = rooms;

            // ── 1. Minimum dimensions ─────────────────────────────────────────
            foreach (var r in rooms)
            {
                if (r.Width < MinRoomDimMm || r.Height < MinRoomDimMm)
                {
                    result.Warnings.Add(
                        $"{r.Name}: dimension {r.Width:F0}×{r.Height:F0}mm " +
                        $"below minimum {MinRoomDimMm}mm — scaled up");
                    r.Width  = Math.Max(r.Width,  MinRoomDimMm);
                    r.Height = Math.Max(r.Height, MinRoomDimMm);
                }
            }

            // ── 2. No-overlap — repair with displacement ──────────────────────
            int passes = 0;
            bool overlapsFound;
            do
            {
                overlapsFound = false;
                for (int i = 0; i < rooms.Count; i++)
                for (int j = i + 1; j < rooms.Count; j++)
                {
                    if (rooms[i].Floor != rooms[j].Floor) continue;
                    if (!rooms[i].Overlaps(rooms[j])) continue;

                    overlapsFound = true;
                    double overlapX = Math.Min(rooms[i].Right, rooms[j].Right)
                                    - Math.Max(rooms[i].X,     rooms[j].X);
                    double overlapY = Math.Min(rooms[i].Top,   rooms[j].Top)
                                    - Math.Max(rooms[i].Y,     rooms[j].Y);

                    if (overlapX < overlapY)
                        rooms[j].X += overlapX + 150;
                    else
                        rooms[j].Y += overlapY + 150;
                }
                passes++;
                result.RepairPasses = passes;
            }
            while (overlapsFound && passes < MaxRepairPasses);

            if (overlapsFound)
            {
                result.Warnings.Add(
                    "Some rooms still overlap after repair — manual adjustment needed");
                result.IsValid = false;
            }

            // ── 3. Site boundary check ────────────────────────────────────────
            foreach (var r in rooms.Where(r => r.Floor == 1))
            {
                bool outsidePlot = r.X < site.BuildableX ||
                                   r.Y < site.BuildableY ||
                                   r.Right > site.BuildableX + site.BuildableW ||
                                   r.Top   > site.BuildableY + site.BuildableH;
                if (outsidePlot)
                {
                    r.X = Math.Max(r.X, site.BuildableX);
                    r.Y = Math.Max(r.Y, site.BuildableY);
                    r.X = Math.Min(r.X, site.BuildableX + site.BuildableW - r.Width);
                    r.Y = Math.Min(r.Y, site.BuildableY + site.BuildableH - r.Height);
                    result.Warnings.Add($"{r.Name}: moved inside plot boundary");
                }
            }

            // ── 4. FAR and coverage ───────────────────────────────────────────
            double totalBuiltSqm  = rooms.Sum(r => r.Width * r.Height / 1_000_000);
            double groundFloorSqm = rooms.Where(r => r.Floor == 1)
                                         .Sum(r => r.Width * r.Height / 1_000_000);
            double plotSqm     = site.PlotWidthMm * site.PlotDepthMm / 1_000_000;
            double farActual   = plotSqm > 0 ? totalBuiltSqm  / plotSqm       : 0;
            double coveragePct = plotSqm > 0 ? groundFloorSqm / plotSqm * 100 : 0;

            if (farActual > site.MaxFar)
                result.Warnings.Add(
                    $"FAR {farActual:F2} exceeds permitted {site.MaxFar:F2}");

            if (coveragePct > site.MaxCoveragePct)
                result.Warnings.Add(
                    $"Ground coverage {coveragePct:F1}% exceeds permitted {site.MaxCoveragePct:F1}%");

            // ── 5. Adjacency check ────────────────────────────────────────────
            var roomMap = rooms.ToDictionary(r => r.Name.ToLower(), r => r);

            foreach (var room in rooms)
            {
                foreach (var adjName in room.MustBeAdjacentTo)
                {
                    if (!roomMap.TryGetValue(adjName.ToLower(), out var adjRoom))
                        continue;
                    if (!room.IsAdjacentTo(adjRoom))
                        result.Warnings.Add(
                            $"Adjacency not met: {room.Name} should be adjacent " +
                            $"to {adjRoom.Name} — verify manually");
                }

                foreach (var sepName in room.MustBeSeparatedFrom)
                {
                    if (!roomMap.TryGetValue(sepName.ToLower(), out var sepRoom))
                        continue;
                    if (room.IsAdjacentTo(sepRoom, tolerance: 500))
                        result.Warnings.Add(
                            $"Separation not met: {room.Name} too close to {sepRoom.Name}");
                }
            }

            // ── 6. Circulation: BFS from entry ────────────────────────────────
            var entryRoom = rooms.FirstOrDefault(r =>
                r.Type.Contains("entry") || r.Type.Contains("lobby") ||
                r.Name.ToLower().Contains("entry") ||
                r.Name.ToLower().Contains("foyer") ||
                r.Name.ToLower().Contains("reception"));

            if (entryRoom != null)
            {
                var reachable   = BfsReach(entryRoom, rooms, CorridorWidthMm);
                var unreachable = rooms
                    .Where(r => r.Floor == entryRoom.Floor && !reachable.Contains(r.Name))
                    .ToList();

                foreach (var ur in unreachable)
                    result.Warnings.Add(
                        $"{ur.Name}: may not be reachable from entry — check circulation");
            }

            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static List<RoomRect> ExtractRooms(VariationPlan plan)
        {
            var rooms = new List<RoomRect>();
            var labelActions = plan.Actions
                .Where(a => a.ActionType == ActionType.DRAW_ROOM_LABEL &&
                            a.Center != null)
                .ToList();

            var groups = plan.Actions
                .Where(a => a.GroupId != null)
                .GroupBy(a => a.GroupId)
                .ToDictionary(g => g.Key!, g => g.ToList());

            foreach (var label in labelActions)
            {
                string gid = label.GroupId ?? "";
                if (!groups.TryGetValue(gid, out var groupActions)) continue;

                var walls = groupActions
                    .Where(a => a.ActionType == ActionType.DRAW_WALL &&
                                a.Start != null && a.End != null)
                    .ToList();

                if (!walls.Any()) continue;

                double minX = walls.SelectMany(w => new[] { w.Start!.X, w.End!.X }).Min();
                double minY = walls.SelectMany(w => new[] { w.Start!.Y, w.End!.Y }).Min();
                double maxX = walls.SelectMany(w => new[] { w.Start!.X, w.End!.X }).Max();
                double maxY = walls.SelectMany(w => new[] { w.Start!.Y, w.End!.Y }).Max();

                var summary = plan.SpaceSummary
                    .FirstOrDefault(s => gid.Contains(
                        s.Name.ToUpper().Replace(" ", "_")));

                rooms.Add(new RoomRect
                {
                    Name   = label.LabelText ?? summary?.Name ?? gid,
                    Type   = summary?.Type ?? "",
                    X      = minX,
                    Y      = minY,
                    Width  = Math.Max(maxX - minX, 1),
                    Height = Math.Max(maxY - minY, 1),
                    Floor  = summary?.Floor ?? 1,
                });
            }

            return rooms;
        }

        private static HashSet<string> BfsReach(
            RoomRect entry, List<RoomRect> all, double corridorWidth)
        {
            var visited = new HashSet<string> { entry.Name };
            var queue   = new Queue<RoomRect>();
            queue.Enqueue(entry);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var other in all.Where(r =>
                    r.Floor == current.Floor &&
                    !visited.Contains(r.Name) &&
                    current.IsAdjacentTo(r, corridorWidth)))
                {
                    visited.Add(other.Name);
                    queue.Enqueue(other);
                }
            }
            return visited;
        }
    }
}
