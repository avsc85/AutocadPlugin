// DrawingEngine.cs — corrected edition
// Bug fixes applied (see Phase3 layoutfix.md):
//   #7  EnsureStandardLayers() replaces missing DrawLayersFromActions()
//   #8  _siteConstraints field wired via SetSiteConstraints() — no null ref
//   #9  DrawDoorOnRoom: position computed in mm, scaled ONCE to drawing units
//   #10 DrawWindowOnRoom: same single-scale fix
//   #11 Multi-floor: each floor drawn offset above previous by plotDepth + gap
//   #12 Outer perimeter wall drawn on A-WALL-EXTR before room interiors

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.EditorInput;
using Newtonsoft.Json.Linq;
using zHeight.Plugin.Models;
using zHeight.Plugin.Solver;

namespace zHeight.Plugin.Engine
{
    public class DrawingEngine
    {
        private readonly Document _doc;
        private readonly Database _db;
        private readonly Editor   _ed;
        private readonly double   _s;   // mm-to-drawing-unit scale factor

        // Fix #8: instance field wired from ZHeightCommand via SetSiteConstraints()
        private SiteConstraints _siteConstraints = new SiteConstraints();

        // D-01: cache which layers have been created; avoids redundant UpgradeOpen() per variation
        private static readonly HashSet<string> _createdLayers =
            new(StringComparer.OrdinalIgnoreCase);

        // D-01: cache dim style ObjectId across variations so we never re-query DimStyleTable
        private static ObjectId _dimStyleId = ObjectId.Null;

        private const double VariationGapMm     = 12000;
        private const double FLOOR_SEPARATION_MM = 2000; // gap between floor plans (fix #11)

        // Space types that are outdoor/site elements — drawn as open space, not rooms
        private static readonly HashSet<string> OutdoorTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "backyard","back_yard","front_yard","yard","garden","landscape",
                "outdoor","outdoor_space","patio","deck","courtyard_outdoor",
                "driveway","parking","open_space","greenspace","lawn"
            };

        public DrawingEngine(Document doc)
        {
            _doc = doc;
            _db  = doc.Database;
            _ed  = doc.Editor;
            _s   = GetMmScale(_db);

            _ed.WriteMessage($"\n[zHeight] Drawing unit scale: 1mm = {_s} drawing units");
        }

        // Fix #8: called by ZHeightCommand before ExecutePlan
        public void SetSiteConstraints(SiteConstraints site) =>
            _siteConstraints = site;

        public void ExecutePlan(DrawActionPlan plan)
        {
            _ed.WriteMessage(
                $"\n[zHeight] Executing: {plan.ProjectDescription} " +
                $"({plan.Variations.Count} variations)");

            var offsets = CalculateOffsets(plan.Variations);

            try
            {
                foreach (var variation in plan.Variations)
                {
                    _ed.WriteMessage(
                        $"\n[zHeight] Drawing V{variation.VariationId}: {variation.VariationName}");

                    var offset = offsets.GetValueOrDefault(variation.VariationId);
                    DrawVariation(variation, offset);
                }

                // Update extents so ZOOM E finds entities, then zoom
                _db.UpdateExt(false);
                _ed.WriteMessage("\n[zHeight] Done — zooming to layout...");
                _doc.SendStringToExecute("_.ZOOM _E \n_.REGEN\n", true, false, false);
            }
            catch (System.Exception ex)
            {
                _ed.WriteMessage($"\n[zHeight ERROR] Drawing failed: {ex.Message}");
                throw;
            }
        }

        private Dictionary<int, Point3d> CalculateOffsets(List<VariationPlan> variations)
        {
            var offsets = new Dictionary<int, Point3d>();
            double x = 0;
            foreach (var v in variations)
            {
                offsets[v.VariationId] = new Point3d(x, 0, 0);
                // Use plot width (known from site constraints) as the variation slot width.
                // Fallback: area-based estimate, never smaller than 12m.
                double estWidthMm = Math.Max(
                    _siteConstraints.PlotWidthMm,
                    Math.Sqrt(Math.Max(v.TotalAreaSqm, 50)) * 1200);
                x += (estWidthMm + VariationGapMm) * _s;
            }
            return offsets;
        }

        private void DrawVariation(VariationPlan variation, Point3d offset)
        {
            LayoutResult? layout = null;
            bool hasZones = variation.Layout?.Zones?.Count > 0
                         || variation.Properties?.ContainsKey("zones_json") == true;
            if (hasZones)
            {
                try
                {
                    // F-01: prefer typed Layout site constraints; fall back to Properties JSON
                    var typedSite = variation.Layout?.SiteConstraints;
                    if (typedSite != null)
                    {
                        _siteConstraints = new SiteConstraints
                        {
                            PlotWidthMm  = typedSite.PlotWidthMm  ?? _siteConstraints.PlotWidthMm,
                            PlotDepthMm  = typedSite.PlotDepthMm  ?? _siteConstraints.PlotDepthMm,
                            FrontSetback = typedSite.FrontSetbackMm ?? _siteConstraints.FrontSetback,
                            SideSetback  = typedSite.SideSetbackMm  ?? _siteConstraints.SideSetback,
                            RearSetback  = typedSite.RearSetbackMm  ?? _siteConstraints.RearSetback,
                        };
                        _ed.WriteMessage(
                            $"\n[zHeight] Site: {_siteConstraints.PlotWidthMm/1000:F1}m × " +
                            $"{_siteConstraints.PlotDepthMm/1000:F1}m lot");
                    }
                    else if (variation.Properties?.TryGetValue("site_constraints", out var scRaw) == true)
                    {
                        try
                        {
                            var sc = JObject.Parse(scRaw?.ToString() ?? "{}");
                            _siteConstraints = new SiteConstraints
                            {
                                PlotWidthMm  = sc["plot_width_mm"]?.Value<double>()    ?? _siteConstraints.PlotWidthMm,
                                PlotDepthMm  = sc["plot_depth_mm"]?.Value<double>()    ?? _siteConstraints.PlotDepthMm,
                                FrontSetback = sc["front_setback_mm"]?.Value<double>() ?? _siteConstraints.FrontSetback,
                                SideSetback  = sc["side_setback_mm"]?.Value<double>()  ?? _siteConstraints.SideSetback,
                                RearSetback  = sc["rear_setback_mm"]?.Value<double>()  ?? _siteConstraints.RearSetback,
                            };
                            _ed.WriteMessage(
                                $"\n[zHeight] Site: {_siteConstraints.PlotWidthMm/1000:F1}m × " +
                                $"{_siteConstraints.PlotDepthMm/1000:F1}m lot");
                        }
                        catch { /* use default constraints */ }
                    }

                    var zones = ParseZones(variation);
                    // F-01: prefer typed Layout fields; fall back to Properties dict
                    string strat = variation.Layout?.OrganisationStrategy
                        ?? variation.Properties?.GetValueOrDefault("organisation_strategy", "residential")?.ToString()
                        ?? "residential";
                    double gridM = variation.Layout?.StructuralGridM
                        ?? (variation.Properties?.TryGetValue("structural_grid_m", out var gv) == true
                            ? Convert.ToDouble(gv) : 4.0);

                    string wingOrient = variation.Layout?.WingOrientation
                        ?? variation.Properties?.GetValueOrDefault("wing_orientation", "living_left")?.ToString()
                        ?? "living_left";
                    string garagePlac = variation.Layout?.GaragePlacement
                        ?? variation.Properties?.GetValueOrDefault("garage_placement", "rear")?.ToString()
                        ?? "rear";

                    layout = GridLayoutEngine.Layout(
                        zones, strat,
                        _siteConstraints.PlotWidthMm,
                        _siteConstraints.PlotDepthMm,
                        _siteConstraints.FrontSetback,
                        _siteConstraints.SideSetback,
                        _siteConstraints.RearSetback,
                        gridM * 1000,
                        wingOrient,
                        garagePlac);

                    foreach (var w in layout.Warnings)
                        _ed.WriteMessage($"\n[zHeight LAYOUT] {w}");

                    _ed.WriteMessage(
                        $"\n[zHeight] GridLayoutEngine: {layout.Rooms.Count} rooms placed, " +
                        $"building {layout.BuildingW/1000:F1}m × {layout.BuildingD/1000:F1}m, " +
                        $"lot {_siteConstraints.PlotWidthMm/1000:F1}m × {_siteConstraints.PlotDepthMm/1000:F1}m");
                }
                catch (Exception ex)
                {
                    _ed.WriteMessage($"\n[zHeight] Layout engine failed: {ex.Message} — fallback");
                    layout = null;
                }
            }

            using var tr = _db.TransactionManager.StartTransaction();
            try
            {
                var space = (BlockTableRecord)tr.GetObject(
                    _db.CurrentSpaceId, OpenMode.ForWrite);

                EnsureStandardLayers(tr);

                if (layout != null && layout.Rooms.Any())
                {
                    // ── Site plan: full lot boundary + setback zone ──────────────
                    try { DrawLotBoundary(layout, offset, space, tr); }
                    catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] LotBoundary skipped: {ex.Message}"); }
                    try { DrawEntryWalkway(layout, offset, space, tr); }
                    catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] Walkway skipped: {ex.Message}"); }

                    // ── Floor plans — one per floor, offset vertically ───────────
                    // Filter outdoor spaces — they are shown as open site area, not rooms
                    var indoorRooms = layout.Rooms
                        .Where(r => !OutdoorTypes.Contains(r.Type.ToLower()))
                        .ToList();

                    var byFloor = indoorRooms
                        .GroupBy(r => r.Floor)
                        .OrderBy(g => g.Key);

                    double totalSqm = variation.TotalAreaSqm > 0
                        ? variation.TotalAreaSqm
                        : indoorRooms.Sum(r => r.AreaSqm > 0 ? r.AreaSqm
                            : r.WidthMm * r.DepthMm / 1_000_000.0);

                    foreach (var floorGroup in byFloor)
                    {
                        int    floor     = floorGroup.Key;
                        double floorOffY = (floor - 1) *
                            (_siteConstraints.PlotDepthMm + FLOOR_SEPARATION_MM) * _s;
                        var floorOffset  = new Point3d(offset.X, offset.Y + floorOffY, 0);

                        DrawPerimeterWall(layout, floorOffset, space, tr);
                        try { DrawBuildingDimensions(layout, floorOffset, space, tr); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] BuildingDims skipped: {ex.Message}"); }
                        try { DrawFloorLabel(floor, totalSqm, floorOffset, space, tr); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] FloorLabel skipped: {ex.Message}"); }

                        var wallKeys = new HashSet<string>();
                        var wallSegs = new List<(double x1, double y1, double x2, double y2)>();
                        // Wall gaps: (isHorizontalWall, fixedCoordinate, gapStart, gapEnd) in mm
                        var doorGaps = new List<(bool horiz, double wallCoord, double gapStart, double gapEnd)>();
                        var winGaps  = new List<(bool horiz, double wallCoord, double gapStart, double gapEnd)>();
                        int roomsDrawn = 0;

                        // MISSING-01: suppress shared walls between open-plan rooms
                        var openPlanSuppress = ComputeOpenPlanSuppressKeys(floorGroup.ToList());

                        foreach (var room in floorGroup)
                        {
                            try
                            {
                                bool isCorridor = room.Type.Equals(
                                    "corridor", StringComparison.OrdinalIgnoreCase);

                                DrawRoomHatch(room, floorOffset, space, tr);

                                AddWallSeg(wallKeys, wallSegs, room.X,     room.Y,   room.Right, room.Y,   openPlanSuppress);
                                AddWallSeg(wallKeys, wallSegs, room.Right, room.Y,   room.Right, room.Top, openPlanSuppress);
                                AddWallSeg(wallKeys, wallSegs, room.Right, room.Top, room.X,     room.Top, openPlanSuppress);
                                AddWallSeg(wallKeys, wallSegs, room.X,     room.Top, room.X,     room.Y,   openPlanSuppress);

                                if (!isCorridor)
                                {
                                    // Collect door gap BEFORE drawing so wall splitter can use it
                                    var dg = CalcDoorGap(room);
                                    if (dg.HasValue) doorGaps.Add(dg.Value);
                                    try { DrawDoorOnRoom(room, floorOffset, space, tr); }
                                    catch { /* door failure never blocks room */ }
                                }

                                string rt = (room.Type ?? "").ToLowerInvariant()
                                    .Replace('-','_').Replace(' ','_');
                                bool isEnclair = IsEnclaireType(rt);
                                bool noWin = isCorridor || isEnclair || new HashSet<string>
                                {
                                    "toilet","bathroom","bath","corridor","store",
                                    "server_room","utility","mechanical","laundry",
                                    "laundry_room","closet","wic","walk_in_closet",
                                    "powder_room","half_bath","primary_bath",
                                    "ensuite_bath","ensuite","master_bath",
                                    "mudroom","mud_room","pantry","storage","garage"
                                }.Contains(rt);

                                // Enclair: draw glass walls on all 4 sides instead of regular door+window
                                if (isEnclair)
                                    try { DrawEnclaireGlassWalls(room, floorOffset, space, tr); }
                                    catch { /* non-fatal */ }
                                else if (!isCorridor)
                                    { /* door already drawn above */ }

                                if (room.HasNaturalLight && !noWin)
                                {
                                    var wg = CalcWindowGap(room);
                                    if (wg.HasValue) winGaps.Add(wg.Value);
                                    try { DrawWindowOnRoom(room, floorOffset, space, tr); }
                                    catch { /* window failure never blocks room */ }
                                }

                                try { DrawRoomLabel(room, floorOffset, space, tr); }
                                catch { /* label failure never blocks room */ }

                                // MISSING-05: schematic furniture
                                try { DrawRoomFurniture(room, floorOffset, space, tr); }
                                catch { /* furniture failure never blocks room */ }

                                roomsDrawn++;
                            }
                            catch (Exception ex)
                            {
                                _ed.WriteMessage(
                                    $"\n[zHeight WARN] Room '{room.Name}' skipped: {ex.Message}");
                            }
                        }

                        // Draw deduped wall segments — exterior on A-WALL-EXTR (230mm), interior on A-WALL-INTR (150mm)
                        var allWallGaps = doorGaps.Concat(winGaps).ToList();
                        double extMinX = layout.BuildingX;
                        double extMaxX = layout.BuildingX + layout.BuildingW;
                        double extMinY = layout.BuildingY;
                        double extMaxY = layout.BuildingY + layout.BuildingD;
                        const double wallExtTol = 100; // mm tolerance for exterior classification
                        foreach (var (x1, y1, x2, y2) in wallSegs)
                        {
                            string wLayer = IsExteriorWall(x1, y1, x2, y2,
                                                extMinX, extMaxX, extMinY, extMaxY, wallExtTol)
                                ? "A-WALL-EXTR" : "A-WALL-INTR";
                            try { DrawWallSegmentWithGaps(x1, y1, x2, y2, wLayer,
                                      floorOffset, space, tr, allWallGaps); }
                            catch { /* wall seg failure is non-fatal */ }
                        }

                        // MISSING-04: corner cap squares close L/T junction gaps in double-line walls
                        var cornerSet = new HashSet<string>();
                        foreach (var room in floorGroup)
                        {
                            foreach (var (cx, cy) in new[]
                            {
                                (room.X,     room.Y),   (room.Right, room.Y),
                                (room.Right, room.Top), (room.X,     room.Top)
                            })
                            {
                                static long Rc(double v) => (long)Math.Round(v / 10.0) * 10;
                                string ck = $"{Rc(cx)},{Rc(cy)}";
                                if (cornerSet.Add(ck))
                                    try { DrawCornerCap(cx, cy, floorOffset, space, tr); }
                                    catch { /* non-fatal */ }
                            }
                        }

                        _ed.WriteMessage(
                            $"\n[zHeight] Floor {floor}: {roomsDrawn} rooms drawn, " +
                            $"{wallSegs.Count} wall segments");

                        // ── Suite-connecting doors + foyer→hallway door ──────────────
                        try { DrawInteriorConnectingDoors(floorGroup.ToList(), floorOffset, space, tr); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] ConnectingDoors skipped: {ex.Message}"); }

                        // ── Continuous dimension chains (room-by-room) ───────────────
                        try
                        {
                            double chainYmm = layout.BuildingY - 1200;
                            DrawHorizDimChain(floorGroup.ToList(), chainYmm, floorOffset, space, tr);
                            double chainXmm = layout.BuildingX + layout.BuildingW + 1200;
                            DrawVertDimChain(floorGroup.ToList(), chainXmm, floorOffset, space, tr);
                        }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] DimChain skipped: {ex.Message}"); }

                        // VALIDATION-FIX: CHECK-DQ02 — pass NorthAngleDeg so arrow rotates per variation
                        try { DrawNorthArrowLayout(layout, floorOffset, space, tr, variation.NorthAngleDeg); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] NorthArrow skipped: {ex.Message}"); }
                        try { DrawScaleBarLayout(layout, floorOffset, space, tr); }
                        catch (Exception ex) { _ed.WriteMessage($"\n[zHeight] ScaleBar skipped: {ex.Message}"); }
                    }
                }
                else
                {
                    foreach (var action in variation.Actions)
                    {
                        try { ExecuteAction(action, offset, space, tr); }
                        catch (Exception ex)
                        {
                            _ed.WriteMessage($"\n[zHeight WARN] {action.ActionType}: {ex.Message}");
                        }
                    }
                }

                tr.Commit();
            }
            catch
            {
                tr.Abort();
                throw;
            }
        }

        // ── GridLayoutEngine room drawing ─────────────────────────────────────

        // Site plan: full lot boundary (dashed C-PROP) + front setback line + backyard label
        private void DrawLotBoundary(LayoutResult layout, Point3d offset,
                                      BlockTableRecord space, Transaction tr)
        {
            double pw = _siteConstraints.PlotWidthMm  * _s;
            double pd = _siteConstraints.PlotDepthMm  * _s;
            double fs = _siteConstraints.FrontSetback * _s;
            double ss = _siteConstraints.SideSetback  * _s;

            // Outer lot boundary
            var lot = new Polyline();
            lot.AddVertexAt(0, new Point2d(offset.X,      offset.Y),      0, 0, 0);
            lot.AddVertexAt(1, new Point2d(offset.X + pw, offset.Y),      0, 0, 0);
            lot.AddVertexAt(2, new Point2d(offset.X + pw, offset.Y + pd), 0, 0, 0);
            lot.AddVertexAt(3, new Point2d(offset.X,      offset.Y + pd), 0, 0, 0);
            lot.Closed        = true;
            lot.ConstantWidth = 0;
            lot.Layer         = "C-PROP";
            space.AppendEntity(lot);
            tr.AddNewlyCreatedDBObject(lot, true);

            double rs = _siteConstraints.RearSetback * _s;

            // Front setback dashed indicator line
            var fsLine = new Line(
                new Point3d(offset.X,      offset.Y + fs, 0),
                new Point3d(offset.X + pw, offset.Y + fs, 0));
            fsLine.Layer = "C-PROP";
            space.AppendEntity(fsLine);
            tr.AddNewlyCreatedDBObject(fsLine, true);

            // Rear setback line
            var rsLine = new Line(
                new Point3d(offset.X,      offset.Y + pd - rs, 0),
                new Point3d(offset.X + pw, offset.Y + pd - rs, 0));
            rsLine.Layer = "C-PROP";
            space.AppendEntity(rsLine);
            tr.AddNewlyCreatedDBObject(rsLine, true);

            // Left side setback line
            var lsLine = new Line(
                new Point3d(offset.X + ss, offset.Y,      0),
                new Point3d(offset.X + ss, offset.Y + pd, 0));
            lsLine.Layer = "C-PROP";
            space.AppendEntity(lsLine);
            tr.AddNewlyCreatedDBObject(lsLine, true);

            // Right side setback line
            var rsrLine = new Line(
                new Point3d(offset.X + pw - ss, offset.Y,      0),
                new Point3d(offset.X + pw - ss, offset.Y + pd, 0));
            rsrLine.Layer = "C-PROP";
            space.AppendEntity(rsrLine);
            tr.AddNewlyCreatedDBObject(rsrLine, true);

            // Buildable area rectangle (inner dashed boundary)
            var buildable = new Polyline();
            buildable.AddVertexAt(0, new Point2d(offset.X + ss,      offset.Y + fs),      0, 0, 0);
            buildable.AddVertexAt(1, new Point2d(offset.X + pw - ss, offset.Y + fs),      0, 0, 0);
            buildable.AddVertexAt(2, new Point2d(offset.X + pw - ss, offset.Y + pd - rs), 0, 0, 0);
            buildable.AddVertexAt(3, new Point2d(offset.X + ss,      offset.Y + pd - rs), 0, 0, 0);
            buildable.Closed = true;
            buildable.Layer  = "C-PROP";
            space.AppendEntity(buildable);
            tr.AddNewlyCreatedDBObject(buildable, true);

            // "STREET" label below lot boundary
            var streetLabel = new DBText();
            streetLabel.Position   = new Point3d(offset.X + pw / 2 - 1500 * _s,
                                                  offset.Y - 1500 * _s, 0);
            streetLabel.TextString = "STREET";
            streetLabel.Height     = 400 * _s;
            streetLabel.Layer      = "C-PROP";
            space.AppendEntity(streetLabel);
            tr.AddNewlyCreatedDBObject(streetLabel, true);

            // "MAXIMIZED BACKYARD" label in the open space above the building footprint
            double buildTopY = (layout.BuildingY + layout.BuildingD) * _s + offset.Y;
            double byardMid  = buildTopY + (offset.Y + pd - buildTopY) / 2;
            if (byardMid > buildTopY + 600 * _s)
            {
                double bh = 700 * _s;
                var byardLabel = new DBText();
                byardLabel.Position   = new Point3d(
                    offset.X + pw / 2 - 9 * bh * 0.5,  // approximate center for 18-char string
                    byardMid, 0);
                byardLabel.TextString = "MAXIMIZED BACKYARD";
                byardLabel.Height     = bh;
                byardLabel.Layer      = "C-PROP";
                space.AppendEntity(byardLabel);
                tr.AddNewlyCreatedDBObject(byardLabel, true);
            }

            // Lot depth annotation — right side with "126'-0"" style
            double lotGap  = 1200 * _s;
            double lotTick = 300 * _s;
            DrawDimLine(offset.X + pw + lotGap, offset.Y,
                        offset.X + pw + lotGap, offset.Y + pd,
                        lotTick, FtIn(_siteConstraints.PlotDepthMm), true, space, tr);
            // Lot width annotation — top of lot
            DrawDimLine(offset.X, offset.Y + pd + lotGap,
                        offset.X + pw, offset.Y + pd + lotGap,
                        lotTick, FtIn(_siteConstraints.PlotWidthMm), false, space, tr);
        }

        // Building perimeter wall — sized to actual building footprint (not full buildable area)
        private void DrawPerimeterWall(LayoutResult layout, Point3d offset,
                                        BlockTableRecord space, Transaction tr)
        {
            double bx = layout.BuildingX * _s + offset.X;
            double by = layout.BuildingY * _s + offset.Y;
            double bw = layout.BuildingW * _s;
            double bd = layout.BuildingD * _s;

            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(bx,      by),      0, 0, 0);
            pl.AddVertexAt(1, new Point2d(bx + bw, by),      0, 0, 0);
            pl.AddVertexAt(2, new Point2d(bx + bw, by + bd), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(bx,      by + bd), 0, 0, 0);
            pl.Closed        = true;
            pl.ConstantWidth = 230 * _s;
            pl.Layer         = "A-WALL-EXTR";
            space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        private void DrawFloorLabel(int floor, double totalAreaSqm, Point3d offset,
                                     BlockTableRecord space, Transaction tr)
        {
            // Position above the lot boundary (prominent title)
            double pw     = _siteConstraints.PlotWidthMm * _s;
            double pd     = _siteConstraints.PlotDepthMm * _s;
            double labelX = offset.X + pw / 2;
            double labelY = offset.Y + pd + 2400 * _s;

            // Format: "FLOOR PLAN – 1,200 SF"
            int sf = (int)Math.Round(totalAreaSqm * 10.7639);
            string sfStr = sf.ToString("N0"); // e.g. "1,200"
            string floorTitle = floor == 1
                ? $"FLOOR PLAN – {sfStr} SF"
                : $"FLOOR {floor} PLAN – {sfStr} SF";

            double th = 500 * _s;
            var titleTxt = new DBText();
            titleTxt.Position   = new Point3d(labelX - floorTitle.Length * th * 0.28, labelY, 0);
            titleTxt.TextString = floorTitle;
            titleTxt.Height     = th;
            titleTxt.Layer      = "A-ANNO-TTLB";
            space.AppendEntity(titleTxt);
            tr.AddNewlyCreatedDBObject(titleTxt, true);
        }

        private static void AddWallSeg(
            HashSet<string> keys, List<(double, double, double, double)> segs,
            double x1, double y1, double x2, double y2,
            HashSet<string>? suppress = null)
        {
            // Round to 10 mm to absorb floating-point jitter from grid snapping
            static long R(double v) => (long)Math.Round(v / 10.0) * 10;
            var (ax1, ay1, ax2, ay2) = (R(x1), R(y1), R(x2), R(y2));
            string key = ax1 < ax2 || (ax1 == ax2 && ay1 <= ay2)
                ? $"{ax1},{ay1}|{ax2},{ay2}"
                : $"{ax2},{ay2}|{ax1},{ay1}";
            if (suppress?.Contains(key) == true) return;
            if (keys.Add(key))
                segs.Add((x1, y1, x2, y2));
        }

        // 3.4 — classify a wall segment as exterior (on building bounding box) or interior
        private static bool IsExteriorWall(
            double x1, double y1, double x2, double y2,
            double minX, double maxX, double minY, double maxY, double tol)
        {
            bool isHoriz = Math.Abs(y2 - y1) < tol;
            if (isHoriz)
                return Math.Abs(y1 - minY) < tol || Math.Abs(y1 - maxY) < tol;
            else
                return Math.Abs(x1 - minX) < tol || Math.Abs(x1 - maxX) < tol;
        }

        // MISSING-01: canonical wall key (same rounding as AddWallSeg)
        private static string WallKey(double x1, double y1, double x2, double y2)
        {
            static long R(double v) => (long)Math.Round(v / 10.0) * 10;
            var (ax1, ay1, ax2, ay2) = (R(x1), R(y1), R(x2), R(y2));
            return ax1 < ax2 || (ax1 == ax2 && ay1 <= ay2)
                ? $"{ax1},{ay1}|{ax2},{ay2}"
                : $"{ax2},{ay2}|{ax1},{ay1}";
        }

        // MISSING-01: find all wall segments that sit between two open-plan rooms.
        // These segments must be suppressed so the public zone reads as one open space.
        private static HashSet<string> ComputeOpenPlanSuppressKeys(List<SpaceNode> rooms)
        {
            var suppress = new HashSet<string>();
            var op = rooms.Where(r => r.IsOpenPlan).ToList();
            const double tol = 50.0;

            for (int a = 0; a < op.Count; a++)
            for (int b = a + 1; b < op.Count; b++)
            {
                var ra = op[a]; var rb = op[b];

                // Horizontal shared wall: ra.Top ≈ rb.Y (ra is below rb)
                if (Math.Abs(ra.Top - rb.Y) < tol &&
                    ra.X < rb.Right - tol && rb.X < ra.Right - tol)
                {
                    suppress.Add(WallKey(Math.Max(ra.X, rb.X), ra.Top,
                                         Math.Min(ra.Right, rb.Right), ra.Top));
                }
                // Horizontal shared wall: rb.Top ≈ ra.Y (rb is below ra)
                else if (Math.Abs(rb.Top - ra.Y) < tol &&
                         ra.X < rb.Right - tol && rb.X < ra.Right - tol)
                {
                    suppress.Add(WallKey(Math.Max(ra.X, rb.X), ra.Y,
                                         Math.Min(ra.Right, rb.Right), ra.Y));
                }

                // Vertical shared wall: ra.Right ≈ rb.X (ra is left of rb)
                if (Math.Abs(ra.Right - rb.X) < tol &&
                    ra.Y < rb.Top - tol && rb.Y < ra.Top - tol)
                {
                    suppress.Add(WallKey(ra.Right, Math.Max(ra.Y, rb.Y),
                                         ra.Right, Math.Min(ra.Top, rb.Top)));
                }
                // Vertical shared wall: rb.Right ≈ ra.X (rb is left of ra)
                else if (Math.Abs(rb.Right - ra.X) < tol &&
                         ra.Y < rb.Top - tol && rb.Y < ra.Top - tol)
                {
                    suppress.Add(WallKey(ra.X, Math.Max(ra.Y, rb.Y),
                                         ra.X, Math.Min(ra.Top, rb.Top)));
                }
            }
            return suppress;
        }

        // Draw a double-line wall at true architectural thickness:
        //   A-WALL-EXTR → 230 mm total (115 mm each side of centreline)
        //   A-WALL-INTR → 150 mm total (75 mm each side)
        private void DrawWallSegment(double x1, double y1, double x2, double y2,
                                      string layer, Point3d offset,
                                      BlockTableRecord space, Transaction tr)
        {
            double ht  = layer == "A-WALL-EXTR" ? 115.0 : 75.0; // half-thickness in mm
            double dx  = x2 - x1;
            double dy  = y2 - y1;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1.0) return;

            // Unit normal perpendicular to wall direction
            double nx = -dy / len * ht;
            double ny =  dx / len * ht;

            DrawLineRaw(x1 + nx, y1 + ny, x2 + nx, y2 + ny, layer, offset, space, tr);
            DrawLineRaw(x1 - nx, y1 - ny, x2 - nx, y2 - ny, layer, offset, space, tr);
        }

        // Raw single-line primitive (drawing-unit coordinates after mm scale)
        private void DrawLineRaw(double x1, double y1, double x2, double y2,
                                  string layer, Point3d offset,
                                  BlockTableRecord space, Transaction tr)
        {
            var line = new Line(
                new Point3d(x1 * _s + offset.X, y1 * _s + offset.Y, 0),
                new Point3d(x2 * _s + offset.X, y2 * _s + offset.Y, 0));
            line.Layer = layer;
            space.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        // Returns the door opening gap for a room's primary door (all values in mm).
        // horiz=true  → gap is on a horizontal wall (bottom or top of room)
        // horiz=false → gap is on a vertical   wall (left or right of room)
        // wallCoord   → the fixed coordinate of the wall (y for horiz, x for vert)
        // gapStart/End → the extent of the opening along the wall's variable axis
        private static (bool horiz, double wallCoord, double gapStart, double gapEnd)?
            CalcDoorGap(SpaceNode room)
        {
            double dw = GetDoorWidth(room.Type);
            double hw = room.WidthMm / 2;
            double hd = room.DepthMm / 2;
            switch (room.Facing)
            {
                case "south": return (true,  room.Y,     room.X + hw - dw / 2, room.X + hw + dw / 2);
                case "north": return (true,  room.Top,   room.X + hw - dw / 2, room.X + hw + dw / 2);
                case "east":  return (false, room.Right, room.Y + hd - dw / 2, room.Y + hd + dw / 2);
                case "west":  return (false, room.X,     room.Y + hd - dw / 2, room.Y + hd + dw / 2);
                default:      return (true,  room.Y,     room.X + hw - dw / 2, room.X + hw + dw / 2);
            }
        }

        // 3.1 — room-type window profile: (wall fraction, max width mm, high-sill flag, two-window flag)
        private static (double frac, double maxMm, bool highSill, bool twoWin)
            GetWindowProfile(string rawType)
        {
            string t = (rawType ?? "").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            return t switch {
                "living_room" or "living" or "great_room" or "family_room" or "open_living"
                                                                => (0.65, 3600, false, false),
                "dining_room" or "dining" or "breakfast_nook"   => (0.50, 2400, false, false),
                "kitchen"     or "open_kitchen"                 => (0.35, 1800, false, true),
                "primary_bedroom" or "primary_suite" or "master_bedroom"
                                                                => (0.45, 1800, false, false),
                "secondary_bedroom" or "bedroom" or "guest_bedroom" or "guest_room"
                 or "home_office_bedroom" or "nursery"          => (0.40, 1500, false, false),
                "bathroom" or "primary_bath" or "ensuite_bath" or "ensuite"
                 or "master_bath" or "secondary_bath" or "shared_bath" or "full_bath"
                                                                => (0.25,  600,  true, false),
                "entry" or "foyer" or "entry_foyer" or "vestibule"
                                                                => (0.35, 1500, false, false),
                _                                               => (0.45, 1800, false, false),
            };
        }

        // Window opening gap — same coordinate system as CalcDoorGap (all mm).
        // MISSING-03: uses SolarWall when set, otherwise falls back to Facing.
        private static (bool horiz, double wallCoord, double gapStart, double gapEnd)?
            CalcWindowGap(SpaceNode room)
        {
            string wall = string.IsNullOrEmpty(room.SolarWall) ? room.Facing : room.SolarWall;
            var (frac, maxMm, _, _) = GetWindowProfile(room.Type);
            bool   horiz   = wall is "south" or "north";
            double wallDim = horiz ? room.WidthMm : room.DepthMm;
            double winW    = Math.Clamp(wallDim * frac, 300, maxMm);
            double center  = horiz ? room.X + room.WidthMm / 2 : room.Y + room.DepthMm / 2;
            switch (wall)
            {
                case "north": return (true,  room.Top,   center - winW / 2, center + winW / 2);
                case "east":  return (false, room.Right, center - winW / 2, center + winW / 2);
                case "west":  return (false, room.X,     center - winW / 2, center + winW / 2);
                default:      return (true,  room.Y,     center - winW / 2, center + winW / 2);
            }
        }

        // Draws a wall segment while leaving gaps at door opening positions.
        // For non-gapped segments, delegates to DrawWallSegment (double-line).
        private void DrawWallSegmentWithGaps(
            double x1, double y1, double x2, double y2,
            string layer, Point3d offset, BlockTableRecord space, Transaction tr,
            List<(bool horiz, double wallCoord, double gapStart, double gapEnd)> gaps)
        {
            bool   isHoriz = Math.Abs(y2 - y1) < 1.0;
            double coord   = isHoriz ? y1 : x1;
            const double tol = 50; // mm matching tolerance

            // Collect gaps that apply to this specific wall segment
            var applicable = new List<(double start, double end)>();
            foreach (var (gh, gwc, gs, ge) in gaps)
            {
                if (gh != isHoriz) continue;
                if (Math.Abs(gwc - coord) > tol) continue;
                double segStart = isHoriz ? x1 : y1;
                double segEnd   = isHoriz ? x2 : y2;
                if (ge < segStart + tol || gs > segEnd - tol) continue;
                applicable.Add((Math.Max(gs, segStart), Math.Min(ge, segEnd)));
            }

            if (applicable.Count == 0)
            {
                DrawWallSegment(x1, y1, x2, y2, layer, offset, space, tr);
                return;
            }

            applicable.Sort((a, b) => a.start.CompareTo(b.start));

            double cur = isHoriz ? x1 : y1;
            double end = isHoriz ? x2 : y2;

            foreach (var (gs, ge) in applicable)
            {
                if (gs > cur + 10) // draw segment before this gap
                {
                    if (isHoriz) DrawWallSegment(cur, y1, gs, y2, layer, offset, space, tr);
                    else         DrawWallSegment(x1, cur, x2, gs, layer, offset, space, tr);
                }
                cur = ge; // advance past the gap
            }

            if (end > cur + 10) // draw tail after last gap
            {
                if (isHoriz) DrawWallSegment(cur, y1, end, y2, layer, offset, space, tr);
                else         DrawWallSegment(x1, cur, x2, end, layer, offset, space, tr);
            }
        }

        // 3.2 — return arc (startAngle, endAngle) that minimises obstruction for the room type.
        // Arc sweeps CCW in AutoCAD: 0=east, PI/2=north, PI=west, 3PI/2=south.
        private static (double start, double end) DoorSwingRule(string rawType, string facing)
        {
            string t = (rawType ?? "").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            bool isBath  = t is "bathroom" or "primary_bath" or "ensuite_bath" or "ensuite"
                        or "master_bath" or "secondary_bath" or "shared_bath" or "full_bath"
                        or "powder_room" or "half_bath";
            bool isEntry = t is "entry" or "foyer" or "entry_foyer" or "vestibule";
            switch (facing)
            {
                case "south":
                    if (isEntry) return (Math.PI / 2, Math.PI);         // entry: hinge right, opens left (N-American)
                    if (isBath)  return (Math.PI, 3 * Math.PI / 2);     // bath: swings away from vanity
                    return (0, Math.PI / 2);                            // default: swings right into room
                case "north":
                    if (isBath)  return (0, Math.PI / 2);               // bath: swings away from fixtures
                    return (Math.PI, 3 * Math.PI / 2);                  // default: swings left into room
                case "east":
                    if (isBath)  return (Math.PI, 3 * Math.PI / 2);     // bath: swings downward
                    return (Math.PI / 2, Math.PI);                      // default: swings upward into room
                case "west":
                    if (isBath)  return (3 * Math.PI / 2, 2 * Math.PI); // bath: swings downward
                    return (0, Math.PI / 2);                            // default: swings upward into room
                default:
                    return (0, Math.PI / 2);
            }
        }

        // Fix #9: all door geometry calculated in mm, scaled ONCE at Point3d
        private void DrawDoorOnRoom(SpaceNode room, Point3d offset,
                                     BlockTableRecord space, Transaction tr)
        {
            double doorW_mm = GetDoorWidth(room.Type);
            double hw_mm    = room.WidthMm / 2;
            double hd_mm    = room.DepthMm / 2;

            double px_mm, py_mm;
            switch (room.Facing)
            {
                case "north":
                    px_mm = room.X + hw_mm - doorW_mm / 2;
                    py_mm = room.Top;
                    break;
                case "east":
                    px_mm = room.Right;
                    py_mm = room.Y + hd_mm - doorW_mm / 2;
                    break;
                case "west":
                    px_mm = room.X;
                    py_mm = room.Y + hd_mm - doorW_mm / 2;
                    break;
                default: // south
                    px_mm = room.X + hw_mm - doorW_mm / 2;
                    py_mm = room.Y;
                    break;
            }

            var    pos      = new Point3d(px_mm * _s + offset.X, py_mm * _s + offset.Y, 0);
            double doorW_du = doorW_mm * _s;

            // Door leaf and arc — swing direction from DoorSwingRule (architectural convention)
            var (swingStart, swingEnd) = DoorSwingRule(room.Type, room.Facing);
            Line leaf; Arc arc;
            switch (room.Facing)
            {
                case "north":
                    leaf = new Line(pos, new Point3d(pos.X + doorW_du, pos.Y, 0));
                    arc  = new Arc(pos, doorW_du, swingStart, swingEnd);
                    break;
                case "east":
                    leaf = new Line(pos, new Point3d(pos.X, pos.Y + doorW_du, 0));
                    arc  = new Arc(pos, doorW_du, swingStart, swingEnd);
                    break;
                case "west":
                    leaf = new Line(pos, new Point3d(pos.X, pos.Y + doorW_du, 0));
                    arc  = new Arc(pos, doorW_du, swingStart, swingEnd);
                    break;
                default: // south
                    leaf = new Line(pos, new Point3d(pos.X + doorW_du, pos.Y, 0));
                    arc  = new Arc(pos, doorW_du, swingStart, swingEnd);
                    break;
            }
            leaf.Layer = "A-DOOR";
            space.AppendEntity(leaf);
            tr.AddNewlyCreatedDBObject(leaf, true);
            arc.Layer = "A-DOOR-SWNG";
            space.AppendEntity(arc);
            tr.AddNewlyCreatedDBObject(arc, true);
        }

        // 3.1 — type-aware window placement: correct proportions per room, kitchen gets two windows
        private void DrawWindowOnRoom(SpaceNode room, Point3d offset,
                                       BlockTableRecord space, Transaction tr)
        {
            string wall  = string.IsNullOrEmpty(room.SolarWall) ? room.Facing : room.SolarWall;
            var (frac, maxMm, _, twoWin) = GetWindowProfile(room.Type);
            bool   horiz   = wall is "south" or "north";
            double wallDim = horiz ? room.WidthMm : room.DepthMm;
            double winW_mm = Math.Clamp(wallDim * frac, 300, maxMm);
            double center  = horiz ? room.X + room.WidthMm / 2 : room.Y + room.DepthMm / 2;

            if (twoWin)
            {
                // Kitchen: two windows flanking the sink zone (centre gap = 10% of wall)
                double hw   = winW_mm * 0.45;
                double half = wallDim * 0.05; // half the centre gap
                DrawWindowGlyph(wall, room, center - half - hw, hw * 2, offset, space, tr);
                DrawWindowGlyph(wall, room, center + half,      hw * 2, offset, space, tr);
            }
            else
            {
                DrawWindowGlyph(wall, room, center - winW_mm / 2, winW_mm, offset, space, tr);
            }
        }

        // Draws the 3-line window glyph on the specified wall.
        // alongStart = start position along the wall's variable axis (X for horiz, Y for vert).
        private void DrawWindowGlyph(string wall, SpaceNode room,
            double alongStart, double winW_mm,
            Point3d offset, BlockTableRecord space, Transaction tr)
        {
            bool   horiz     = wall is "south" or "north";
            double wallCoord = wall switch {
                "north" => room.Top,   "east" => room.Right,
                "west"  => room.X,     _      => room.Y,
            };
            for (int i = 0; i < 3; i++)
            {
                double off_du = i * 50 * _s;
                Line line;
                if (horiz)
                    line = new Line(
                        new Point3d(alongStart            * _s + offset.X, wallCoord * _s + offset.Y + off_du, 0),
                        new Point3d((alongStart + winW_mm) * _s + offset.X, wallCoord * _s + offset.Y + off_du, 0));
                else
                    line = new Line(
                        new Point3d(wallCoord * _s + offset.X + off_du, alongStart            * _s + offset.Y, 0),
                        new Point3d(wallCoord * _s + offset.X + off_du, (alongStart + winW_mm) * _s + offset.Y, 0));
                line.Layer = "A-GLAZ";
                space.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
            }
        }

        // ── MISSING-04: corner cap — fills the pocket gap at L/T/cross junctions ──
        private void DrawCornerCap(double cx, double cy, Point3d offset,
            BlockTableRecord space, Transaction tr)
        {
            const double ht = 75.0; // half internal-wall thickness (mm)
            double x1 = (cx - ht) * _s + offset.X;
            double x2 = (cx + ht) * _s + offset.X;
            double y1 = (cy - ht) * _s + offset.Y;
            double y2 = (cy + ht) * _s + offset.Y;
            var cap = new Polyline();
            cap.AddVertexAt(0, new Point2d(x1, y1), 0, 0, 0);
            cap.AddVertexAt(1, new Point2d(x2, y1), 0, 0, 0);
            cap.AddVertexAt(2, new Point2d(x2, y2), 0, 0, 0);
            cap.AddVertexAt(3, new Point2d(x1, y2), 0, 0, 0);
            cap.Closed        = true;
            cap.ConstantWidth = 0;
            cap.Layer         = "A-WALL-INTR";
            space.AppendEntity(cap);
            tr.AddNewlyCreatedDBObject(cap, true);
        }

        // ── MISSING-05: schematic furniture ──────────────────────────────────────

        private void DrawRoomFurniture(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr)
        {
            string rt = (room.Type ?? "").ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
            switch (rt)
            {
                case "living_room" or "living" or "great_room" or "family_room" or "open_living":
                    DrawLivingFurniture(room, offset, space, tr); break;
                case "dining_room" or "dining" or "kitchen_dining" or "breakfast_nook":
                    DrawDiningFurniture(room, offset, space, tr); break;
                case "kitchen" or "open_kitchen":
                    DrawKitchenFurniture(room, offset, space, tr); break;
                case "primary_bedroom" or "primary_suite" or "master_bedroom":
                    DrawBedFurniture(room, offset, space, tr, king: true); break;
                case "secondary_bedroom" or "bedroom" or "guest_bedroom" or "guest_room"
                  or "home_office_bedroom" or "nursery":
                    DrawBedFurniture(room, offset, space, tr, king: false); break;
                case "bathroom" or "bath" or "primary_bath" or "ensuite_bath" or "ensuite"
                  or "master_bath" or "secondary_bath" or "shared_bath" or "full_bath":
                    DrawBathFurniture(room, offset, space, tr, full: true); break;
                case "powder_room" or "half_bath" or "toilet":
                    DrawBathFurniture(room, offset, space, tr, full: false); break;
            }
        }

        // Draws a schematic rectangle on A-FURN layer (all coords in mm, room-relative origin)
        private void DrawFurnRect(double rx, double ry, double rw, double rd,
            Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var pl = new Polyline();
            pl.AddVertexAt(0, new Point2d(rx        * _s + offset.X, ry        * _s + offset.Y), 0, 0, 0);
            pl.AddVertexAt(1, new Point2d((rx + rw) * _s + offset.X, ry        * _s + offset.Y), 0, 0, 0);
            pl.AddVertexAt(2, new Point2d((rx + rw) * _s + offset.X, (ry + rd) * _s + offset.Y), 0, 0, 0);
            pl.AddVertexAt(3, new Point2d(rx        * _s + offset.X, (ry + rd) * _s + offset.Y), 0, 0, 0);
            pl.Closed        = true;
            pl.ConstantWidth = 0;
            pl.Layer         = "A-FURN";
            space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        // Draws a schematic circle on A-FURN (chair seat, appliance symbol)
        private void DrawFurnCircle(double cx, double cy, double r,
            Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var c = new Circle(
                new Point3d(cx * _s + offset.X, cy * _s + offset.Y, 0),
                Vector3d.ZAxis, r * _s);
            c.Layer = "A-FURN";
            space.AppendEntity(c);
            tr.AddNewlyCreatedDBObject(c, true);
        }

        // Sofa + coffee table + TV-wall marker along the south wall of the living room
        private void DrawLivingFurniture(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr)
        {
            const double clearance = 150.0; // mm clearance from walls
            double sofaW = Math.Min(2500.0, room.WidthMm - 2 * clearance);
            double sofaD = 900.0;
            if (sofaW < 1200 || room.DepthMm < sofaD + 1200) return;

            // Sofa: centred horizontally, placed near the south wall
            double sofaX = room.X + (room.WidthMm - sofaW) / 2;
            double sofaY = room.Y + clearance;
            DrawFurnRect(sofaX, sofaY, sofaW, sofaD, offset, space, tr);

            // Coffee table: centred in front of sofa
            double ctW = Math.Min(1200.0, sofaW * 0.55);
            double ctD = 600.0;
            DrawFurnRect(sofaX + (sofaW - ctW) / 2, sofaY + sofaD + 300, ctW, ctD, offset, space, tr);

            // TV wall indicator: thick line on the north wall, centred
            double tvW = Math.Min(1600.0, room.WidthMm * 0.40);
            double tvY = room.Top - clearance - 75;
            DrawFurnRect(room.X + (room.WidthMm - tvW) / 2, tvY, tvW, 75, offset, space, tr);
        }

        // Dining table + 4 chairs (2 per long side)
        private void DrawDiningFurniture(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr)
        {
            const double chairW = 460.0, chairD = 460.0;
            double tableW = Math.Min(1200.0, room.WidthMm  - 800);
            double tableD = Math.Min(900.0,  room.DepthMm  - 800);
            if (tableW < 800 || tableD < 600) return;

            // Table centred in room
            double tx = room.X + (room.WidthMm - tableW) / 2;
            double ty = room.Y + (room.DepthMm - tableD) / 2;
            DrawFurnRect(tx, ty, tableW, tableD, offset, space, tr);

            // 2 chairs on south side
            double chairGap = tableW / 3;
            for (int i = 0; i < 2; i++)
                DrawFurnRect(tx + chairGap * (i + 0.25), ty - chairD - 150,
                             chairW, chairD, offset, space, tr);
            // 2 chairs on north side
            for (int i = 0; i < 2; i++)
                DrawFurnRect(tx + chairGap * (i + 0.25), ty + tableD + 150,
                             chairW, chairD, offset, space, tr);
        }

        // Kitchen: perimeter counters (600 mm deep) + island if room is wide enough
        private void DrawKitchenFurniture(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr)
        {
            const double counterD = 600.0;
            const double clearance = 150.0;

            // South counter (base cabinets along south wall)
            DrawFurnRect(room.X + clearance, room.Y + clearance,
                         room.WidthMm - 2 * clearance, counterD, offset, space, tr);

            // East counter (if room deep enough)
            if (room.DepthMm > 2400)
                DrawFurnRect(room.Right - clearance - counterD, room.Y + clearance,
                             counterD, room.DepthMm * 0.55, offset, space, tr);

            // Sink symbol on south counter centre
            double sinkX = room.X + room.WidthMm / 2 - 300;
            DrawFurnCircle(sinkX + 200, room.Y + clearance + counterD * 0.5,
                           150, offset, space, tr);

            // Island: room > 10 sqm with 1200mm clearance on all sides (work triangle rule)
            if (room.AreaSqm >= 10.0 && room.WidthMm >= 2700 && room.DepthMm >= 2700)
            {
                double islandW = Math.Min(1500.0, room.WidthMm - 2 * (counterD + 900));
                double islandD = 900.0;
                if (islandW >= 900)
                {
                    double islandX = room.X + (room.WidthMm - islandW) / 2;
                    double islandY = room.Y + counterD + clearance + 1000;
                    DrawFurnRect(islandX, islandY, islandW, islandD, offset, space, tr);
                }
            }
        }

        // Bed (king or queen) + 2 side tables — placed against the wall OPPOSITE the door (Facing wall).
        // This matches the architectural convention: door opens into the room, bed faces it.
        private void DrawBedFurniture(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr, bool king)
        {
            double bedW = king ? 1930.0 : 1524.0;
            double bedD = king ? 2030.0 : 2032.0;
            const double stW = 600.0, stD = 450.0;
            const double clearance = 200.0;

            if (room.WidthMm < bedW + 2 * clearance || room.DepthMm < bedD + 600) return;

            double bedX, bedY;
            switch (room.Facing)
            {
                case "north": // door on north → bed against south wall
                    bedX = room.X + (room.WidthMm - bedW) / 2;
                    bedY = room.Y + clearance;
                    break;
                case "east": // door on east → bed head against west wall, centred N-S
                    bedX = room.X + clearance;
                    bedY = room.Y + (room.DepthMm - bedD) / 2;
                    break;
                case "west": // door on west → bed head against east wall, centred N-S
                    bedX = room.Right - clearance - bedW;
                    bedY = room.Y + (room.DepthMm - bedD) / 2;
                    break;
                default: // south (most common): door on south → bed against north wall
                    bedX = room.X + (room.WidthMm - bedW) / 2;
                    bedY = room.Top - clearance - bedD;
                    break;
            }
            DrawFurnRect(bedX, bedY, bedW, bedD, offset, space, tr);

            // Side tables flanking the bed's long sides
            bool bedRunsEW = room.Facing is "east" or "west";
            if (bedRunsEW)
            {
                // Bed is against E or W wall — side tables above and below (in Y)
                DrawFurnRect(bedX + (bedW - stW) / 2, bedY - stD - 100,    stW, stD, offset, space, tr);
                DrawFurnRect(bedX + (bedW - stW) / 2, bedY + bedD + 100,   stW, stD, offset, space, tr);
            }
            else if (bedX - room.X > stW + clearance)
            {
                // Bed is against N or S wall — side tables left and right (in X)
                DrawFurnRect(bedX - stW - 100,  bedY + (bedD - stD) / 2, stW, stD, offset, space, tr);
                DrawFurnRect(bedX + bedW + 100, bedY + (bedD - stD) / 2, stW, stD, offset, space, tr);
            }
        }

        // Bathroom furniture: toilet + vanity + shower or tub
        private void DrawBathFurniture(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr, bool full)
        {
            const double clearance = 150.0;
            bool narrow = room.WidthMm < 1800;

            // Toilet (460×700) in the corner farthest from door (north-east)
            double toiletX = room.Right - clearance - 460;
            double toiletY = room.Top   - clearance - 700;
            DrawFurnRect(toiletX, toiletY, 460, 700, offset, space, tr);

            // Vanity (900×500) along south wall
            double vanW = Math.Min(900.0, room.WidthMm - 2 * clearance);
            if (vanW >= 450)
                DrawFurnRect(room.X + clearance, room.Y + clearance, vanW, 500, offset, space, tr);

            if (!full) return;

            // Shower or tub
            if (room.DepthMm >= 2800 && room.WidthMm >= 2200)
            {
                // Tub (1525×760) along east wall
                DrawFurnRect(room.Right - clearance - 760, room.Y + clearance,
                             760, 1525, offset, space, tr);
            }
            else if (!narrow)
            {
                // Shower stall (900×900) in north-west corner
                DrawFurnRect(room.X + clearance, room.Top - clearance - 900,
                             900, 900, offset, space, tr);
            }
        }

        private void DrawRoomLabel(SpaceNode room, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            double cx_du = (room.X + room.WidthMm / 2) * _s + offset.X;
            double cy_du = (room.Y + room.DepthMm / 2) * _s + offset.Y;
            double h     = 220 * _s;
            string dimStr = $"{FtIn(room.WidthMm)} x {FtIn(room.DepthMm)}";

            // Room name — centred by offsetting left by half estimated text width
            var name = new DBText();
            name.Position   = new Point3d(cx_du - room.Name.Length * h * 0.3, cy_du + h * 0.6, 0);
            name.TextString = room.Name.ToUpper();
            name.Height     = h;
            name.Layer      = "A-ANNO-TEXT";
            space.AppendEntity(name);
            tr.AddNewlyCreatedDBObject(name, true);

            // Dimension line below name
            var dims = new DBText();
            dims.Position   = new Point3d(cx_du - dimStr.Length * h * 0.25, cy_du - h * 0.6, 0);
            dims.TextString = dimStr;
            dims.Height     = h * 0.75;
            dims.Layer      = "A-ANNO-TEXT";
            space.AppendEntity(dims);
            tr.AddNewlyCreatedDBObject(dims, true);
        }

        // Convert mm to US architectural feet-inches (rounded to nearest 6 inches)
        private static string FtIn(double mm)
        {
            double totalIn = mm / 25.4;
            int ft = (int)(totalIn / 12);
            double rem = totalIn - ft * 12;
            int sixths = (int)Math.Round(rem / 6.0) * 6;
            if (sixths >= 12) { ft++; sixths = 0; }
            return sixths == 0 ? $"{ft}'-0\"" : $"{ft}'-{sixths}\"";
        }

        // Hatch fill — pattern chosen by room type for architectural readability
        private void DrawRoomHatch(SpaceNode room, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            string rt = (room.Type ?? "").ToLowerInvariant().Replace('-','_').Replace(' ','_');

            // No hatch for open/circulation spaces and enclair (glass rooms) — leave white
            if (rt is "corridor" or "hallway" or "open_plan" or "living" or "dining"
                    or "great_room" or "family_room" or "living_room" or "dining_room") return;
            if (IsEnclaireType(rt)) return;

            // Choose pattern by room type
            string pattern;
            double scale;
            double angle;
            switch (rt)
            {
                case "bathroom" or "bath" or "primary_bath" or "ensuite_bath" or "ensuite"
                  or "master_bath" or "secondary_bath" or "shared_bath" or "full_bath"
                  or "powder_room" or "half_bath" or "toilet" or "kitchen":
                    pattern = "NET";    scale = 20.0 * Math.Max(_s, 1.0); angle = 0;           break;
                case "garage" or "utility" or "mechanical" or "laundry" or "laundry_room"
                  or "mudroom" or "mud_room" or "storage" or "pantry":
                    pattern = "ANSI37"; scale = 50.0 * Math.Max(_s, 1.0); angle = 0;           break;
                default: // bedrooms, entry, closets, etc.
                    pattern = "ANSI31"; scale = 75.0 * Math.Max(_s, 1.0); angle = Math.PI / 4; break;
            }

            try
            {
                // AutoCAD .NET hatch rule: add hatch to DB FIRST, then set pattern,
                // then add boundary, then AppendLoop — any other order risks eNotInDatabase.
                var hatch = new Hatch();
                space.AppendEntity(hatch);
                tr.AddNewlyCreatedDBObject(hatch, true);

                hatch.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                hatch.PatternScale = scale;
                hatch.PatternAngle = angle;
                hatch.Layer        = "A-WALL-PATT";
                hatch.Associative  = false; // non-associative is more reliable cross-version

                // Inset by 75mm (half of 150mm internal wall) so hatch fills net interior only
                const double HatchInsetMm = 75.0;
                double ins = HatchInsetMm * _s;
                double hx1 = room.X     * _s + offset.X + ins;
                double hx2 = room.Right * _s + offset.X - ins;
                double hy1 = room.Y     * _s + offset.Y + ins;
                double hy2 = room.Top   * _s + offset.Y - ins;
                // Guard: skip hatch for rooms too small to have a net interior
                if (hx2 - hx1 < 100 * _s || hy2 - hy1 < 100 * _s) return;

                var boundary = new Polyline();
                boundary.AddVertexAt(0, new Point2d(hx1, hy1), 0, 0, 0);
                boundary.AddVertexAt(1, new Point2d(hx2, hy1), 0, 0, 0);
                boundary.AddVertexAt(2, new Point2d(hx2, hy2), 0, 0, 0);
                boundary.AddVertexAt(3, new Point2d(hx1, hy2), 0, 0, 0);
                boundary.Closed = true;
                boundary.Layer  = "A-WALL-PATT";
                space.AppendEntity(boundary);
                tr.AddNewlyCreatedDBObject(boundary, true);

                hatch.AppendLoop(HatchLoopTypes.Default,
                                 new ObjectIdCollection { boundary.ObjectId });
                hatch.EvaluateHatch(true);
            }
            catch (Exception ex)
            {
                // Hatch is optional visual fill — never let it crash the drawing
                _ed.WriteMessage($"\n[zHeight] Hatch skipped for {room.Name}: {ex.Message}");
            }
        }

        // Per-variation north arrow — positioned right of building, relative to this variation's offset
        // VALIDATION-FIX: CHECK-DQ02 — north arrow rotated by northAngleDeg from variation plan
        private void DrawNorthArrowLayout(LayoutResult layout, Point3d offset,
                                          BlockTableRecord space, Transaction tr,
                                          double northAngleDeg = 0.0)
        {
            double cx = (layout.BuildingX + layout.BuildingW + 2400) * _s + offset.X;
            double cy = (layout.BuildingY + layout.BuildingD * 0.80) * _s + offset.Y;
            double r  = 300 * _s;

            // Rotate arrow direction by northAngleDeg (0° = up, clockwise positive per survey)
            double rad = northAngleDeg * Math.PI / 180.0;
            double dx  =  Math.Sin(rad) * r;
            double dy  =  Math.Cos(rad) * r;

            var circle = new Circle(new Point3d(cx, cy, 0), Vector3d.ZAxis, r);
            circle.Layer = "A-ANNO-SYMB";
            space.AppendEntity(circle); tr.AddNewlyCreatedDBObject(circle, true);

            var arrow = new Line(
                new Point3d(cx, cy, 0),
                new Point3d(cx + dx, cy + dy, 0));
            arrow.Layer = "A-ANNO-SYMB";
            space.AppendEntity(arrow); tr.AddNewlyCreatedDBObject(arrow, true);

            // "N" label at arrow tip
            var nTxt = new DBText();
            nTxt.Position   = new Point3d(cx + dx - 100 * _s, cy + dy + 80 * _s, 0);
            nTxt.TextString = "N";
            nTxt.Height     = 200 * _s;
            nTxt.Layer      = "A-ANNO-SYMB";
            space.AppendEntity(nTxt); tr.AddNewlyCreatedDBObject(nTxt, true);
        }

        // Per-variation scale bar — positioned below-right of building
        // VALIDATION-FIX: CHECK-DQ03 — graduated 0-5-10m scale bar with mid-tick
        private void DrawScaleBarLayout(LayoutResult layout, Point3d offset,
                                        BlockTableRecord space, Transaction tr)
        {
            double x = (layout.BuildingX + layout.BuildingW + 1200) * _s + offset.X;
            double y = (layout.BuildingY - 2400) * _s + offset.Y;
            double halfBar = 5000 * _s;  // each half = 5m at 1:100
            double barLen  = halfBar * 2; // total = 10m

            // Full bar baseline
            var bar = new Line(new Point3d(x, y, 0), new Point3d(x + barLen, y, 0));
            bar.Layer = "A-ANNO-SYMB";
            space.AppendEntity(bar); tr.AddNewlyCreatedDBObject(bar, true);

            // End-ticks (tall) + mid-tick (short)
            double tallTick  = 200 * _s;
            double shortTick = 120 * _s;
            foreach (var (px, h) in new[] {
                (x,            tallTick),   // 0m
                (x + halfBar,  shortTick),  // 5m
                (x + barLen,   tallTick),   // 10m
            })
            {
                var t = new Line(new Point3d(px, y - h, 0), new Point3d(px, y + h, 0));
                t.Layer = "A-ANNO-SYMB";
                space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }

            // Hatch the first 5m segment for readability
            double hh = 100 * _s;
            var fill = new Polyline();
            fill.AddVertexAt(0, new Point2d(x,           y),     0, 0, 0);
            fill.AddVertexAt(1, new Point2d(x + halfBar, y),     0, 0, 0);
            fill.AddVertexAt(2, new Point2d(x + halfBar, y + hh),0, 0, 0);
            fill.AddVertexAt(3, new Point2d(x,           y + hh),0, 0, 0);
            fill.Closed        = true;
            fill.ConstantWidth = 0;
            fill.Layer         = "A-ANNO-SYMB";
            space.AppendEntity(fill); tr.AddNewlyCreatedDBObject(fill, true);

            // Labels: 0, 5m, 10m
            double th = 175 * _s;
            double ty  = y - tallTick - th - 50 * _s;
            foreach (var (px, lbl) in new[] {
                (x,                   "0"),
                (x + halfBar - th,    "5"),
                (x + barLen - th * 2, "10m"),
            })
            {
                var t = new DBText();
                t.Position   = new Point3d(px, ty, 0);
                t.TextString = lbl;
                t.Height     = th;
                t.Layer      = "A-ANNO-SYMB";
                space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }

            // Scale note
            var note = new DBText();
            note.Position   = new Point3d(x, y + tallTick + 50 * _s, 0);
            note.TextString = "1:100";
            note.Height     = th;
            note.Layer      = "A-ANNO-SYMB";
            space.AppendEntity(note); tr.AddNewlyCreatedDBObject(note, true);
        }

        private static bool IsEnclaireType(string rt) =>
            rt is "enclair" or "covered_porch" or "sunroom" or "sun_room" or "lanai"
              or "veranda" or "covered_outdoor_room" or "conservatory"
              or "screened_porch" or "florida_room";

        // Enclair: draw all 4 walls as a glazing triple-line (A-GLAZ) to suggest glass/sliding walls
        private void DrawEnclaireGlassWalls(SpaceNode room, Point3d offset,
            BlockTableRecord space, Transaction tr)
        {
            double x1 = room.X     * _s + offset.X;
            double y1 = room.Y     * _s + offset.Y;
            double x2 = room.Right * _s + offset.X;
            double y2 = room.Top   * _s + offset.Y;

            // Three parallel lines on each of the 4 walls to suggest glazing
            for (int i = 0; i < 3; i++)
            {
                double offDu = i * 60 * _s;
                // Bottom wall
                var b = new Line(new Point3d(x1, y1 + offDu, 0), new Point3d(x2, y1 + offDu, 0));
                b.Layer = "A-GLAZ"; space.AppendEntity(b); tr.AddNewlyCreatedDBObject(b, true);
                // Top wall (rear — main glass wall facing backyard)
                var t = new Line(new Point3d(x1, y2 - offDu, 0), new Point3d(x2, y2 - offDu, 0));
                t.Layer = "A-GLAZ"; space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                // Left wall
                var l = new Line(new Point3d(x1 + offDu, y1, 0), new Point3d(x1 + offDu, y2, 0));
                l.Layer = "A-GLAZ"; space.AppendEntity(l); tr.AddNewlyCreatedDBObject(l, true);
                // Right wall
                var r = new Line(new Point3d(x2 - offDu, y1, 0), new Point3d(x2 - offDu, y2, 0));
                r.Layer = "A-GLAZ"; space.AppendEntity(r); tr.AddNewlyCreatedDBObject(r, true);
            }

            // Sliding door symbol: a double arrow line at the bottom of the enclair
            double sdW  = Math.Min(room.WidthMm * 0.5, 2400) * _s;
            double sdMx = (x1 + x2) / 2;
            var sd = new Line(new Point3d(sdMx - sdW / 2, y1, 0), new Point3d(sdMx + sdW / 2, y1, 0));
            sd.Layer = "A-DOOR"; space.AppendEntity(sd); tr.AddNewlyCreatedDBObject(sd, true);
        }

        // Interior connecting doors: bedroom↔bath suite connections + foyer↔hallway
        private void DrawInteriorConnectingDoors(IList<SpaceNode> rooms,
            Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var bedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "primary_bedroom","master_bedroom","bedroom","secondary_bedroom","guest_bedroom" };
            var bathTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "bathroom","bath","primary_bath","ensuite_bath","ensuite","master_bath",
              "secondary_bath","shared_bath","full_bath" };
            var entryTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "entry","foyer","entry_foyer","vestibule","front_entry" };

            const double tol = 150; // mm snap tolerance — must tolerate 300mm grid rounding

            // ── 1. Bed ↔ Bath suite doors ─────────────────────────────────────
            var beds  = rooms.Where(r => bedTypes.Contains(r.Type)).ToList();
            var baths = rooms.Where(r => bathTypes.Contains(r.Type)).ToList();

            foreach (var bed in beds)
            {
                // Find a bath that shares a vertical wall with this bedroom
                var paired = baths.FirstOrDefault(b =>
                    // Bath immediately to the right of bed
                    (Math.Abs(b.X - bed.Right) < tol &&
                     b.Y < bed.Top - tol && b.Top > bed.Y + tol) ||
                    // Bath immediately to the left of bed
                    (Math.Abs(b.Right - bed.X) < tol &&
                     b.Y < bed.Top - tol && b.Top > bed.Y + tol));

                if (paired == null) continue;

                bool bathOnRight = Math.Abs(paired.X - bed.Right) < tol;
                double sharedX   = bathOnRight ? bed.Right : bed.X;
                // Place door on the shared wall, centred on the overlap Y range
                double overlapY1 = Math.Max(bed.Y, paired.Y);
                double overlapY2 = Math.Min(bed.Top, paired.Top);
                double doorW_mm  = 750;
                double midY      = (overlapY1 + overlapY2) / 2 - doorW_mm / 2;
                midY = Math.Max(midY, overlapY1);

                double sx_du = sharedX * _s + offset.X;
                double sy_du = midY    * _s + offset.Y;
                double dw_du = doorW_mm * _s;

                var leaf = new Line(
                    new Point3d(sx_du, sy_du,          0),
                    new Point3d(sx_du, sy_du + dw_du,  0));
                leaf.Layer = "A-DOOR";
                space.AppendEntity(leaf); tr.AddNewlyCreatedDBObject(leaf, true);

                // Arc swings into the bathroom
                double arcStart = bathOnRight ? 0 : Math.PI;
                var arc = new Arc(
                    new Point3d(sx_du, sy_du, 0), dw_du,
                    arcStart, arcStart + Math.PI / 2);
                arc.Layer = "A-DOOR-SWNG";
                space.AppendEntity(arc); tr.AddNewlyCreatedDBObject(arc, true);
            }

            // ── 2. Foyer → Hallway door (living wing LEFT → hallway CENTRE) ───
            var corridor = rooms.FirstOrDefault(r =>
                r.Type.Equals("corridor", StringComparison.OrdinalIgnoreCase));
            var foyer = rooms.FirstOrDefault(r => entryTypes.Contains(r.Type));
            if (corridor != null && foyer != null)
            {
                // After wing swap: living wing is LEFT, hallway is to its RIGHT.
                // Shared wall: foyer.Right ≈ corridor.X
                double sharedX = 0;
                bool found = false;
                if (Math.Abs(foyer.Right - corridor.X) < tol * 4)
                {
                    sharedX = corridor.X; found = true;
                }
                else if (Math.Abs(foyer.X - corridor.Right) < tol * 4)
                {
                    sharedX = corridor.Right; found = true;
                }

                if (found)
                {
                    double overlapY1 = Math.Max(foyer.Y, corridor.Y);
                    double overlapY2 = Math.Min(foyer.Top, corridor.Top);
                    double doorW_mm  = 900;
                    double midY      = (overlapY1 + overlapY2) / 2 - doorW_mm / 2;

                    double sx_du = sharedX  * _s + offset.X;
                    double sy_du = midY     * _s + offset.Y;
                    double dw_du = doorW_mm * _s;

                    var leaf = new Line(
                        new Point3d(sx_du, sy_du,         0),
                        new Point3d(sx_du, sy_du + dw_du, 0));
                    leaf.Layer = "A-DOOR";
                    space.AppendEntity(leaf); tr.AddNewlyCreatedDBObject(leaf, true);

                    // Arc swings toward the hallway (east direction)
                    var arc = new Arc(
                        new Point3d(sx_du, sy_du, 0), dw_du, 0, Math.PI / 2);
                    arc.Layer = "A-DOOR-SWNG";
                    space.AppendEntity(arc); tr.AddNewlyCreatedDBObject(arc, true);
                }
            }
        }

        // Continuous horizontal dimension chain — individual room widths across the building
        // Draws tick marks at every unique X boundary, labels each segment between ticks
        private void DrawHorizDimChain(IList<SpaceNode> rooms, double chainYmm,
                                        Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var xVals = rooms.Select(r => r.X)
                             .Concat(rooms.Select(r => r.Right))
                             .Distinct()
                             .OrderBy(v => v)
                             .ToList();
            if (xVals.Count < 2) return;

            double chainY = chainYmm * _s + offset.Y;
            double tick   = 220 * _s;

            // Main chain line
            var dl = new Line(
                new Point3d(xVals[0] * _s + offset.X, chainY, 0),
                new Point3d(xVals[^1] * _s + offset.X, chainY, 0));
            dl.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dl); tr.AddNewlyCreatedDBObject(dl, true);

            // Tick marks
            foreach (double xmm in xVals)
            {
                double xdu = xmm * _s + offset.X;
                var t = new Line(new Point3d(xdu, chainY - tick, 0),
                                  new Point3d(xdu, chainY + tick, 0));
                t.Layer = "A-ANNO-DIMS";
                space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }

            // Segment labels
            for (int i = 0; i + 1 < xVals.Count; i++)
            {
                double seg = xVals[i + 1] - xVals[i];
                if (seg < 600) continue;
                string label = FtIn(seg);
                double midX  = (xVals[i] + xVals[i + 1]) / 2 * _s + offset.X;
                var txt = new DBText
                {
                    Position   = new Point3d(midX - label.Length * 50 * _s, chainY + 180 * _s, 0),
                    TextString = label,
                    Height     = 175 * _s,
                    Layer      = "A-ANNO-DIMS"
                };
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
        }

        // Continuous vertical dimension chain — individual room depths along the right side
        private void DrawVertDimChain(IList<SpaceNode> rooms, double chainXmm,
                                       Point3d offset, BlockTableRecord space, Transaction tr)
        {
            var yVals = rooms.Select(r => r.Y)
                             .Concat(rooms.Select(r => r.Top))
                             .Distinct()
                             .OrderBy(v => v)
                             .ToList();
            if (yVals.Count < 2) return;

            double chainX = chainXmm * _s + offset.X;
            double tick   = 220 * _s;

            var dl = new Line(
                new Point3d(chainX, yVals[0] * _s + offset.Y, 0),
                new Point3d(chainX, yVals[^1] * _s + offset.Y, 0));
            dl.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dl); tr.AddNewlyCreatedDBObject(dl, true);

            foreach (double ymm in yVals)
            {
                double ydu = ymm * _s + offset.Y;
                var t = new Line(new Point3d(chainX - tick, ydu, 0),
                                  new Point3d(chainX + tick, ydu, 0));
                t.Layer = "A-ANNO-DIMS";
                space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
            }

            for (int i = 0; i + 1 < yVals.Count; i++)
            {
                double seg = yVals[i + 1] - yVals[i];
                if (seg < 600) continue;
                string label = FtIn(seg);
                double midY  = (yVals[i] + yVals[i + 1]) / 2 * _s + offset.Y;
                var txt = new DBText
                {
                    Position   = new Point3d(chainX + 180 * _s, midY, 0),
                    Rotation   = Math.PI / 2,
                    TextString = label,
                    Height     = 175 * _s,
                    Layer      = "A-ANNO-DIMS"
                };
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
        }

        // Building footprint dimension annotations (W and D in feet-inches)
        private void DrawBuildingDimensions(LayoutResult layout, Point3d offset,
                                             BlockTableRecord space, Transaction tr)
        {
            double bx = layout.BuildingX * _s + offset.X;
            double by = layout.BuildingY * _s + offset.Y;
            double bw = layout.BuildingW * _s;
            double bd = layout.BuildingD * _s;
            double gap  = 900 * _s;
            double tick = 250 * _s;

            // Width — below the building
            DrawDimLine(bx, by - gap, bx + bw, by - gap, tick,
                        FtIn(layout.BuildingW), false, space, tr);
            // Depth — right of the building
            DrawDimLine(bx + bw + gap, by, bx + bw + gap, by + bd, tick,
                        FtIn(layout.BuildingD), true, space, tr);
        }

        private void DrawDimLine(double x1, double y1, double x2, double y2,
                                  double tick, string label, bool vertical,
                                  BlockTableRecord space, Transaction tr)
        {
            // Main line
            var dl = new Line(new Point3d(x1, y1, 0), new Point3d(x2, y2, 0));
            dl.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dl); tr.AddNewlyCreatedDBObject(dl, true);

            // Tick marks
            if (vertical)
            {
                foreach (double py in new[] { y1, y2 })
                {
                    var t = new Line(new Point3d(x1 - tick, py, 0), new Point3d(x1 + tick, py, 0));
                    t.Layer = "A-ANNO-DIMS";
                    space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                }
                var txt = new DBText();
                txt.Position   = new Point3d(x1 + 200 * _s, (y1 + y2) / 2, 0);
                txt.Rotation   = Math.PI / 2;
                txt.TextString = label;
                txt.Height     = 200 * _s;
                txt.Layer      = "A-ANNO-DIMS";
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
            else
            {
                foreach (double px in new[] { x1, x2 })
                {
                    var t = new Line(new Point3d(px, y1 - tick, 0), new Point3d(px, y1 + tick, 0));
                    t.Layer = "A-ANNO-DIMS";
                    space.AppendEntity(t); tr.AddNewlyCreatedDBObject(t, true);
                }
                var txt = new DBText();
                txt.Position   = new Point3d((x1 + x2) / 2 - label.Length * 55 * _s, y1 + 180 * _s, 0);
                txt.TextString = label;
                txt.Height     = 200 * _s;
                txt.Layer      = "A-ANNO-DIMS";
                space.AppendEntity(txt); tr.AddNewlyCreatedDBObject(txt, true);
            }
        }

        // Entry walkway — paved path from building front to street
        private void DrawEntryWalkway(LayoutResult layout, Point3d offset,
                                       BlockTableRecord space, Transaction tr)
        {
            double bx     = layout.BuildingX * _s + offset.X;
            double by     = layout.BuildingY * _s + offset.Y;
            double bw     = layout.BuildingW * _s;
            double pathW  = 1800 * _s;
            double midX   = bx + bw / 2;
            double streetY = offset.Y;

            var path = new Polyline();
            path.AddVertexAt(0, new Point2d(midX - pathW / 2, streetY), 0, 0, 0);
            path.AddVertexAt(1, new Point2d(midX + pathW / 2, streetY), 0, 0, 0);
            path.AddVertexAt(2, new Point2d(midX + pathW / 2, by),      0, 0, 0);
            path.AddVertexAt(3, new Point2d(midX - pathW / 2, by),      0, 0, 0);
            path.Closed        = true;
            path.ConstantWidth = 0;
            path.Layer         = "C-PROP";
            space.AppendEntity(path);
            tr.AddNewlyCreatedDBObject(path, true);
        }

        private static double GetDoorWidth(string type)
        {
            string t = (type ?? "").ToLowerInvariant().Replace('-','_').Replace(' ','_');
            return t switch
            {
                "primary_bedroom" or "master_bedroom" or "bedroom"
                or "secondary_bedroom" or "guest_bedroom"   => 900,
                "primary_bath" or "ensuite_bath" or "ensuite"
                or "bathroom"  or "bath" or "shared_bath"   => 750,
                "powder_room"  or "half_bath" or "toilet"   => 750,
                "walk_in_closet" or "closet" or "wic"       => 750,
                "living" or "dining" or "open_plan"
                or "family_room" or "great_room"            => 1000,
                "entry" or "foyer" or "entry_foyer"
                or "vestibule"                              => 1200,
                "kitchen"                                   => 900,
                "laundry" or "laundry_room" or "mudroom"   => 900,
                "garage"                                    => 2400,
                "emergency" or "icu"                        => 1800,
                _                                           => 900,
            };
        }

        // Fix #7: create all AIA layers directly — no separate helper needed
        // VALIDATION-FIX: CHECK-DQ05 — added programmatic LineWeight to every layer entry
        private void EnsureStandardLayers(Transaction tr)
        {
            var layers = new (string name, short color, LineWeight lw)[]
            {
                ("A-WALL-EXTR", 7, LineWeight.LineWeight035),  // exterior walls: heavy
                ("A-WALL-INTR", 7, LineWeight.LineWeight025),  // interior walls: medium
                ("A-WALL-PRTN", 8, LineWeight.LineWeight018),  // partitions: light
                ("A-DOOR",      4, LineWeight.LineWeight018),
                ("A-DOOR-SWNG", 4, LineWeight.LineWeight013),
                ("A-GLAZ",      4, LineWeight.LineWeight013),
                ("A-ANNO-TEXT", 2, LineWeight.LineWeight018),
                ("A-AREA-IDEN", 6, LineWeight.LineWeight013),
                ("A-ANNO-SYMB", 2, LineWeight.LineWeight013),
                ("A-ANNO-DIMS", 2, LineWeight.LineWeight013),
                ("A-ANNO-TTLB", 7, LineWeight.LineWeight035),  // title block: heavy
                ("A-WALL-PATT", 8, LineWeight.LineWeight009),
                ("C-PROP",      1, LineWeight.LineWeight050),  // property line: bold
                ("S-COLS",      7, LineWeight.LineWeight035),
                ("ZH-AI-NOTES",150,LineWeight.LineWeight013),
                ("A-FURN",      3, LineWeight.LineWeight013),  // MISSING-05: schematic furniture
            };

            // D-01: skip the table open entirely if all layers are already cached
            if (layers.All(l => _createdLayers.Contains(l.name)))
                return;

            var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            bool upgraded = false;

            foreach (var (name, color, lw) in layers)
            {
                if (_createdLayers.Contains(name) || lt.Has(name))
                {
                    _createdLayers.Add(name);
                    continue;
                }
                if (!upgraded) { lt.UpgradeOpen(); upgraded = true; }
                var ltr = new LayerTableRecord { Name = name };
                ltr.Color      = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci, color);
                ltr.LineWeight = lw;
                lt.Add(ltr);
                tr.AddNewlyCreatedDBObject(ltr, true);
                _createdLayers.Add(name);
            }
        }

        // ── Zone parsing — prefers typed Layout contract, falls back to zones_json ─

        private List<ZoneGroup> ParseZones(VariationPlan variation)
        {
            // F-01: prefer typed Layout.Zones (no JSON parsing needed)
            if (variation.Layout?.Zones?.Count > 0)
                return ParseZonesFromContract(variation.Layout.Zones);

            // legacy fallback: parse zones_json string from Properties dict
            var zones = new List<ZoneGroup>();
            if (!variation.Properties.TryGetValue("zones_json", out var raw))
                return zones;

            try
            {
                var list = JArray.Parse(raw?.ToString() ?? "[]");

                foreach (var z in list)
                {
                    var zone = new ZoneGroup
                    {
                        ZoneName = z["zone_name"]?.ToString()     ?? "",
                        Position = z["zone_position"]?.ToString() ?? "front",
                    };

                    foreach (var s in z["spaces"] as JArray ?? new JArray())
                    {
                        zone.Spaces.Add(new SpaceNode
                        {
                            Name            = s["name"]?.ToString()               ?? "Room",
                            Type            = s["type"]?.ToString()               ?? "room",
                            AreaSqm         = s["area_sqm"]?.Value<double>()      ?? 15,
                            AspectRatio     = s["aspect_ratio"]?.Value<double>()  ?? 1.4,
                            Floor           = s["floor"]?.Value<int>()            ?? 1,
                            Facing          = s["facing"]?.ToString()             ?? "south",
                            HasNaturalLight = s["has_natural_light"]?.Value<bool>() ?? true,
                            MinWidthM       = s["min_width_m"]?.Value<double>()   ?? 2.4,
                            MinDepthM       = s["min_depth_m"]?.Value<double>()   ?? 2.4,
                            AdjacentTo      = s["must_be_adjacent_to"]?
                                              .ToObject<List<string>>() ?? new(),
                            DoorConnects    = s["door_connects_to"]?
                                              .ToObject<List<string>>() ?? new(),
                        });
                    }

                    zones.Add(zone);
                }
            }
            catch (Exception ex)
            {
                _ed.WriteMessage($"\n[zHeight] Zone parse error: {ex.Message}");
            }

            return zones;
        }

        private static List<ZoneGroup> ParseZonesFromContract(List<ZoneGroupContract> contracts)
        {
            var zones = new List<ZoneGroup>();
            foreach (var zc in contracts)
            {
                var zone = new ZoneGroup
                {
                    ZoneName = zc.ZoneName,
                    Position = zc.ZonePosition,
                };
                foreach (var sc in zc.Spaces)
                {
                    zone.Spaces.Add(new SpaceNode
                    {
                        Name            = sc.Name,
                        Type            = sc.Type,
                        AreaSqm         = sc.AreaSqm ?? 15.0,
                        AspectRatio     = sc.AspectRatio ?? 1.4,
                        Floor           = sc.Floor,
                        Facing          = "south",
                        HasNaturalLight = sc.HasNaturalLight,
                        MinWidthM       = sc.MinWidthM ?? 2.4,
                        MinDepthM       = sc.MinDepthM ?? 2.4,
                        AdjacentTo      = new List<string>(),
                        DoorConnects    = new List<string>(),
                    });
                }
                zones.Add(zone);
            }
            return zones;
        }

        // ── Fallback action executor (used when no zones present) ─────────────

        private void ExecuteAction(DrawAction a, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            switch (a.ActionType)
            {
                case ActionType.CREATE_LAYER:
                    EnsureLayer(a, tr);
                    break;

                case ActionType.DRAW_WALL:
                    if (a.Vertices?.Count >= 2)
                        DrawPolyline(a.Vertices, offset, a.Layer,
                                     (a.ThicknessMm ?? 150) * _s,
                                     a.Properties.ContainsKey("closed"), space, tr);
                    else if (a.Start != null && a.End != null)
                        DrawLine(a.Start, a.End, offset, a.Layer, space, tr);
                    break;

                case ActionType.DRAW_DOOR:
                    if (a.Start != null) DrawDoor(a, offset, space, tr);
                    break;

                case ActionType.DRAW_WINDOW:
                    if (a.Start != null) DrawWindow(a, offset, space, tr);
                    break;

                case ActionType.DRAW_COLUMN:
                    if (a.Center != null) DrawColumn(a, offset, space, tr);
                    break;

                case ActionType.DRAW_ROOM_LABEL:
                case ActionType.ADD_AREA_TAG:
                    if (a.Center != null && !string.IsNullOrEmpty(a.LabelText))
                        DrawMText(a, offset, space, tr);
                    break;

                case ActionType.ADD_DIMENSION:
                    if (a.Start != null && a.End != null)
                        DrawDimension(a, offset, space, tr);
                    break;

                case ActionType.ADD_HATCH:
                    if (a.HatchBoundary?.Count >= 3)
                        DrawHatch(a, offset, space, tr);
                    break;

                case ActionType.ADD_NORTH_ARROW:
                    if (a.Center != null) DrawNorthArrow(a, offset, space, tr);
                    break;

                case ActionType.ADD_TITLE_BLOCK:
                    // VALIDATION-FIX: CHECK-DQ01 — insert as block reference, not raw DBText
                    DrawTitleBlockInsert(a, offset, space, tr);
                    break;

                case ActionType.START_GROUP:
                case ActionType.END_GROUP:
                    break;
            }
        }

        // ── Drawing primitives (fallback path) ───────────────────────────────

        private void DrawPolyline(List<Point2D> pts, Point3d offset, string layer,
                                   double cw, bool closed,
                                   BlockTableRecord space, Transaction tr)
        {
            var pl = new Polyline();
            for (int i = 0; i < pts.Count; i++)
                pl.AddVertexAt(i, To2d(pts[i], offset), 0, 0, 0);
            pl.Closed        = closed;
            pl.ConstantWidth = cw;
            pl.Layer         = layer;
            space.AppendEntity(pl);
            tr.AddNewlyCreatedDBObject(pl, true);
        }

        private void DrawLine(Point2D s, Point2D e, Point3d offset,
                               string layer, BlockTableRecord space, Transaction tr)
        {
            var line = new Line(To3d(s, offset), To3d(e, offset));
            line.Layer = layer;
            space.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
        }

        private void DrawDoor(DrawAction a, Point3d offset,
                               BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Start!, offset);
            double w = (a.DoorWidthMm ?? 900) * _s;

            var leaf = new Line(pos, new Point3d(pos.X + w, pos.Y, 0));
            leaf.Layer = "A-DOOR";
            space.AppendEntity(leaf);
            tr.AddNewlyCreatedDBObject(leaf, true);

            double rad = a.SwingAngle * Math.PI / 180.0;
            var arc    = new Arc(pos, w, 0, rad);
            arc.Layer  = "A-DOOR-SWNG";
            space.AppendEntity(arc);
            tr.AddNewlyCreatedDBObject(arc, true);
        }

        private void DrawWindow(DrawAction a, Point3d offset,
                                 BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Start!, offset);
            double w = (a.WindowWidthMm ?? 1200) * _s;

            for (int i = 0; i < 3; i++)
            {
                double yOff = i * 50 * _s;
                var line = new Line(
                    new Point3d(pos.X, pos.Y + yOff, 0),
                    new Point3d(pos.X + w, pos.Y + yOff, 0));
                line.Layer = "A-GLAZ";
                space.AppendEntity(line);
                tr.AddNewlyCreatedDBObject(line, true);
            }
        }

        private void DrawColumn(DrawAction a, Point3d offset,
                                 BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Center!, offset);
            double cw = (a.ColumnWidthMm ?? 300) * _s / 2;
            double cd = (a.ColumnDepthMm ?? 300) * _s / 2;

            var solid = new Solid(
                new Point3d(pos.X - cw, pos.Y - cd, 0),
                new Point3d(pos.X + cw, pos.Y - cd, 0),
                new Point3d(pos.X - cw, pos.Y + cd, 0),
                new Point3d(pos.X + cw, pos.Y + cd, 0));
            solid.Layer = "S-COLS";
            space.AppendEntity(solid);
            tr.AddNewlyCreatedDBObject(solid, true);
        }

        private void DrawMText(DrawAction a, Point3d offset,
                                BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Center ?? a.Start!, offset);

            string content = a.LabelText!;
            if (a.LabelAreaSqm.HasValue)
                content += $"\\P{a.LabelAreaSqm:F1} m\\U+00B2";

            var mt = new MText();
            mt.Location   = pos;
            mt.TextHeight = a.FontHeightMm * _s;
            mt.Layer      = a.Layer;
            mt.Contents   = content;
            mt.Attachment = AttachmentPoint.MiddleCenter;
            space.AppendEntity(mt);
            tr.AddNewlyCreatedDBObject(mt, true);
        }

        // VALIDATION-FIX: CHECK-DQ01 — title block as BlockDefinition + BlockReference INSERT
        private const string TitleBlockName = "ZHEIGHT-TITLE";

        private void DrawTitleBlockInsert(DrawAction a, Point3d offset,
                                          BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Start ?? a.Center ?? new Point2D { X = 0, Y = 0 }, offset);
            string projectTitle = a.LabelText ?? "zHeight Project";

            // Build block definition once per drawing session
            var bt = (BlockTable)tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
            ObjectId blkId;

            if (!bt.Has(TitleBlockName))
            {
                bt.UpgradeOpen();
                var btr = new BlockTableRecord { Name = TitleBlockName, Origin = Point3d.Origin };
                blkId = bt.Add(btr);
                tr.AddNewlyCreatedDBObject(btr, true);
                PopulateTitleBlockDef(btr, tr);
            }
            else
            {
                blkId = bt[TitleBlockName];
            }

            var bref = new BlockReference(pos, blkId);
            bref.Layer = "A-ANNO-TTLB";
            space.AppendEntity(bref);
            tr.AddNewlyCreatedDBObject(bref, true);

            // Stamp attribute values
            var defBtr = (BlockTableRecord)tr.GetObject(blkId, OpenMode.ForRead);
            foreach (ObjectId eid in defBtr)
            {
                if (!(tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition attDef) || attDef.Constant)
                    continue;
                var attRef = new AttributeReference();
                attRef.SetAttributeFromBlock(attDef, bref.BlockTransform);
                attRef.TextString = attDef.Tag switch
                {
                    "PROJECT_TITLE" => projectTitle,
                    "DRAW_SCALE"    => a.Properties.TryGetValue("scale", out var sc)
                                         ? sc?.ToString() ?? "1:100" : "1:100",
                    "DRAW_DATE"     => System.DateTime.UtcNow.ToString("yyyy-MM-dd"),
                    _               => attDef.TextString,
                };
                bref.AttributeCollection.AppendAttribute(attRef);
                tr.AddNewlyCreatedDBObject(attRef, true);
            }
        }

        private void PopulateTitleBlockDef(BlockTableRecord btr, Transaction tr)
        {
            // Title block frame: 200mm × 50mm box, drawn at origin, scaled to drawing units
            double bw = 200 * _s;
            double bh = 50  * _s;
            double div = bh * 0.55;   // divider between title row and info row

            // Outer border
            var border = new Polyline();
            border.AddVertexAt(0, new Point2d(0,  0),  0, 0, 0);
            border.AddVertexAt(1, new Point2d(bw, 0),  0, 0, 0);
            border.AddVertexAt(2, new Point2d(bw, bh), 0, 0, 0);
            border.AddVertexAt(3, new Point2d(0,  bh), 0, 0, 0);
            border.Closed        = true;
            border.ConstantWidth = 0.8 * _s;
            border.Layer         = "A-ANNO-TTLB";
            btr.AppendEntity(border);
            tr.AddNewlyCreatedDBObject(border, true);

            // Horizontal divider between project title and info row
            var divLine = new Line(new Point3d(0, div, 0), new Point3d(bw, div, 0));
            divLine.Layer = "A-ANNO-TTLB";
            btr.AppendEntity(divLine);
            tr.AddNewlyCreatedDBObject(divLine, true);

            // Vertical divider: Scale | Date
            double vd = bw * 0.45;
            var vLine = new Line(new Point3d(vd, 0, 0), new Point3d(vd, div, 0));
            vLine.Layer = "A-ANNO-TTLB";
            btr.AppendEntity(vLine);
            tr.AddNewlyCreatedDBObject(vLine, true);

            // PROJECT_TITLE attribute definition
            AddAttDef(btr, tr, "PROJECT_TITLE", "Project Title",
                new Point3d(4 * _s, div + 6 * _s, 0), 6 * _s, "PROJECT TITLE");

            // DRAW_SCALE attribute definition
            AddAttDef(btr, tr, "DRAW_SCALE", "Scale",
                new Point3d(3 * _s, 6 * _s, 0), 4 * _s, "1:100");

            // DRAW_DATE attribute definition
            AddAttDef(btr, tr, "DRAW_DATE", "Date",
                new Point3d(vd + 3 * _s, 6 * _s, 0), 4 * _s,
                System.DateTime.UtcNow.ToString("yyyy-MM-dd"));
        }

        private static void AddAttDef(BlockTableRecord btr, Transaction tr,
                                       string tag, string prompt,
                                       Point3d pos, double height, string defaultVal)
        {
            var att = new AttributeDefinition();
            att.Tag         = tag;
            att.Prompt      = prompt;
            att.TextString  = defaultVal;
            att.Position    = pos;
            att.Height      = height;
            att.Layer       = "A-ANNO-TTLB";
            att.Invisible   = false;
            btr.AppendEntity(att);
            tr.AddNewlyCreatedDBObject(att, true);
        }

        private void DrawDimension(DrawAction a, Point3d offset,
                                    BlockTableRecord space, Transaction tr)
        {
            var p1  = To3d(a.Start!, offset);
            var p2  = To3d(a.End!,   offset);
            var mid = new Point3d((p1.X + p2.X) / 2, p1.Y - 600 * _s, 0);
            var dim = new RotatedDimension(0, p1, p2, mid, "", EnsureDimStyle(tr));
            dim.Layer = "A-ANNO-DIMS";
            space.AppendEntity(dim);
            tr.AddNewlyCreatedDBObject(dim, true);
        }

        private ObjectId EnsureDimStyle(Transaction tr)
        {
            // D-01: return cached ObjectId — avoids DimStyleTable lookup on every variation
            if (!_dimStyleId.IsNull)
                return _dimStyleId;

            var dst = (DimStyleTable)tr.GetObject(_db.DimStyleTableId, OpenMode.ForRead);
            if (dst.Has("ZHEIGHT-DIMS"))
            {
                _dimStyleId = dst["ZHEIGHT-DIMS"];
                return _dimStyleId;
            }

            dst.UpgradeOpen();
            var dsr = new DimStyleTableRecord();
            dsr.Name     = "ZHEIGHT-DIMS";
            dsr.Dimscale = 100.0;  // 1:100 architectural sheet
            dsr.Dimasz   = 3.0;    // 3mm arrow/tick
            dsr.Dimtxt   = 3.0;    // 3mm text height
            dsr.Dimexe   = 1.5;    // 1.5mm extension above tick
            dsr.Dimexo   = 1.5;    // 1.5mm extension offset
            dsr.Dimgap   = 1.0;    // 1mm text-to-line gap
            dsr.Dimlunit = 4;      // Architectural units (feet + inches)
            dsr.Dimdec   = 0;      // 0 decimal places
            _dimStyleId = dst.Add(dsr);
            tr.AddNewlyCreatedDBObject(dsr, true);
            return _dimStyleId;
        }

        private void DrawHatch(DrawAction a, Point3d offset,
                                BlockTableRecord space, Transaction tr)
        {
            var boundary = new Polyline();
            for (int i = 0; i < a.HatchBoundary.Count; i++)
                boundary.AddVertexAt(i, To2d(a.HatchBoundary[i], offset), 0, 0, 0);
            boundary.Closed = true;
            boundary.Layer  = a.Layer;
            space.AppendEntity(boundary);
            tr.AddNewlyCreatedDBObject(boundary, true);

            var hatch = new Hatch();
            hatch.SetHatchPattern(HatchPatternType.PreDefined, a.HatchPattern ?? "ANSI31");
            hatch.PatternScale = a.HatchScale;
            hatch.PatternAngle = a.HatchAngle * Math.PI / 180.0;
            hatch.Layer        = a.Layer;
            hatch.Associative  = true;
            space.AppendEntity(hatch);
            tr.AddNewlyCreatedDBObject(hatch, true);

            hatch.AppendLoop(HatchLoopTypes.Outermost,
                             new ObjectIdCollection { boundary.ObjectId });
            hatch.EvaluateHatch(true);
        }

        private void DrawNorthArrow(DrawAction a, Point3d offset,
                                     BlockTableRecord space, Transaction tr)
        {
            var pos = To3d(a.Center!, offset);
            double r = 300 * _s;

            var circle = new Circle(pos, Vector3d.ZAxis, r);
            circle.Layer = "A-ANNO-SYMB";
            space.AppendEntity(circle);
            tr.AddNewlyCreatedDBObject(circle, true);

            var arrow = new Line(pos, new Point3d(pos.X, pos.Y + r, 0));
            arrow.Layer = "A-ANNO-SYMB";
            space.AppendEntity(arrow);
            tr.AddNewlyCreatedDBObject(arrow, true);

            var nLabel = new DBText();
            nLabel.Position   = new Point3d(pos.X - 100 * _s, pos.Y + r + 80 * _s, 0);
            nLabel.TextString = "N";
            nLabel.Height     = 200 * _s;
            nLabel.Layer      = "A-ANNO-SYMB";
            space.AppendEntity(nLabel);
            tr.AddNewlyCreatedDBObject(nLabel, true);
        }

        private void EnsureLayer(DrawAction a, Transaction tr)
        {
            var lt = (LayerTable)tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(a.Layer)) return;

            lt.UpgradeOpen();
            var ltr = new LayerTableRecord { Name = a.Layer };
            if (a.LayerColor.HasValue)
                ltr.Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                    Autodesk.AutoCAD.Colors.ColorMethod.ByAci,
                    (short)a.LayerColor.Value);
            if (!string.IsNullOrEmpty(a.LayerLinetype))
            {
                var ltt = (LinetypeTable)tr.GetObject(
                    _db.LinetypeTableId, OpenMode.ForRead);
                if (ltt.Has(a.LayerLinetype))
                    ltr.LinetypeObjectId = ltt[a.LayerLinetype];
            }
            lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
        }

        // ── Coordinate helpers ────────────────────────────────────────────────

        private Point3d To3d(Point2D p, Point3d offset) =>
            new Point3d(p.X * _s + offset.X, p.Y * _s + offset.Y, 0);

        private Point2d To2d(Point2D p, Point3d offset) =>
            new Point2d(p.X * _s + offset.X, p.Y * _s + offset.Y);

        // ── Unit scale detection ──────────────────────────────────────────────

        public static double GetMmScale(Database db) => db.Insunits switch
        {
            UnitsValue.Millimeters => 1.0,
            UnitsValue.Centimeters => 0.1,
            UnitsValue.Decimeters  => 0.01,
            UnitsValue.Meters      => 0.001,
            UnitsValue.Kilometers  => 0.000001,
            UnitsValue.Inches      => 1.0 / 25.4,
            UnitsValue.Feet        => 1.0 / 304.8,
            UnitsValue.Yards       => 1.0 / 914.4,
            UnitsValue.Undefined   => 1.0,
            _                      => 1.0,
        };
    }
}
