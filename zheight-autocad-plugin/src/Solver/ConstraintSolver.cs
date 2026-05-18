// ConstraintSolver.cs
// Validates and repairs geometry from the AI before drawing.
// Runs deterministically in C# — no AI involved.
//
// Nine constraint checks:
//   1. Baseline minimum dimensions (1800mm) — repair by scaling up
//   2. No-overlap (AABB) — repair with displacement
//   3. Site boundary (plot minus setbacks)
//   4. FAR / coverage
//   5. Adjacency requirements
//   6. Circulation (every room reachable via BFS from entry)
//   7. IRC 2021 code minimums by room type (habitable 2134mm, bath 1524mm, corridor 915mm)
//   8. Kitchen work triangle (2700mm × 2700mm minimum)
//   9. Bathroom-to-bedroom ratio and one-hop proximity

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

            // ── 7+8. IRC 2021 code minimums + kitchen work triangle (after baseline repair) ──
            ValidateMinimumDimensions(rooms, result);

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

            // VALIDATION-FIX: CHECK-CS03 — required type-based adjacency pairs
            CheckRequiredAdjacency(rooms, result);

            // ── 9. Bathroom-to-bedroom ratio and one-hop proximity ────────────
            ValidateBathroomBedRatio(rooms, result);

            // VALIDATION-FIX: CHECK-MISSING-10 — stair alignment across floors
            ValidateStairAlignment(rooms, result);

            return result;
        }

        // VALIDATION-FIX: CHECK-CS03 — validate required architectural adjacency pairs by room type
        private static void CheckRequiredAdjacency(List<RoomRect> rooms, SolverResult result)
        {
            static RoomRect? FindByType(List<RoomRect> rms, params string[] types) =>
                rms.FirstOrDefault(r => types.Any(t =>
                    string.Equals(r.Type, t, StringComparison.OrdinalIgnoreCase)));

            // kitchen ↔ dining
            var kitchen = FindByType(rooms, "kitchen", "open_kitchen", "kitchen_dining");
            var dining  = FindByType(rooms, "dining_room", "dining", "breakfast_nook");
            if (kitchen != null && dining != null && !kitchen.IsAdjacentTo(dining))
                result.Warnings.Add("Adjacency required: Kitchen should be adjacent to Dining — not met");

            // kitchen ↔ living
            var living = FindByType(rooms, "living_room", "living", "great_room", "family_room", "open_living");
            if (kitchen != null && living != null && !kitchen.IsAdjacentTo(living))
                result.Warnings.Add("Adjacency required: Kitchen should be adjacent to Living — not met");

            // primary_bedroom ↔ primary_bath
            var primBed  = FindByType(rooms, "primary_bedroom", "primary_suite", "master_bedroom");
            var primBath = FindByType(rooms, "primary_bath", "ensuite_bath", "ensuite", "master_bath");
            if (primBed != null && primBath != null && !primBed.IsAdjacentTo(primBath))
                result.Warnings.Add("Adjacency required: Primary Bedroom should be adjacent to Primary Bath — not met");

            // entry ↔ powder_room (only if both present)
            var entry  = FindByType(rooms, "entry", "foyer", "entry_foyer", "vestibule");
            var powder = FindByType(rooms, "powder_room", "half_bath", "toilet");
            if (entry != null && powder != null && !entry.IsAdjacentTo(powder, 3000))
                result.Warnings.Add("Adjacency recommended: Entry/Foyer should be near Powder Room — not met");

            // garage ↔ mudroom (only if both present)
            var garage  = FindByType(rooms, "garage");
            var mudroom = FindByType(rooms, "mudroom", "mud_room", "garage_entry");
            if (garage != null && mudroom != null && !garage.IsAdjacentTo(mudroom))
                result.Warnings.Add("Adjacency required: Garage should be adjacent to Mudroom — not met");
        }

        // VALIDATION-FIX: CHECK-MISSING-10 — stair footprints must vertically align across floors
        private static void ValidateStairAlignment(List<RoomRect> rooms, SolverResult result)
        {
            var floor1Stairs = rooms
                .Where(r => r.Floor == 1 &&
                            (r.Type.Contains("stair") || r.Name.ToLower().Contains("stair")))
                .ToList();
            var floor2Stairs = rooms
                .Where(r => r.Floor == 2 &&
                            (r.Type.Contains("stair") || r.Name.ToLower().Contains("stair")))
                .ToList();

            if (!floor1Stairs.Any() || !floor2Stairs.Any()) return;

            foreach (var s1 in floor1Stairs)
            {
                bool aligned = floor2Stairs.Any(s2 =>
                {
                    double overlapX = Math.Min(s1.Right, s2.Right) - Math.Max(s1.X, s2.X);
                    double overlapY = Math.Min(s1.Top,   s2.Top)   - Math.Max(s1.Y, s2.Y);
                    if (overlapX <= 0 || overlapY <= 0) return false;
                    double overlapArea = overlapX * overlapY;
                    double minArea     = Math.Min(s1.Width * s1.Height, s2.Width * s2.Height);
                    return overlapArea / minArea >= 0.80;
                });
                if (!aligned)
                    result.Warnings.Add(
                        $"Stair '{s1.Name}' (floor 1) does not align with any floor-2 stair — structural issue");
            }
        }

        // ── IRC 2021 + Kitchen Work Triangle ─────────────────────────────────

        // 4.1 — IRC 2021 §R304/R307/R311 minimum dimensions per room type.
        // 4.2 — Kitchen work triangle: 2700mm × 2700mm minimum rectangle.
        private static void ValidateMinimumDimensions(List<RoomRect> rooms, SolverResult result)
        {
            const double habitableMin = 2134; // §R304.2 — 7 ft any horizontal direction
            const double bathMin      = 1524; // §R307.1 — 5 ft for bathroom with tub/shower
            const double hallMin      =  915; // §R311.6 — 3 ft clear corridor width
            const double kitchenMin   = 2700; // work triangle: all sides ≤ 2.7m

            foreach (var r in rooms)
            {
                string t = RoomT(r.Type);
                bool isHabitable = t is "bedroom" or "primary_bedroom" or "primary_suite"
                    or "master_bedroom" or "secondary_bedroom" or "guest_bedroom"
                    or "guest_room" or "home_office_bedroom" or "nursery"
                    or "living_room" or "living" or "great_room" or "family_room"
                    or "open_living" or "dining_room" or "dining";
                bool isBath    = IsFullBathType(t);
                bool isHall    = t is "corridor" or "hallway";
                bool isKitchen = t is "kitchen" or "open_kitchen";

                if (isHabitable)
                {
                    double shortest = Math.Min(r.Width, r.Height);
                    if (shortest < habitableMin)
                        result.Errors.Add(
                            $"IRC 2021 §R304.2 — {r.Name}: shortest dimension " +
                            $"{shortest:F0}mm < 2134mm (7 ft) required for habitable room");
                }
                if (isBath && (r.Width < bathMin || r.Height < bathMin))
                    result.Warnings.Add(
                        $"IRC 2021 §R307.1 — {r.Name}: {r.Width:F0}×{r.Height:F0}mm " +
                        $"< 1524mm (5 ft) minimum for bathroom");
                if (isHall)
                {
                    double hallW = Math.Min(r.Width, r.Height);
                    if (hallW < hallMin)
                        result.Warnings.Add(
                            $"IRC 2021 §R311.6 — {r.Name}: corridor width {hallW:F0}mm " +
                            $"< 915mm (3 ft) minimum");
                }
                if (isKitchen && (r.Width < kitchenMin || r.Height < kitchenMin))
                    result.Errors.Add(
                        $"Work triangle — {r.Name}: {r.Width:F0}×{r.Height:F0}mm " +
                        $"is too small for a valid work triangle (minimum 2700×2700mm)");
            }
        }

        // 4.3 — Each bedroom must have a full bathroom within one adjacency hop.
        // Also checks the bedroom-to-bathroom ratio (warn if > 2 bedrooms per bath).
        private static void ValidateBathroomBedRatio(List<RoomRect> rooms, SolverResult result)
        {
            var beds  = rooms.Where(r => IsBedType(r.Type)).ToList();
            var baths = rooms.Where(r => IsFullBathType(r.Type)).ToList();

            if (!beds.Any()) return;

            // Ratio warning
            if (baths.Count > 0 && (double)beds.Count / baths.Count > 2.0)
                result.Warnings.Add(
                    $"Bedroom-to-bathroom ratio {beds.Count}:{baths.Count} — " +
                    $"recommend at least 1 full bath per 2 bedrooms");

            if (!baths.Any())
            {
                result.Warnings.Add("No full bathrooms found — every bedroom needs bathroom access");
                return;
            }

            // One-hop proximity: bed directly adjacent to bath, OR bed→corridor→bath
            const double hopTol = 400; // mm — slightly looser than strict adjacency to handle corridor gap
            foreach (var bed in beds)
            {
                bool hasAccess = baths.Any(bath => bed.IsAdjacentTo(bath, hopTol))
                    || rooms.Any(mid =>
                        !IsBedType(mid.Type) && !IsFullBathType(mid.Type) &&
                        bed.IsAdjacentTo(mid, hopTol) &&
                        baths.Any(bath => mid.IsAdjacentTo(bath, hopTol)));

                if (!hasAccess)
                    result.Warnings.Add(
                        $"{bed.Name}: no full bathroom reachable within one door of travel — " +
                        $"verify suite pairing in layout");
            }
        }

        // ── Type helpers ──────────────────────────────────────────────────────

        private static string RoomT(string? raw) =>
            (raw ?? "").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

        private static bool IsBedType(string? raw)
        {
            string t = RoomT(raw);
            return t is "bedroom" or "primary_bedroom" or "primary_suite" or "master_bedroom"
                or "secondary_bedroom" or "guest_bedroom" or "guest_room"
                or "home_office_bedroom" or "nursery";
        }

        private static bool IsFullBathType(string? raw)
        {
            string t = RoomT(raw);
            return t is "bathroom" or "primary_bath" or "ensuite_bath" or "ensuite"
                or "master_bath" or "secondary_bath" or "shared_bath" or "full_bath";
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // VALIDATION-FIX: CHECK-CS02 — enrich rooms from typed Layout.Zones when available
        private static List<RoomRect> ExtractRooms(VariationPlan plan)
        {
            var rooms = new List<RoomRect>();

            // ── Geometry always comes from actions (they carry X/Y) ──────────────
            var labelActions = plan.Actions
                .Where(a => a.ActionType == ActionType.DRAW_ROOM_LABEL &&
                            a.Center != null)
                .ToList();

            var groups = plan.Actions
                .Where(a => a.GroupId != null)
                .GroupBy(a => a.GroupId)
                .ToDictionary(g => g.Key!, g => g.ToList());

            // Build typed metadata lookup from Layout.Zones when present
            var typedMeta = new Dictionary<string, ZoneSpaceContract>(
                StringComparer.OrdinalIgnoreCase);
            if (plan.Layout?.Zones?.Count > 0)
            {
                foreach (var zone in plan.Layout.Zones)
                foreach (var sp in zone.Spaces)
                    typedMeta[sp.Name] = sp;
            }

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

                string roomName = label.LabelText ?? summary?.Name ?? gid;

                // Prefer typed contract for adjacency/separation metadata
                List<string> mustAdj = new();
                List<string> mustSep = new();
                if (typedMeta.TryGetValue(roomName, out var meta))
                {
                    // Adjacency is Dictionary<string,object>; values are JArray or List<string> at runtime
                    if (meta.Adjacency.TryGetValue("must_be_adjacent_to", out var adj))
                        mustAdj = ToStringList(adj);
                    if (meta.Adjacency.TryGetValue("must_be_separated_from", out var sep))
                        mustSep = ToStringList(sep);
                }

                rooms.Add(new RoomRect
                {
                    Name                = roomName,
                    Type                = summary?.Type ?? "",
                    X                   = minX,
                    Y                   = minY,
                    Width               = Math.Max(maxX - minX, 1),
                    Height              = Math.Max(maxY - minY, 1),
                    Floor               = summary?.Floor ?? 1,
                    MustBeAdjacentTo    = mustAdj,
                    MustBeSeparatedFrom = mustSep,
                });
            }

            return rooms;
        }

        private static List<string> ToStringList(object? val) => val switch
        {
            List<string> ls                     => ls,
            IEnumerable<object> en              => en.Select(x => x?.ToString() ?? "")
                                                     .Where(s => s.Length > 0).ToList(),
            string s when s.Length > 0          => new List<string> { s },
            _                                   => new List<string>(),
        };

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
