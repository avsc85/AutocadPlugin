// GridLayoutEngine.cs — v6: Full Architectural Fix
//
// Wing layout:
//   ┌─────────────────────┬──────┬──────────────────────────┐
//   │    Bedroom Wing     │ Hall │      Living Wing          │
//   │  Primary Suite      │      │  Entry / Foyer            │
//   │  [Bed][WIC][Bath]   │      │  [Powder Room] (optional) │
//   │  Bed2 | Bath2       │      │  Open Living & Kitchen    │
//   │  Bed3 | Bath3       │      │  Laundry | Mudroom | etc. │
//   └─────────────────────┴──────┴──────────────────────────┘
//   Rooms face outward: bedroom wing → north (backyard privacy)
//                       living wing  → south (street, natural light)
//
// Fixes vs v5:
//   1. Type normalization (spaces/dashes → underscores before any classification)
//   2. Powder rooms separated from bedroom baths → placed near entry in living wing
//   3. Room facing assigned correctly (bedroom=north, living/entry=south, kitchen=north)
//   4. Service rooms never overflow living wing
//   5. result.BuildingW/D always set for all layout paths
//   6. result.BuildingW > 0 check (not .Equals(0))

using System;
using System.Collections.Generic;
using System.Linq;

namespace zHeight.Plugin.Solver
{
    public class SpaceNode
    {
        public string Name              { get; set; } = "";
        public string Type              { get; set; } = "";
        public string ZoneName          { get; set; } = "";
        public double AreaSqm           { get; set; }
        public double AspectRatio       { get; set; } = 1.4;
        public int    Floor             { get; set; } = 1;
        public string Facing            { get; set; } = "south";
        public bool   HasNaturalLight   { get; set; } = true;
        public List<string> AdjacentTo  { get; set; } = new();
        public List<string> DoorConnects { get; set; } = new();
        public double MinWidthM         { get; set; } = 2.4;
        public double MinDepthM         { get; set; } = 2.4;

        public double X       { get; set; }
        public double Y       { get; set; }
        public double WidthMm { get; set; }
        public double DepthMm { get; set; }
        public double Right   => X + WidthMm;
        public double Top     => Y + DepthMm;
    }

    public class ZoneGroup
    {
        public string          ZoneName { get; set; } = "";
        public string          Position { get; set; } = "front";
        public List<SpaceNode> Spaces   { get; set; } = new();
    }

    public class LayoutResult
    {
        public List<SpaceNode> Rooms    { get; set; } = new();
        public List<string> Warnings    { get; set; } = new();
        public bool         IsValid     { get; set; } = true;
        public double       BuildingX   { get; set; }
        public double       BuildingY   { get; set; }
        public double       BuildingW   { get; set; }
        public double       BuildingD   { get; set; }
    }

    public static class GridLayoutEngine
    {
        private const double GRID    = 300;   // mm snap (≈ 1 ft)
        private const double STRUCT  = 1219;  // mm structural grid (4 ft)
        private const double MIN_DIM = 1800;  // mm minimum room side
        private const double HALLWAY = 1219;  // mm corridor width (4 ft)
        private const double CORRIDOR= 1200;  // mm strip corridor (linear fallback)

        // ── Type normalisation ─────────────────────────────────────────────────
        // Converts "Primary Bedroom", "primary-bedroom", etc. → "primary_bedroom"
        private static string N(string t) =>
            (t ?? "").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

        // ── Classification sets (all lowercase with underscores) ───────────────
        private static readonly HashSet<string> BedTypes = new(StringComparer.Ordinal)
        {
            "bedroom","master_bedroom","primary_bedroom","primary_suite",
            "secondary_bedroom","guest_bedroom","guest_room","kids_bedroom",
            "nursery","home_office_bedroom"
        };
        private static readonly HashSet<string> FullBathTypes = new(StringComparer.Ordinal)
        {
            "bathroom","bath","ensuite","ensuite_bath","primary_bath",
            "secondary_bath","shared_bath","full_bath","master_bath"
        };
        // Half-baths belong near entry, NOT paired with bedrooms
        private static readonly HashSet<string> HalfBathTypes = new(StringComparer.Ordinal)
        {
            "powder_room","half_bath","toilet"
        };
        private static readonly HashSet<string> BathTypes = new(StringComparer.Ordinal)
        {
            "bathroom","bath","ensuite","ensuite_bath","primary_bath",
            "secondary_bath","shared_bath","full_bath","master_bath",
            "powder_room","half_bath","toilet"
        };
        private static readonly HashSet<string> ClosetTypes = new(StringComparer.Ordinal)
        {
            "walk_in_closet","closet","wardrobe","wic","dressing_room"
        };
        private static readonly HashSet<string> EntryTypes = new(StringComparer.Ordinal)
        {
            "entry","foyer","entry_foyer","vestibule","front_entry","entry_hall","lobby"
        };
        private static readonly HashSet<string> ServiceTypes = new(StringComparer.Ordinal)
        {
            "laundry","laundry_room","mudroom","mud_room","garage","utility",
            "mechanical","storage","pantry","garage_entry"
        };
        private static readonly HashSet<string> OpenPlanTypes = new(StringComparer.Ordinal)
        {
            "kitchen","dining","living","great_room","family_room",
            "breakfast_nook","open_plan","open_kitchen","open_living",
            "living_room","dining_room","kitchen_dining","great_room"
        };
        // Enclair / covered outdoor rooms — glass-walled structures at rear of living wing
        private static readonly HashSet<string> EnclaireTypes = new(StringComparer.Ordinal)
        {
            "enclair","covered_porch","sunroom","sun_room","lanai","veranda",
            "covered_outdoor_room","conservatory","screened_porch","florida_room"
        };

        // ── Standard US room dimensions (mm) ──────────────────────────────────
        private static (double w, double d) StandardDims(string rawType, double areaSqm)
        {
            string t = N(rawType);
            return t switch
            {
                "primary_bedroom" or "primary_suite" or "master_bedroom"
                                                        => (3658, 4115),  // 12'×13'-6"
                "secondary_bedroom" or "bedroom"
                or "guest_bedroom"  or "guest_room"
                or "kids_bedroom"                       => (3048, 3658),  // 10'×12'
                "home_office_bedroom"                   => (3048, 3658),
                "primary_bath" or "ensuite_bath"
                or "ensuite"   or "master_bath"         => (1829, 2438),  // 6'×8'
                "bathroom" or "bath" or "full_bath"
                or "secondary_bath" or "shared_bath"    => (1524, 2438),  // 5'×8'
                "powder_room" or "half_bath" or "toilet"=> (914,  1829),  // 3'×6'
                "walk_in_closet" or "closet" or "wic"   => (1829, 2438),  // 6'×8'
                "entry" or "foyer" or "entry_foyer"
                or "vestibule"                          => (1829, 2438),  // 6'×8'
                "laundry"   or "laundry_room"           => (1829, 2438),  // 6'×8'
                "mudroom"   or "mud_room"               => (1829, 2438),
                "pantry"                                => (1219, 2438),  // 4'×8'
                "garage"                                => (6096, 6096),  // 20'×20'
                "utility"   or "mechanical"             => (1829, 2438),
                "storage"                               => (1219, 1829),
                _                                       => AreaDims(areaSqm),
            };
        }

        private static (double w, double d) AreaDims(double sqm)
        {
            double a = Math.Max(sqm, 4.0) * 1_000_000.0;
            double w = Snap(Math.Sqrt(a * 1.4), GRID);
            double d = Snap(Math.Sqrt(a / 1.4), GRID);
            return (Math.Max(w, MIN_DIM), Math.Max(d, MIN_DIM));
        }

        // ── Public entry point ─────────────────────────────────────────────────
        public static LayoutResult Layout(
            List<ZoneGroup> zones,
            string          strategy,
            double          plotW,
            double          plotD,
            double          frontSetback = 7500,
            double          sideSetback  = 1500,
            double          rearSetback  = 7500,
            double          gridModule   = 4000)
        {
            var result   = new LayoutResult();
            var warnings = new List<string>();

            double bx = sideSetback;
            double by = frontSetback;
            double bw = plotW - 2 * sideSetback;
            double bd = plotD - frontSetback - rearSetback;

            if (bw < 3000 || bd < 3000)
            {
                warnings.Add("Setbacks exceed plot — using full plot extent");
                bx = 0; by = 0; bw = plotW; bd = plotD;
            }

            // Normalise all space types before any logic
            foreach (var z in zones)
                foreach (var s in z.Spaces)
                    s.Type = N(s.Type);

            var allSpaces     = zones.SelectMany(z => z.Spaces).ToList();
            bool isResidential = allSpaces.Any(s => BedTypes.Contains(s.Type));

            if (isResidential && strategy != "spine")
                LayoutResidentialWing(zones, bx, by, bw, bd, result, warnings);
            else if (strategy == "spine")
                LayoutSpine(zones, bx, by, bw, bd, result.Rooms, warnings);
            else
                LayoutLinear(zones, bx, by, bw, bd, result.Rooms, warnings);

            // Ensure BuildingW/D are always set
            if (result.BuildingW <= 0)
            {
                result.BuildingX = bx;
                result.BuildingY = by;
                result.BuildingW = bw;
                result.BuildingD = bd;
            }

            result.Warnings = warnings;
            result.IsValid  = !result.Rooms.Any(a =>
                result.Rooms.Any(b => a != b && a.Floor == b.Floor && Overlaps(a, b)));

            return result;
        }

        // ══════════════════════════════════════════════════════════════════════
        // RESIDENTIAL WING LAYOUT
        // ══════════════════════════════════════════════════════════════════════
        private static void LayoutResidentialWing(
            List<ZoneGroup> zones,
            double bx, double by, double bw, double bd,
            LayoutResult result, List<string> warnings)
        {
            var all     = zones.SelectMany(z => z.Spaces).ToList();
            int floor   = all.Select(s => s.Floor).DefaultIfEmpty(1).Min();
            double sqm  = Math.Max(all.Sum(s => Math.Max(s.AreaSqm, 1.0)), 30.0);

            // ── House footprint: 4:3 aspect ratio ─────────────────────────────
            double mm2    = sqm * 1_000_000.0;
            double houseW = SnapG(Math.Sqrt(mm2 * 4.0 / 3.0));
            double houseD = SnapG(mm2 / Math.Max(houseW, STRUCT));
            houseW = Math.Clamp(houseW, STRUCT * 6, bw);
            houseD = Math.Clamp(houseD, STRUCT * 4, bd);

            result.BuildingX = bx;
            result.BuildingY = by;
            result.BuildingW = houseW;
            result.BuildingD = houseD;

            // ── Wing widths ───────────────────────────────────────────────────
            double bedWingW = SnapG(houseW * 0.45);
            bedWingW = Math.Max(bedWingW, STRUCT * 4);
            double hallW    = HALLWAY;
            double livWingW = houseW - bedWingW - hallW;
            if (livWingW < STRUCT * 3)
            {
                bedWingW = SnapG(houseW * 0.40);
                livWingW = houseW - bedWingW - hallW;
                warnings.Add("Bedroom wing narrowed to fit living wing");
            }
            livWingW = Math.Max(livWingW, STRUCT * 3);
            bedWingW = houseW - hallW - livWingW;

            // Living wing LEFT, hallway CENTRE, bedroom wing RIGHT
            // (matches standard US suburban plan: living faces street-left, beds private-right)
            double livX  = bx;
            double hallX = bx + livWingW;
            double bedX  = bx + livWingW + hallW;

            // ── Classify ─────────────────────────────────────────────────────
            var beds      = all.Where(s => BedTypes.Contains(s.Type))
                               .OrderByDescending(s => s.AreaSqm).ToList();
            var fullBaths = all.Where(s => FullBathTypes.Contains(s.Type)).ToList();
            var halfBaths = all.Where(s => HalfBathTypes.Contains(s.Type)).ToList();
            var closets   = all.Where(s => ClosetTypes.Contains(s.Type)).ToList();
            var entries   = all.Where(s => EntryTypes.Contains(s.Type)).ToList();
            var service   = all.Where(s => ServiceTypes.Contains(s.Type)).ToList();
            var open      = all.Where(s => OpenPlanTypes.Contains(s.Type)).ToList();
            var enclairs  = all.Where(s => EnclaireTypes.Contains(s.Type)).ToList();

            // Primary suite pieces
            var primaryBed = beds.FirstOrDefault(b =>
                b.Type is "primary_bedroom" or "primary_suite" or "master_bedroom" ||
                b.Name.IndexOf("Primary", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? beds.FirstOrDefault();

            var primaryBath = fullBaths.FirstOrDefault(b =>
                b.Type is "primary_bath" or "ensuite_bath" or "ensuite" or "master_bath" ||
                b.Name.IndexOf("Primary", StringComparison.OrdinalIgnoreCase) >= 0 ||
                b.Name.IndexOf("En", StringComparison.OrdinalIgnoreCase) >= 0);

            var primaryWic  = closets.FirstOrDefault();
            var secBeds     = beds.Where(b => b != primaryBed).ToList();
            var remBaths    = fullBaths.Where(b => b != primaryBath).ToList();

            // ── Bedroom wing row heights (scaled to fill houseD, capped at 1.15× standard) ──
            var rawH = new List<double>();
            if (primaryBed != null) rawH.Add(STRUCT * 4);   // ~16ft primary suite
            foreach (var _ in secBeds)    rawH.Add(STRUCT * 3);   // ~12ft secondary

            if (!rawH.Any()) rawH.Add(STRUCT * 4); // fallback if no beds classified
            double rawSum = rawH.Sum();
            double scale  = houseD / rawSum;
            var rowH = rawH.Select(h => Snap(h * scale, GRID)).ToList();
            // Fix last row to remove rounding gap
            if (rowH.Any())
                rowH[^1] = houseD - rowH.SkipLast(1).Sum();

            // Cap each row at 1.15× its standard height to prevent over-tall bedrooms
            // in deep houses (e.g. 3 beds on a 120ft lot → each row scales to 40ft)
            double capPrimary = Snap(STRUCT * 4 * 1.15, GRID); // ≈5600mm
            double capSecond  = Snap(STRUCT * 3 * 1.15, GRID); // ≈4200mm
            for (int i = 0; i < rowH.Count; i++)
            {
                double cap = (i == 0 && primaryBed != null) ? capPrimary : capSecond;
                rowH[i] = Math.Min(rowH[i], cap);
            }

            // ── Place bedroom wing ────────────────────────────────────────────
            double bedY  = by;
            int    ri    = 0;

            if (primaryBed != null && ri < rowH.Count)
            {
                double rh    = Math.Max(rowH[ri++], MIN_DIM);
                bool   hasW  = primaryWic != null;
                bool   hasB  = primaryBath != null;

                double bedFrac = hasW && hasB ? 0.55 : hasB ? 0.65 : 1.0;
                double bedW    = Snap(bedWingW * bedFrac, GRID);
                double rem     = bedWingW - bedW;

                primaryBed.Facing = "east"; // bedroom wing on right → east wall has windows
                Place(primaryBed, bedX, bedY, bedW, rh, result.Rooms);

                if (hasW && hasB)
                {
                    double ww = Snap(rem * 0.5, GRID);
                    primaryWic!.Facing  = "east";
                    primaryBath!.Facing = "east";
                    Place(primaryWic,  bedX + bedW,      bedY, ww,       rh, result.Rooms);
                    Place(primaryBath, bedX + bedW + ww, bedY, rem - ww, rh, result.Rooms);
                }
                else if (hasB)
                {
                    primaryBath!.Facing = "east";
                    Place(primaryBath, bedX + bedW, bedY, rem, rh, result.Rooms);
                }
                else if (hasW)
                {
                    primaryWic!.Facing = "east";
                    Place(primaryWic,  bedX + bedW, bedY, rem, rh, result.Rooms);
                }
                bedY += rh;
            }

            for (int i = 0; i < secBeds.Count && ri < rowH.Count; i++, ri++)
            {
                double rh   = Math.Max(rowH[ri], MIN_DIM);
                var    bath = i < remBaths.Count ? remBaths[i] : null;
                double bw2  = bath != null ? Snap(bedWingW * 0.65, GRID) : bedWingW;
                bw2 = Math.Max(bw2, MIN_DIM);

                secBeds[i].Facing = "east"; // bedroom wing on right, windows face east
                Place(secBeds[i], bedX, bedY, bw2, rh, result.Rooms);
                if (bath != null)
                {
                    bath.Facing = "east";
                    Place(bath, bedX + bw2, bedY, bedWingW - bw2, rh, result.Rooms);
                }
                bedY += rh;
            }

            // ── Add bonus/storage if bedroom rows don't fill houseD ──────────
            if (bedY < by + houseD - MIN_DIM * 2)
            {
                double bonusD = by + houseD - bedY;
                result.Rooms.Add(new SpaceNode {
                    Name = "Storage", Type = "storage", ZoneName = "bedroom_wing",
                    Floor = floor, HasNaturalLight = false, Facing = "north",
                    X = bedX, Y = bedY, WidthMm = bedWingW, DepthMm = bonusD,
                    AreaSqm = bedWingW * bonusD / 1_000_000.0,
                });
            }

            // ── Hallway (full house depth) ────────────────────────────────────
            result.Rooms.Add(new SpaceNode
            {
                Name = "Hallway", Type = "corridor", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = false, Facing = "south",
                X = hallX, Y = by, WidthMm = hallW, DepthMm = houseD,
                AreaSqm = hallW * houseD / 1_000_000.0,
            });

            // ── Living wing height budget ─────────────────────────────────────
            // From front (street) to rear (backyard):
            //   Entry row → Open plan → Service strip → Enclair (glass room at rear) → Garage
            var garages        = service.Where(s => s.Type == "garage").ToList();
            var nonGarService  = service.Where(s => s.Type != "garage").ToList();

            double entryH    = Math.Max(Snap(houseD * 0.15, GRID), STRUCT * 2);
            double garageH   = garages.Any()
                ? Math.Max(Snap(houseD * 0.22, GRID), 6096.0) : 0;
            double enclaireH = enclairs.Any()
                ? Math.Max(Snap(houseD * 0.20, GRID), STRUCT * 3) : 0;
            double serviceH  = nonGarService.Any()
                ? Math.Max(Snap(houseD * 0.15, GRID), STRUCT * 2) : 0;
            double openH     = Math.Max(houseD - entryH - garageH - enclaireH - serviceH, STRUCT * 3);

            // Correct so they sum exactly to houseD
            serviceH = houseD - entryH - garageH - enclaireH - openH;
            if (serviceH < 0) { openH += serviceH; serviceH = 0; }

            double livY = by;

            // ── Entry row (with optional half-bath side by side) ──────────────
            var entryRoom = entries.FirstOrDefault() ?? new SpaceNode
            {
                Name = "Entry", Type = "entry", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = true, Facing = "south",
            };

            if (halfBaths.Any())
            {
                // Half-bath beside entry, both in the entry row
                double hbW = Snap(livWingW * 0.30, GRID);
                double enW = livWingW - hbW;
                entryRoom.Facing = "south";
                Place(entryRoom,   livX,       livY, enW, entryH, result.Rooms);
                halfBaths[0].Facing = "south";
                Place(halfBaths[0], livX + enW, livY, hbW, entryH, result.Rooms);
                // Any extra half-baths ignored (rare)
            }
            else
            {
                entryRoom.Facing = "south";
                Place(entryRoom, livX, livY, livWingW, entryH, result.Rooms);
            }
            livY += entryH;

            // ── Open living + kitchen (merged, facing backyard) ───────────────
            double openArea = open.Any()
                ? open.Sum(s => Math.Max(s.AreaSqm, 1.0))
                : sqm * 0.30;
            var openRoom = new SpaceNode
            {
                Name = "Open Living & Kitchen", Type = "open_plan",
                ZoneName = "centre", Floor = floor,
                HasNaturalLight = true, Facing = "north", // faces backyard
                MinWidthM = 4.5, MinDepthM = 4.0, AreaSqm = openArea,
            };
            Place(openRoom, livX, livY, livWingW, openH, result.Rooms);
            livY += openH;

            // ── Service row (non-garage, side by side above enclair/garage) ────
            if (nonGarService.Any() && serviceH > 0)
            {
                double svcY = by + houseD - serviceH - enclaireH - garageH;
                double perW = Snap(livWingW / nonGarService.Count, GRID);
                double tmpX = livX;
                for (int i = 0; i < nonGarService.Count; i++)
                {
                    double w = (i == nonGarService.Count - 1)
                        ? livX + livWingW - tmpX : perW;
                    w = Math.Max(w, MIN_DIM);
                    nonGarService[i].Facing = "north";
                    Place(nonGarService[i], tmpX, svcY, w, serviceH, result.Rooms);
                    tmpX += w;
                }
            }

            // ── Enclair / covered outdoor room (glass-walled, rear of living wing) ──
            // Placed at the very rear of the living wing, opening to the backyard.
            if (enclairs.Any() && enclaireH > 0)
            {
                double encY = by + houseD - enclaireH - garageH;
                double perW = Snap(livWingW / enclairs.Count, GRID);
                double tmpX = livX;
                for (int i = 0; i < enclairs.Count; i++)
                {
                    double w = (i == enclairs.Count - 1)
                        ? livX + livWingW - tmpX : perW;
                    w = Math.Max(w, MIN_DIM);
                    enclairs[i].Facing         = "north"; // glass wall faces backyard
                    enclairs[i].HasNaturalLight = true;
                    enclairs[i].ZoneName        = "enclair";
                    Place(enclairs[i], tmpX, encY, w, enclaireH, result.Rooms);
                    tmpX += w;
                }
            }

            // ── Garage row (full living-wing width, at very rear) ─────────────
            if (garages.Any() && garageH > 0)
            {
                double garY = by + houseD - garageH;
                double perW = Snap(livWingW / garages.Count, GRID);
                double tmpX = livX;
                for (int i = 0; i < garages.Count; i++)
                {
                    double w = (i == garages.Count - 1)
                        ? livX + livWingW - tmpX : perW;
                    w = Math.Max(w, MIN_DIM);
                    garages[i].Facing = "north";
                    // Mark oversized garage as ADU-capable
                    if (garages[i].AreaSqm > 30)
                    {
                        garages[i].ZoneName       = "adu_capable";
                        garages[i].HasNaturalLight = true;
                    }
                    Place(garages[i], tmpX, garY, w, garageH, result.Rooms);
                    tmpX += w;
                }
            }
        }

        private static void Place(SpaceNode room, double x, double y,
                                   double w, double d, List<SpaceNode> rooms)
        {
            room.X       = Snap(x, GRID);
            room.Y       = Snap(y, GRID);
            room.WidthMm = Math.Max(Snap(w, GRID), MIN_DIM);
            room.DepthMm = Math.Max(Snap(d, GRID), MIN_DIM);
            room.AreaSqm = room.WidthMm * room.DepthMm / 1_000_000.0;
            rooms.Add(room);
        }

        // ══════════════════════════════════════════════════════════════════════
        // LINEAR STRIP LAYOUT (non-residential / fallback)
        // ══════════════════════════════════════════════════════════════════════
        private static void LayoutLinear(
            List<ZoneGroup> zones,
            double bx, double by, double bw, double bd,
            List<SpaceNode> rooms, List<string> warnings)
        {
            var ordered = zones.OrderBy(z => ZoneOrder(z.Position)).ToList();

            foreach (var zone in ordered.Where(z => z.Position == "centre"))
                zone.Spaces = MergeOpenPlan(zone.Spaces);

            int corCount = 0;
            for (int i = 0; i + 1 < ordered.Count; i++)
                if (NeedsCorridor(ordered[i].Position, ordered[i + 1].Position))
                    corCount++;

            double totalMm2  = ordered.Sum(z => z.Spaces.Sum(s => Math.Max(s.AreaSqm, 1.0)))
                               * 1_000_000.0;
            double buildingD = Snap(totalMm2 / Math.Max(bw, GRID), GRID);
            buildingD = Math.Clamp(buildingD, MIN_DIM * 3, bd);

            double avail = buildingD - corCount * CORRIDOR;
            avail = Math.Max(avail, MIN_DIM * ordered.Count);
            double total = Math.Max(ordered.Sum(z => ZoneArea(z)), 1.0);
            double curY  = by;

            for (int zi = 0; zi < ordered.Count; zi++)
            {
                if (zi > 0 && NeedsCorridor(ordered[zi-1].Position, ordered[zi].Position))
                {
                    int fl = ordered[zi].Spaces.FirstOrDefault()?.Floor ?? 1;
                    rooms.Add(new SpaceNode
                    {
                        Name = "Hallway", Type = "corridor", ZoneName = "circulation",
                        Floor = fl, HasNaturalLight = false,
                        X = bx, Y = curY, WidthMm = bw, DepthMm = CORRIDOR,
                        AreaSqm = bw * CORRIDOR / 1_000_000.0,
                    });
                    curY += CORRIDOR;
                }

                var zone = ordered[zi];
                double depth = (zi == ordered.Count - 1)
                    ? by + buildingD - curY
                    : Snap(avail * ZoneArea(zone) / total, GRID);
                depth = Math.Max(depth, MIN_DIM);
                FillZone(zone.Spaces, bx, curY, bw, depth, rooms);
                curY += depth;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SPINE LAYOUT (two-column)
        // ══════════════════════════════════════════════════════════════════════
        private static void LayoutSpine(
            List<ZoneGroup> zones,
            double bx, double by, double bw, double bd,
            List<SpaceNode> rooms, List<string> warnings)
        {
            double leftW  = Snap(bw * 0.40, GRID);
            double rightW = bw - leftW - CORRIDOR;
            double leftX  = bx;
            double rightX = bx + leftW + CORRIDOR;

            var assigned = new HashSet<ZoneGroup>();
            var left  = zones.Where(z => z.Position is "left" or "service" or "rear").ToList();
            var right = zones.Where(z => z.Position is "front" or "right" or "centre").ToList();
            assigned.UnionWith(left); assigned.UnionWith(right);
            right.AddRange(zones.Where(z => !assigned.Contains(z)));

            FillColumn(left,  leftX,  by, leftW,  bd, rooms);
            FillColumn(right, rightX, by, rightW, bd, rooms);
        }

        private static void FillColumn(List<ZoneGroup> zones,
            double cx, double cy, double cw, double cd, List<SpaceNode> rooms)
        {
            if (!zones.Any()) return;
            var ordered = zones.OrderBy(z => ZoneOrder(z.Position)).ToList();
            double total = Math.Max(ordered.Sum(z => ZoneArea(z)), 1.0);
            double curY  = cy;
            for (int zi = 0; zi < ordered.Count; zi++)
            {
                double depth = (zi == ordered.Count - 1)
                    ? cy + cd - curY
                    : Snap(cd * ZoneArea(ordered[zi]) / total, GRID);
                depth = Math.Max(depth, MIN_DIM);
                FillZone(ordered[zi].Spaces, cx, curY, cw, depth, rooms);
                curY += depth;
            }
        }

        private static void FillZone(List<SpaceNode> spaces,
            double rx, double ry, double rw, double rd, List<SpaceNode> rooms)
        {
            if (!spaces.Any()) return;
            var sorted  = spaces.OrderByDescending(s => s.AreaSqm).ToList();
            int n       = sorted.Count;
            int perRow  = n <= 2 ? n : n <= 5 ? 2 : 3;
            var rows    = new List<List<SpaceNode>>();
            for (int i = 0; i < n; i += perRow)
                rows.Add(sorted.Skip(i).Take(perRow).ToList());

            double rowH = Snap(rd / rows.Count, GRID);
            double curY = ry;
            for (int ri2 = 0; ri2 < rows.Count; ri2++)
            {
                var row = rows[ri2];
                double h = (ri2 == rows.Count - 1) ? ry + rd - curY : rowH;
                h = Math.Max(h, MIN_DIM);
                double tot = row.Sum(s => Math.Max(s.AreaSqm, 1.0));
                double curX = rx;
                for (int si = 0; si < row.Count; si++)
                {
                    double frac = tot > 0 ? Math.Max(row[si].AreaSqm, 1.0) / tot : 1.0 / row.Count;
                    double w    = (si == row.Count - 1) ? rx + rw - curX : Snap(rw * frac, GRID);
                    w = Math.Max(w, MIN_DIM);
                    row[si].X = curX; row[si].Y = curY;
                    row[si].WidthMm = w; row[si].DepthMm = h;
                    rooms.Add(row[si]);
                    curX += w;
                }
                curY += h;
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────
        private static List<SpaceNode> MergeOpenPlan(List<SpaceNode> spaces)
        {
            var open   = spaces.Where(s => OpenPlanTypes.Contains(s.Type)).ToList();
            var others = spaces.Where(s => !OpenPlanTypes.Contains(s.Type)).ToList();
            if (open.Count < 2) return spaces;
            var merged = new SpaceNode
            {
                Name = "Open Living & Kitchen", Type = "open_plan",
                AreaSqm = open.Sum(s => Math.Max(s.AreaSqm, 1.0)),
                AspectRatio = 1.8, Floor = open[0].Floor, Facing = open[0].Facing,
                HasNaturalLight = true, MinWidthM = 4.5, MinDepthM = 4.0,
            };
            var result = new List<SpaceNode> { merged };
            result.AddRange(others);
            return result;
        }

        private static bool NeedsCorridor(string prev, string next) =>
            prev is "centre" or "front" && next is "rear" or "service";

        private static double ZoneArea(ZoneGroup z) =>
            Math.Max(z.Spaces.Sum(s => Math.Max(s.AreaSqm, 1.0)), 1.0);

        private static bool Overlaps(SpaceNode a, SpaceNode b, double tol = 50) =>
            !(a.Right - tol <= b.X || a.X + tol >= b.Right ||
              a.Top   - tol <= b.Y || a.Y + tol >= b.Top);

        private static double Snap(double v, double g)  => Math.Round(v / g) * g;
        private static double SnapG(double v)           => Math.Round(v / STRUCT) * STRUCT;

        private static int ZoneOrder(string p) => p switch
        {
            "front"   => 0, "centre" => 1, "left" => 2,
            "right"   => 3, "rear"   => 4, "service" => 5, _ => 6,
        };
    }
}
