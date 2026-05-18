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

        public bool   IsOpenPlan { get; set; } = false;
        // Solar wall: the facade that should receive primary windows (MISSING-03).
        // Empty string = fall back to Facing for window placement.
        public string SolarWall  { get; set; } = "";

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
        // VALIDATION-FIX: CHECK-C06 — 1219mm (4ft) is substandard; 1500mm is premium residential minimum
        private const double HALLWAY = 1500;  // mm corridor width — premium residential minimum
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
            double          frontSetback    = 7500,
            double          sideSetback     = 1500,
            double          rearSetback     = 7500,
            double          gridModule      = 4000,
            string          wingOrientation = "living_left",
            string          garagePlacement = "rear")
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

            // "residential" strategy explicitly activates wing layout even when
            // type detection fails (e.g. incomplete zone data from Gemini)
            bool useResidential = isResidential ||
                string.Equals(strategy, "residential", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(strategy, "open_plan",   StringComparison.OrdinalIgnoreCase);

            if (string.Equals(strategy, "split_wing", StringComparison.OrdinalIgnoreCase))
                LayoutSplitWing(zones, bx, by, bw, bd, result, warnings,
                                wingOrientation, garagePlacement);
            else if (useResidential && !string.Equals(strategy, "spine", StringComparison.OrdinalIgnoreCase))
                LayoutResidentialWing(zones, bx, by, bw, bd, result, warnings,
                                      strategy, wingOrientation, garagePlacement);
            else if (string.Equals(strategy, "spine", StringComparison.OrdinalIgnoreCase))
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
            LayoutResult result, List<string> warnings,
            string strategy, string wingOrientation, string garagePlacement)
        {
            var all     = zones.SelectMany(z => z.Spaces).ToList();
            int floor   = all.Select(s => s.Floor).DefaultIfEmpty(1).Min();
            double sqm  = Math.Max(all.Sum(s => Math.Max(s.AreaSqm, 1.0)), 30.0);

            // ── House footprint: lot-responsive aspect ratio ──────────────────
            double mm2      = sqm * 1_000_000.0;
            double lotRatio = bw / Math.Max(bd, 1.0);
            string strat    = (strategy ?? "").ToLowerInvariant();
            double targetAsp = strat switch
            {
                var s when s.Contains("ranch")   => Math.Min(2.8, lotRatio * 0.9),
                var s when s.Contains("compact") => 1.0,
                var s when s.Contains("spine")   => 0.8,
                "courtyard"                      => 1.0,
                _                                => 1.7,  // wide+shallow suburban default (was 1.4)
            };
            // 9000mm (30 ft) minimum — wing layout cannot resolve below this width
            double houseW = Math.Max(SnapG(Math.Sqrt(mm2 * targetAsp)), 9000);
            double houseD = SnapG(mm2 / Math.Max(houseW, STRUCT));
            houseW = Math.Clamp(houseW, STRUCT * 6, bw);
            houseD = Math.Clamp(houseD, STRUCT * 4, bd);

            result.BuildingX = bx;
            result.BuildingY = by;
            result.BuildingW = houseW;
            result.BuildingD = houseD;

            // ── Wing widths ───────────────────────────────────────────────────
            // Living zone dominates for compact plans: 62% for ≤2 bed, 55% for 3+ bed
            int bedCount     = all.Count(s => BedTypes.Contains(s.Type));
            double livFrac   = bedCount <= 2 ? 0.62 : 0.55;
            double hallW     = HALLWAY;
            double livWingW  = SnapG(houseW * livFrac);
            double bedWingW  = houseW - hallW - livWingW;
            bedWingW = Math.Max(bedWingW, STRUCT * 4);
            // Re-derive livWingW after clamping bedWingW to avoid under-sizing
            livWingW = houseW - hallW - bedWingW;
            if (livWingW < STRUCT * 3)
            {
                bedWingW = SnapG(houseW * (1.0 - livFrac) + 0.02);  // concede 2% to beds
                livWingW = houseW - hallW - bedWingW;
                warnings.Add("Bedroom wing narrowed to fit living wing");
            }
            livWingW = Math.Max(livWingW, STRUCT * 3);
            bedWingW = houseW - hallW - livWingW;

            // Wing orientation — "living_left" (default CA suburban) or "living_right"
            bool livingOnLeft = !string.Equals(wingOrientation, "living_right",
                                               StringComparison.OrdinalIgnoreCase);
            double livX  = livingOnLeft ? bx                           : bx + bedWingW + hallW;
            double hallX = livingOnLeft ? bx + livWingW                : bx + bedWingW;
            double bedX  = livingOnLeft ? bx + livWingW + hallW        : bx;
            // Bedroom outer wall faces east when living is left, west when living is right
            string bedFacing = livingOnLeft ? "east" : "west";

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
            // FIX-ARCH-03: secondary bedrooms first (near street/low-Y) → primary suite last (rear)
            var rawH = new List<double>();
            foreach (var _ in secBeds)    rawH.Add(STRUCT * 3);   // ~12ft secondary — near street
            if (primaryBed != null)       rawH.Add(STRUCT * 4);   // ~16ft primary suite — at rear

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
            int primaryRowIdx = rowH.Count - 1; // primary suite is the last row
            for (int i = 0; i < rowH.Count; i++)
            {
                double cap = (i == primaryRowIdx && primaryBed != null) ? capPrimary : capSecond;
                rowH[i] = Math.Min(rowH[i], cap);
            }

            // ── Place bedroom wing — secondary beds first, primary suite at rear ─
            double bedY = by;
            int    ri   = 0;

            // Secondary bedrooms (near street — low Y) + paired bath on same row
            // Bath: 35% of wing width (min 1500mm/5ft), Bed: 65% (min 2700mm/9ft)
            for (int i = 0; i < secBeds.Count && ri < rowH.Count; i++, ri++)
            {
                double rh   = Math.Max(rowH[ri], MIN_DIM);
                var    bath = i < remBaths.Count ? remBaths[i] : null;

                secBeds[i].Facing    = bedFacing;
                secBeds[i].SolarWall = SolarWallForType(N(secBeds[i].Type));

                if (bath != null)
                {
                    double bathW = Math.Max(Snap(bedWingW * 0.35, GRID), 1500);
                    double bedW  = Math.Max(bedWingW - bathW, 2700);
                    // Re-clamp: ensure sum still equals bedWingW
                    bathW = bedWingW - bedW;
                    Place(secBeds[i], bedX,         bedY, bedW,  rh, result.Rooms);
                    bath.Facing    = bedFacing;
                    bath.SolarWall = SolarWallForType(N(bath.Type));
                    Place(bath,       bedX + bedW,   bedY, bathW, rh, result.Rooms);
                }
                else
                    Place(secBeds[i], bedX, bedY, bedWingW, rh, result.Rooms);

                bedY += rh;
            }

            // Primary suite at rear (highest Y — deepest privacy corner)
            if (primaryBed != null && ri < rowH.Count)
            {
                double rh    = Math.Max(rowH[ri++], MIN_DIM);
                bool   hasW  = primaryWic != null;
                bool   hasB  = primaryBath != null;

                // Derive suite sub-widths from standard dims so proportions match real rooms
                double stdBedW  = StandardDims(primaryBed.Type, primaryBed.AreaSqm).w;
                double stdExtra = (hasW ? StandardDims(primaryWic!.Type,  primaryWic.AreaSqm).w  : 0)
                                + (hasB ? StandardDims(primaryBath!.Type, primaryBath.AreaSqm).w : 0);
                double bedFrac  = stdBedW + stdExtra > 0
                    ? Math.Clamp(stdBedW / (stdBedW + stdExtra), 0.40, 0.75)
                    : (hasW && hasB ? 0.55 : hasB ? 0.65 : 1.0);
                double bedW    = Snap(bedWingW * bedFrac, GRID);
                double rem     = bedWingW - bedW;

                primaryBed.Facing    = bedFacing;
                primaryBed.SolarWall = SolarWallForType(N(primaryBed.Type));
                Place(primaryBed, bedX, bedY, bedW, rh, result.Rooms);

                if (hasW && hasB)
                {
                    double ww = Snap(rem * 0.5, GRID);
                    primaryWic!.Facing   = bedFacing;
                    primaryBath!.Facing  = bedFacing;
                    primaryBath.SolarWall = SolarWallForType(N(primaryBath.Type));
                    Place(primaryWic,  bedX + bedW,      bedY, ww,       rh, result.Rooms);
                    Place(primaryBath, bedX + bedW + ww, bedY, rem - ww, rh, result.Rooms);
                }
                else if (hasB)
                {
                    primaryBath!.Facing   = bedFacing;
                    primaryBath.SolarWall = SolarWallForType(N(primaryBath.Type));
                    Place(primaryBath, bedX + bedW, bedY, rem, rh, result.Rooms);
                }
                else if (hasW)
                {
                    primaryWic!.Facing = bedFacing;
                    Place(primaryWic,  bedX + bedW, bedY, rem, rh, result.Rooms);
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

            // ── Hallway with linen-closet termination (visual corridor stop at rear) ──
            double linenD   = 900; // 900mm deep linen closet terminates the corridor
            double hallCorD = houseD - linenD;
            result.Rooms.Add(new SpaceNode
            {
                Name = "Hallway", Type = "corridor", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = false, Facing = "south",
                X = hallX, Y = by, WidthMm = hallW, DepthMm = hallCorD,
                AreaSqm = hallW * hallCorD / 1_000_000.0,
            });
            result.Rooms.Add(new SpaceNode
            {
                Name = "Linen", Type = "storage", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = false, Facing = "north",
                X = hallX, Y = by + hallCorD, WidthMm = hallW, DepthMm = linenD,
                AreaSqm = hallW * linenD / 1_000_000.0,
            });

            // ── Living wing height budget ─────────────────────────────────────
            // From front (street) to rear (backyard):
            //   [Front garage if front-loaded] → Entry row → Open plan →
            //   Service strip → Enclair (glass room at rear) → [Rear garage if rear-loaded]
            var garages       = service.Where(s => s.Type == "garage").ToList();
            var nonGarService = service.Where(s => s.Type != "garage").ToList();
            bool garageFront  = garages.Any() &&
                string.Equals(garagePlacement, "front", StringComparison.OrdinalIgnoreCase);

            double entryH    = Math.Min(Snap(houseD * 0.18, GRID), 2400);  // 8 ft max — transitional foyer only
            double garageH   = garages.Any()
                ? Math.Max(Snap(houseD * 0.22, GRID), 6096.0) : 0;
            double enclaireH = enclairs.Any()
                ? Math.Max(Snap(houseD * 0.20, GRID), STRUCT * 3) : 0;
            double serviceH  = nonGarService.Any()
                ? Math.Max(Snap(houseD * 0.15, GRID), STRUCT * 2) : 0;
            double rearGarH  = garageFront ? 0 : garageH; // rear garage only when not front-loaded
            double openH     = Math.Max(houseD - entryH - rearGarH - enclaireH - serviceH
                                        - (garageFront ? garageH : 0), STRUCT * 3);

            // Correct so they sum exactly to houseD
            serviceH = houseD - entryH - rearGarH - enclaireH - openH
                       - (garageFront ? garageH : 0);
            if (serviceH < 0) { openH += serviceH; serviceH = 0; }

            double livY = by;

            // ── Front-loaded garage (street-facing, placed before entry) ──────
            if (garageFront && garageH > 0)
            {
                double perW = Snap(livWingW / garages.Count, GRID);
                double tmpX = livX;
                for (int i = 0; i < garages.Count; i++)
                {
                    double w = (i == garages.Count - 1) ? livX + livWingW - tmpX : perW;
                    w = Math.Max(w, MIN_DIM);
                    garages[i].Facing = "south"; // faces street
                    if (garages[i].AreaSqm > 30)
                    {
                        garages[i].ZoneName       = "adu_capable";
                        garages[i].HasNaturalLight = true;
                    }
                    Place(garages[i], tmpX, livY, w, garageH, result.Rooms);
                    tmpX += w;
                }
                livY += garageH;
            }

            // ── Entry row — small transitional foyer at street face ──────────
            // Entry: 40% of living wing width, max 2400mm deep (8 ft)
            // Powder room (or remaining space) fills the other 60% beside it
            var entryRoom = entries.FirstOrDefault() ?? new SpaceNode
            {
                Name = "Entry", Type = "entry", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = true, Facing = "south",
            };

            double entryW = Snap(livWingW * 0.40, GRID);
            entryW = Math.Max(entryW, STRUCT * 2);  // at least 8 ft wide
            entryRoom.Facing = "south";
            Place(entryRoom, livX, livY, entryW, entryH, result.Rooms);

            if (halfBaths.Any())
            {
                halfBaths[0].Facing = "south";
                Place(halfBaths[0], livX + entryW, livY, livWingW - entryW, entryH, result.Rooms);
            }
            livY += entryH;

            // ── MISSING-01: Open-plan zone — individual rooms in sequence ────────
            // Order (south→north, i.e. entry→backyard): living → dining → kitchen.
            // Shared walls between them are suppressed at draw time via IsOpenPlan.
            double openArea = open.Any()
                ? open.Sum(s => Math.Max(s.AreaSqm, 1.0))
                : sqm * 0.30;

            // Bucket incoming rooms by category
            var livingBucket  = open.Where(s => s.Type is "living" or "living_room"
                                               or "great_room" or "family_room"
                                               or "open_living").ToList();
            var diningBucket  = open.Where(s => s.Type is "dining" or "dining_room"
                                               or "breakfast_nook" or "kitchen_dining").ToList();
            var kitchenBucket = open.Where(s => s.Type is "kitchen" or "open_kitchen"
                                               or "open_plan").ToList();

            // Remaining unclassified open rooms go to living
            var classified = livingBucket.Concat(diningBucket).Concat(kitchenBucket)
                                         .ToHashSet();
            livingBucket.AddRange(open.Where(s => !classified.Contains(s)));

            // If Gemini sent no individual rooms, synthesise proportional split
            if (!livingBucket.Any() && !diningBucket.Any() && !kitchenBucket.Any())
            {
                livingBucket.Add(new SpaceNode  { Name = "Living Room",  Type = "living_room",
                    Floor = floor, HasNaturalLight = true, AreaSqm = openArea * 0.40 });
                diningBucket.Add(new SpaceNode  { Name = "Dining Room",  Type = "dining_room",
                    Floor = floor, HasNaturalLight = true, AreaSqm = openArea * 0.25 });
                kitchenBucket.Add(new SpaceNode { Name = "Kitchen",       Type = "kitchen",
                    Floor = floor, HasNaturalLight = true, AreaSqm = openArea * 0.35 });
            }

            // Merge each bucket into a single node; skip null (empty bucket)
            var openSeq = new List<SpaceNode>();
            SpaceNode? Merge(List<SpaceNode> bucket, string dName, string dType)
            {
                if (!bucket.Any()) return null;
                if (bucket.Count == 1) return bucket[0];
                return new SpaceNode { Name = dName, Type = dType,
                    Floor = bucket[0].Floor, HasNaturalLight = true,
                    AreaSqm = bucket.Sum(r => Math.Max(r.AreaSqm, 1.0)) };
            }
            var lNode = Merge(livingBucket,  "Living Room", "living_room");
            var dNode = Merge(diningBucket,  "Dining Room", "dining_room");
            var kNode = Merge(kitchenBucket, "Kitchen",     "kitchen");
            foreach (var n in new[] { lNode, dNode, kNode })
                if (n != null) openSeq.Add(n);

            double totalOpenArea = openSeq.Sum(n => n.AreaSqm) < 0.01
                ? openArea : openSeq.Sum(n => Math.Max(n.AreaSqm, 1.0));
            double curLivY = livY;

            // ── ARCH-04: Lateral open-plan — Living full-width at front (south),
            //             Dining + Kitchen side-by-side at rear (toward backyard/enclair).
            //   livWingW
            //   ├────────────────────────────────┐
            //   │       LIVING  (full width)      │  ~55 % of openH — faces entry
            //   ├───────────────┬────────────────┤
            //   │    DINING     │    KITCHEN     │  ~45 % of openH — kitchen at enclair
            //   └───────────────┴────────────────┘
            bool useLateral = lNode != null && (dNode != null || kNode != null);
            if (useLateral)
            {
                double openLivFrac = totalOpenArea > 0 && lNode!.AreaSqm > 0
                    ? lNode.AreaSqm / totalOpenArea : 0.55;
                openLivFrac = Math.Clamp(openLivFrac, 0.45, 0.65);
                double frontH = Snap(openH * openLivFrac, GRID);
                frontH = Math.Max(frontH, STRUCT * 3);
                double rearH  = openH - frontH;
                rearH  = Math.Max(rearH, STRUCT * 2);
                frontH = openH - rearH; // ensure exact sum

                lNode!.IsOpenPlan = true; lNode.ZoneName = "open_plan";
                lNode.Facing = "south";  lNode.SolarWall = "south";
                Place(lNode, livX, curLivY, livWingW, frontH, result.Rooms);
                curLivY += frontH;

                if (dNode != null && kNode != null)
                {
                    double dkTotal = Math.Max(dNode.AreaSqm, 1.0) + Math.Max(kNode.AreaSqm, 1.0);
                    double kitFrac = Math.Clamp(kNode.AreaSqm / dkTotal, 0.40, 0.60);
                    double kitW    = Snap(livWingW * kitFrac, GRID);
                    double dinW    = livWingW - kitW;
                    kitW = Math.Max(kitW, MIN_DIM); dinW = Math.Max(dinW, MIN_DIM);

                    dNode.IsOpenPlan = true; dNode.ZoneName = "open_plan";
                    dNode.Facing = "north"; dNode.SolarWall = "south";
                    Place(dNode, livX, curLivY, dinW, rearH, result.Rooms);

                    kNode.IsOpenPlan = true; kNode.ZoneName = "open_plan";
                    kNode.Facing = "north"; kNode.SolarWall = "north";
                    Place(kNode, livX + dinW, curLivY, kitW, rearH, result.Rooms);
                }
                else if (kNode != null)
                {
                    kNode.IsOpenPlan = true; kNode.ZoneName = "open_plan";
                    kNode.Facing = "north"; kNode.SolarWall = "north";
                    Place(kNode, livX, curLivY, livWingW, rearH, result.Rooms);
                }
                else
                {
                    dNode!.IsOpenPlan = true; dNode!.ZoneName = "open_plan";
                    dNode.Facing = "north"; dNode.SolarWall = "south";
                    Place(dNode, livX, curLivY, livWingW, rearH, result.Rooms);
                }
                curLivY += rearH;
            }
            else
            {
                // Stacked fallback when only one open-plan zone type exists
                for (int i = 0; i < openSeq.Count; i++)
                {
                    var node = openSeq[i];
                    double frac  = totalOpenArea > 0 ? node.AreaSqm / totalOpenArea : 1.0 / openSeq.Count;
                    double roomH = (i == openSeq.Count - 1)
                        ? livY + openH - curLivY
                        : Snap(openH * frac, GRID);
                    roomH = Math.Max(roomH, MIN_DIM);
                    node.IsOpenPlan = true;
                    node.ZoneName   = "open_plan";
                    node.Facing     = "north";
                    node.SolarWall  = SolarWallForType(N(node.Type));
                    Place(node, livX, curLivY, livWingW, roomH, result.Rooms);
                    curLivY += roomH;
                }
            }
            livY += openH;

            // ── Service row (non-garage, side by side above enclair/garage) ────
            if (nonGarService.Any() && serviceH > 0)
            {
                double svcY = by + houseD - serviceH - enclaireH - rearGarH;
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
            // Single enclair must span exactly livWingW — rear wall continuation of living zone
            if (enclairs.Any() && enclaireH > 0)
            {
                double encY = by + houseD - enclaireH - rearGarH;
                double encW = enclairs.Count == 1
                    ? livWingW
                    : Snap(livWingW / enclairs.Count, GRID);
                for (int i = 0; i < enclairs.Count; i++)
                {
                    double w = (i == enclairs.Count - 1)
                        ? livX + livWingW - (livX + i * encW) : encW;
                    w = Math.Max(w, MIN_DIM);
                    enclairs[i].Facing         = "north";
                    enclairs[i].HasNaturalLight = true;
                    enclairs[i].ZoneName        = "enclair";
                    Place(enclairs[i], livX + i * encW, encY, w, enclaireH, result.Rooms);
                }
            }

            // ── Rear-loaded garage (alley access, at very rear of living wing) ─
            if (!garageFront && garages.Any() && rearGarH > 0)
            {
                double garY = by + houseD - rearGarH;
                double perW = Snap(livWingW / garages.Count, GRID);
                double tmpX = livX;
                for (int i = 0; i < garages.Count; i++)
                {
                    double w = (i == garages.Count - 1)
                        ? livX + livWingW - tmpX : perW;
                    w = Math.Max(w, MIN_DIM);
                    garages[i].Facing = "north";
                    if (garages[i].AreaSqm > 30)
                    {
                        garages[i].ZoneName       = "adu_capable";
                        garages[i].HasNaturalLight = true;
                    }
                    Place(garages[i], tmpX, garY, w, rearGarH, result.Rooms);
                    tmpX += w;
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        // SPLIT-WING LAYOUT  (Y / T footprint)
        // ══════════════════════════════════════════════════════════════════════
        //   Street
        //   ├─────────────────────────────────────────────────┐
        //   │        STEM — open living core (full width)      │  ~45% depth
        //   │        Entry → Living → Dining + Kitchen         │
        //   └──────────────────────┬──────────────────────────┘
        //          [light gap]     │     [light gap]
        //   ┌──────────────┐  gap  ┌──────────────┐
        //   │  BED WING    │       │  SERVICE WING │  ~55% depth
        //   │  Sec beds    │       │  Laundry      │
        //   │  Primary suite│      │  Mudroom      │
        //   └──────────────┘       │  Garage/Enclair│
        //                          └──────────────┘
        //   Backyard
        // Wing orientation ("living_left" default):
        //   bedroom wing on RIGHT (high X), service wing on LEFT (low X)
        private static void LayoutSplitWing(
            List<ZoneGroup> zones,
            double bx, double by, double bw, double bd,
            LayoutResult result, List<string> warnings,
            string wingOrientation, string garagePlacement)
        {
            var all   = zones.SelectMany(z => z.Spaces).ToList();
            int floor = all.Select(s => s.Floor).DefaultIfEmpty(1).Min();
            double sqm = Math.Max(all.Sum(s => Math.Max(s.AreaSqm, 1.0)), 30.0);

            // ── Footprint: wide stem + two diverging wings ────────────────────
            double mm2    = sqm * 1_000_000.0;
            double houseW = SnapG(Math.Sqrt(mm2 * 1.5));
            houseW = Math.Clamp(houseW, STRUCT * 6, bw);
            double houseD = SnapG(mm2 / Math.Max(houseW, STRUCT));
            houseD = Math.Clamp(houseD, STRUCT * 4, bd);

            result.BuildingX = bx;
            result.BuildingY = by;
            result.BuildingW = houseW;
            result.BuildingD = houseD;

            // Stem = front 45%, wings = rear 55%
            double stemD    = Snap(houseD * 0.45, GRID);
            double wingD    = houseD - stemD;
            double gapW     = HALLWAY;                         // light court between wings
            double bedWingW = Snap((houseW - gapW) * 0.55, GRID);
            bedWingW = Math.Max(bedWingW, STRUCT * 3);
            double svcWingW = houseW - gapW - bedWingW;
            svcWingW = Math.Max(svcWingW, STRUCT * 2);

            // "living_left" default: bedroom wing on RIGHT, service on LEFT
            bool livingOnLeft = !string.Equals(wingOrientation, "living_right",
                                               StringComparison.OrdinalIgnoreCase);
            double bedWingX = livingOnLeft ? bx + svcWingW + gapW : bx;
            double svcWingX = livingOnLeft ? bx                   : bx + bedWingW + gapW;
            double gapX     = livingOnLeft ? bx + svcWingW        : bx + bedWingW;

            // ── Classify ──────────────────────────────────────────────────────
            var beds      = all.Where(s => BedTypes.Contains(s.Type))
                               .OrderByDescending(s => s.AreaSqm).ToList();
            var fullBaths = all.Where(s => FullBathTypes.Contains(s.Type)).ToList();
            var halfBaths = all.Where(s => HalfBathTypes.Contains(s.Type)).ToList();
            var closets   = all.Where(s => ClosetTypes.Contains(s.Type)).ToList();
            var entries   = all.Where(s => EntryTypes.Contains(s.Type)).ToList();
            var service   = all.Where(s => ServiceTypes.Contains(s.Type)).ToList();
            var open      = all.Where(s => OpenPlanTypes.Contains(s.Type)).ToList();
            var enclairs  = all.Where(s => EnclaireTypes.Contains(s.Type)).ToList();

            var primaryBed  = beds.FirstOrDefault(b =>
                b.Type is "primary_bedroom" or "primary_suite" or "master_bedroom" ||
                b.Name.IndexOf("Primary", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? beds.FirstOrDefault();
            var primaryBath = fullBaths.FirstOrDefault(b =>
                b.Type is "primary_bath" or "ensuite_bath" or "ensuite" or "master_bath" ||
                b.Name.IndexOf("Primary", StringComparison.OrdinalIgnoreCase) >= 0 ||
                b.Name.IndexOf("En",      StringComparison.OrdinalIgnoreCase) >= 0);
            var primaryWic  = closets.FirstOrDefault();
            var secBeds     = beds.Where(b => b != primaryBed).ToList();
            var remBaths    = fullBaths.Where(b => b != primaryBath).ToList();

            // ── STEM: Open living core (full width) ───────────────────────────
            double stemY  = by;
            double entryH = Math.Max(Snap(stemD * 0.28, GRID), STRUCT * 2);

            var entryRoom = entries.FirstOrDefault() ?? new SpaceNode
            {
                Name = "Entry", Type = "entry", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = true, Facing = "south",
            };
            if (halfBaths.Any())
            {
                double hbW = Snap(houseW * 0.20, GRID);
                double enW = houseW - hbW;
                entryRoom.Facing = "south";
                Place(entryRoom,    bx,       stemY, enW, entryH, result.Rooms);
                halfBaths[0].Facing = "south";
                Place(halfBaths[0], bx + enW, stemY, hbW, entryH, result.Rooms);
            }
            else
            {
                entryRoom.Facing = "south";
                Place(entryRoom, bx, stemY, houseW, entryH, result.Rooms);
            }
            stemY += entryH;

            // Open-plan rooms inside stem — lateral layout (living front, dining+kitchen rear)
            double openStemH = stemD - entryH;
            var lB = open.Where(s => s.Type is "living" or "living_room" or "great_room"
                                     or "family_room" or "open_living").ToList();
            var dB = open.Where(s => s.Type is "dining" or "dining_room"
                                     or "breakfast_nook" or "kitchen_dining").ToList();
            var kB = open.Where(s => s.Type is "kitchen" or "open_kitchen" or "open_plan").ToList();
            var cls = lB.Concat(dB).Concat(kB).ToHashSet();
            lB.AddRange(open.Where(s => !cls.Contains(s)));
            if (!lB.Any() && !dB.Any() && !kB.Any())
            {
                double oa = sqm * 0.30;
                lB.Add(new SpaceNode { Name = "Living Room", Type = "living_room",
                    Floor = floor, HasNaturalLight = true, AreaSqm = oa * 0.40 });
                dB.Add(new SpaceNode { Name = "Dining Room", Type = "dining_room",
                    Floor = floor, HasNaturalLight = true, AreaSqm = oa * 0.25 });
                kB.Add(new SpaceNode { Name = "Kitchen",     Type = "kitchen",
                    Floor = floor, HasNaturalLight = true, AreaSqm = oa * 0.35 });
            }
            SpaceNode? MergeNodes(List<SpaceNode> bucket, string dName, string dType)
            {
                if (!bucket.Any()) return null;
                if (bucket.Count == 1) return bucket[0];
                return new SpaceNode { Name = dName, Type = dType,
                    Floor = bucket[0].Floor, HasNaturalLight = true,
                    AreaSqm = bucket.Sum(r => Math.Max(r.AreaSqm, 1.0)) };
            }
            var lSW = MergeNodes(lB, "Living Room", "living_room");
            var dSW = MergeNodes(dB, "Dining Room", "dining_room");
            var kSW = MergeNodes(kB, "Kitchen",     "kitchen");

            if (lSW != null && (dSW != null || kSW != null))
            {
                double frontH2 = Snap(openStemH * 0.55, GRID);
                frontH2 = Math.Max(frontH2, STRUCT * 2);
                double rearH2  = openStemH - frontH2;
                rearH2  = Math.Max(rearH2, STRUCT * 2);
                frontH2 = openStemH - rearH2;

                lSW.IsOpenPlan = true; lSW.ZoneName = "open_plan";
                lSW.Facing = "south";  lSW.SolarWall = "south";
                Place(lSW, bx, stemY, houseW, frontH2, result.Rooms);
                stemY += frontH2;

                if (dSW != null && kSW != null)
                {
                    double dkTotal = Math.Max(dSW.AreaSqm, 1.0) + Math.Max(kSW.AreaSqm, 1.0);
                    double kitFrac = Math.Clamp(kSW.AreaSqm / dkTotal, 0.40, 0.60);
                    double kitW2   = Snap(houseW * kitFrac, GRID);
                    double dinW2   = houseW - kitW2;
                    kitW2 = Math.Max(kitW2, MIN_DIM); dinW2 = Math.Max(dinW2, MIN_DIM);

                    dSW.IsOpenPlan = true; dSW.ZoneName = "open_plan";
                    dSW.Facing = "north"; dSW.SolarWall = "south";
                    Place(dSW, bx, stemY, dinW2, rearH2, result.Rooms);

                    kSW.IsOpenPlan = true; kSW.ZoneName = "open_plan";
                    kSW.Facing = "north"; kSW.SolarWall = "north";
                    Place(kSW, bx + dinW2, stemY, kitW2, rearH2, result.Rooms);
                }
                else if (kSW != null)
                {
                    kSW.IsOpenPlan = true; kSW.ZoneName = "open_plan";
                    kSW.Facing = "north"; kSW.SolarWall = "north";
                    Place(kSW, bx, stemY, houseW, rearH2, result.Rooms);
                }
                else
                {
                    dSW!.IsOpenPlan = true; dSW.ZoneName = "open_plan";
                    dSW.Facing = "north"; dSW.SolarWall = "south";
                    Place(dSW, bx, stemY, houseW, rearH2, result.Rooms);
                }
            }
            else
            {
                var single = lSW ?? kSW ?? dSW;
                if (single != null)
                {
                    single.IsOpenPlan = true; single.ZoneName = "open_plan";
                    single.Facing = "north"; single.SolarWall = SolarWallForType(N(single.Type));
                    Place(single, bx, stemY, houseW, openStemH, result.Rooms);
                }
            }

            // ── Hallway corridor at wing junction (spans full wing depth − linen) ─
            double linenSW  = 900;
            double hallCorW = wingD - linenSW;
            result.Rooms.Add(new SpaceNode
            {
                Name = "Hallway", Type = "corridor", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = false, Facing = "south",
                X = gapX, Y = by + stemD, WidthMm = gapW, DepthMm = hallCorW,
                AreaSqm = gapW * hallCorW / 1_000_000.0,
            });
            result.Rooms.Add(new SpaceNode
            {
                Name = "Linen", Type = "storage", ZoneName = "circulation",
                Floor = floor, HasNaturalLight = false, Facing = "north",
                X = gapX, Y = by + stemD + hallCorW, WidthMm = gapW, DepthMm = linenSW,
                AreaSqm = gapW * linenSW / 1_000_000.0,
            });

            // ── BEDROOM WING (rear, one side) ────────────────────────────────
            var rawHSW = new List<double>();
            foreach (var _ in secBeds) rawHSW.Add(STRUCT * 3);
            if (primaryBed != null)    rawHSW.Add(STRUCT * 4);
            if (!rawHSW.Any())         rawHSW.Add(STRUCT * 4);
            double rawSumSW = rawHSW.Sum();
            var rowHSW = rawHSW.Select(h => Snap(h * wingD / rawSumSW, GRID)).ToList();
            if (rowHSW.Any())
                rowHSW[^1] = wingD - rowHSW.SkipLast(1).Sum();
            double capPSW = Snap(STRUCT * 4 * 1.15, GRID);
            double capSSW = Snap(STRUCT * 3 * 1.15, GRID);
            for (int i = 0; i < rowHSW.Count; i++)
                rowHSW[i] = Math.Min(rowHSW[i],
                    i == rowHSW.Count - 1 && primaryBed != null ? capPSW : capSSW);

            double bedY2 = by + stemD;
            int riSW = 0;
            for (int i = 0; i < secBeds.Count && riSW < rowHSW.Count; i++, riSW++)
            {
                double rh   = Math.Max(rowHSW[riSW], MIN_DIM);
                var    bath = i < remBaths.Count ? remBaths[i] : null;
                double bw2  = bath != null ? Snap(bedWingW * 0.65, GRID) : bedWingW;
                if (bath != null)
                    bw2 = ClampAspectRatio(bw2, rh, 0.65, 1.35, MIN_DIM, bedWingW - MIN_DIM);
                bw2 = Math.Max(bw2, MIN_DIM);

                secBeds[i].Facing = livingOnLeft ? "east" : "west";
                Place(secBeds[i], bedWingX, bedY2, bw2, rh, result.Rooms);
                if (bath != null)
                {
                    bath.Facing = livingOnLeft ? "east" : "west";
                    Place(bath, bedWingX + bw2, bedY2, bedWingW - bw2, rh, result.Rooms);
                }
                bedY2 += rh;
            }

            if (primaryBed != null && riSW < rowHSW.Count)
            {
                double rh   = Math.Max(rowHSW[riSW], MIN_DIM);
                bool   hasW = primaryWic  != null;
                bool   hasB = primaryBath != null;

                double stdBedW2  = StandardDims(primaryBed.Type, primaryBed.AreaSqm).w;
                double stdExtra2 = (hasW ? StandardDims(primaryWic!.Type,  primaryWic.AreaSqm).w  : 0)
                                 + (hasB ? StandardDims(primaryBath!.Type, primaryBath.AreaSqm).w : 0);
                double bedFrac2  = stdBedW2 + stdExtra2 > 0
                    ? Math.Clamp(stdBedW2 / (stdBedW2 + stdExtra2), 0.40, 0.75)
                    : (hasW && hasB ? 0.55 : hasB ? 0.65 : 1.0);
                double bW   = Snap(bedWingW * bedFrac2, GRID);
                double rem2 = bedWingW - bW;
                string bf   = livingOnLeft ? "east" : "west";

                primaryBed.Facing = bf;
                Place(primaryBed, bedWingX, bedY2, bW, rh, result.Rooms);
                if (hasW && hasB)
                {
                    double ww2 = Snap(rem2 * 0.5, GRID);
                    primaryWic!.Facing = bf; primaryBath!.Facing = bf;
                    Place(primaryWic,  bedWingX + bW,       bedY2, ww2,        rh, result.Rooms);
                    Place(primaryBath, bedWingX + bW + ww2, bedY2, rem2 - ww2, rh, result.Rooms);
                }
                else if (hasB)
                {
                    primaryBath!.Facing = bf;
                    Place(primaryBath, bedWingX + bW, bedY2, rem2, rh, result.Rooms);
                }
                else if (hasW)
                {
                    primaryWic!.Facing = bf;
                    Place(primaryWic, bedWingX + bW, bedY2, rem2, rh, result.Rooms);
                }
                bedY2 += rh;
            }
            if (bedY2 < by + stemD + wingD - MIN_DIM)
            {
                double bonusD2 = by + stemD + wingD - bedY2;
                result.Rooms.Add(new SpaceNode
                {
                    Name = "Storage", Type = "storage", ZoneName = "bedroom_wing",
                    Floor = floor, HasNaturalLight = false, Facing = "north",
                    X = bedWingX, Y = bedY2, WidthMm = bedWingW, DepthMm = bonusD2,
                    AreaSqm = bedWingW * bonusD2 / 1_000_000.0,
                });
            }

            // ── SERVICE WING (rear, opposite side) ───────────────────────────
            var garages2   = service.Where(s => s.Type == "garage").ToList();
            var nonGarSvc  = service.Where(s => s.Type != "garage").ToList();
            bool garageFrt = garages2.Any() &&
                string.Equals(garagePlacement, "front", StringComparison.OrdinalIgnoreCase);
            double svcY    = by + stemD;
            double encH2   = enclairs.Any()
                ? Math.Max(Snap(wingD * 0.35, GRID), STRUCT * 3) : 0;
            double garH2   = garages2.Any()
                ? Math.Max(Snap(wingD * 0.40, GRID), 6096.0) : 0;
            double svcRem  = Math.Max(wingD - encH2 - (garageFrt ? 0 : garH2), 0);

            if (nonGarSvc.Any() && svcRem > MIN_DIM)
            {
                double perW2 = Snap(svcWingW / nonGarSvc.Count, GRID);
                double tmpX  = svcWingX;
                for (int i = 0; i < nonGarSvc.Count; i++)
                {
                    double w = (i == nonGarSvc.Count - 1)
                        ? svcWingX + svcWingW - tmpX : perW2;
                    w = Math.Max(w, MIN_DIM);
                    nonGarSvc[i].Facing = livingOnLeft ? "west" : "east";
                    Place(nonGarSvc[i], tmpX, svcY, w, svcRem, result.Rooms);
                    tmpX += w;
                }
                svcY += svcRem;
            }
            if (enclairs.Any() && encH2 > 0)
            {
                double perW2 = Snap(svcWingW / enclairs.Count, GRID);
                double tmpX  = svcWingX;
                for (int i = 0; i < enclairs.Count; i++)
                {
                    double w = (i == enclairs.Count - 1)
                        ? svcWingX + svcWingW - tmpX : perW2;
                    w = Math.Max(w, MIN_DIM);
                    enclairs[i].Facing = "north"; enclairs[i].HasNaturalLight = true;
                    enclairs[i].ZoneName = "enclair";
                    Place(enclairs[i], tmpX, svcY, w, encH2, result.Rooms);
                    tmpX += w;
                }
                svcY += encH2;
            }
            if (garages2.Any() && garH2 > 0)
            {
                double perW2 = Snap(svcWingW / garages2.Count, GRID);
                double tmpX  = svcWingX;
                for (int i = 0; i < garages2.Count; i++)
                {
                    double w = (i == garages2.Count - 1)
                        ? svcWingX + svcWingW - tmpX : perW2;
                    w = Math.Max(w, MIN_DIM);
                    garages2[i].Facing = "north";
                    if (garages2[i].AreaSqm > 30)
                    {
                        garages2[i].ZoneName = "adu_capable";
                        garages2[i].HasNaturalLight = true;
                    }
                    Place(garages2[i], tmpX, svcY, w, garH2, result.Rooms);
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

        // MISSING-03: return the preferred solar-glazing wall for a room type.
        // Empty string means "use Facing" (no solar override).
        private static string SolarWallForType(string t) => t switch
        {
            "living_room"  or "living"  or "great_room" or "family_room"
                or "open_living"                                              => "south",
            "dining_room"  or "dining"  or "breakfast_nook"                  => "south",
            "kitchen"      or "open_kitchen"                                  => "north",
            "primary_bedroom" or "primary_suite" or "master_bedroom"         => "east",
            "secondary_bedroom" or "bedroom" or "guest_bedroom"
                or "home_office_bedroom"                                      => "east",
            "bathroom" or "primary_bath" or "ensuite_bath" or "ensuite"
                or "master_bath" or "secondary_bath" or "shared_bath"
                or "full_bath"                                                => "north",
            "powder_room"  or "half_bath" or "toilet"                        => "north",
            "entry"        or "foyer"    or "entry_foyer" or "vestibule"      => "south",
            _                                                                  => "",
        };

        // Clamp room width to enforce architectural aspect ratio (W:D) bounds.
        // Returns adjusted width; adjacent room should receive (availableW - returnedW).
        private static double ClampAspectRatio(double w, double d,
            double minRatio, double maxRatio, double minW, double maxW)
        {
            if (d < 1.0) return Math.Clamp(w, minW, maxW);
            double ratio = w / d;
            if (ratio > maxRatio) w = Snap(d * maxRatio, GRID);
            else if (ratio < minRatio) w = Snap(d * minRatio, GRID);
            return Math.Clamp(w, minW, maxW);
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
