using System.Collections.Generic;
using Newtonsoft.Json;

namespace zHeight.Plugin.Models
{
    public enum ActionType
    {
        DRAW_WALL, DRAW_DOOR, DRAW_WINDOW, DRAW_COLUMN,
        DRAW_STAIR, DRAW_ROOM_LABEL, ADD_DIMENSION, ADD_AREA_TAG,
        ADD_NORTH_ARROW, ADD_SCALE_BAR, ADD_TITLE_BLOCK,
        ADD_HATCH, CREATE_LAYER, START_GROUP, END_GROUP
    }

    public class Point2D
    {
        [JsonProperty("x")] public double X { get; set; }
        [JsonProperty("y")] public double Y { get; set; }
    }

    public class ConstraintReport
    {
        [JsonProperty("far_used")]          public double? FarUsed { get; set; }
        [JsonProperty("height_m")]          public double? HeightM { get; set; }
        [JsonProperty("coverage_pct")]      public double? CoveragePct { get; set; }
        [JsonProperty("all_rooms_reached")] public bool AllRoomsReached { get; set; } = true;
        [JsonProperty("adjacency_met")]     public List<string> AdjacencyMet { get; set; } = new();
        [JsonProperty("adjacency_failed")]  public List<string> AdjacencyFailed { get; set; } = new();
    }

    public class SpaceSummary
    {
        [JsonProperty("name")]     public string Name { get; set; } = "";
        [JsonProperty("type")]     public string Type { get; set; } = "";
        [JsonProperty("area_sqm")] public double AreaSqm { get; set; }
        [JsonProperty("floor")]    public int Floor { get; set; } = 1;
        [JsonProperty("facing")]   public string? Facing { get; set; }
    }

    public class DrawAction
    {
        [JsonProperty("action_type")]      public ActionType ActionType { get; set; }
        [JsonProperty("layer")]            public string Layer { get; set; } = "";
        [JsonProperty("group_id")]         public string? GroupId { get; set; }
        [JsonProperty("start")]            public Point2D? Start { get; set; }
        [JsonProperty("end")]              public Point2D? End { get; set; }
        [JsonProperty("vertices")]         public List<Point2D> Vertices { get; set; } = new();
        [JsonProperty("center")]           public Point2D? Center { get; set; }
        [JsonProperty("thickness_mm")]     public double? ThicknessMm { get; set; }
        [JsonProperty("height_mm")]        public double? HeightMm { get; set; }
        [JsonProperty("wall_type")]        public string? WallType { get; set; }
        [JsonProperty("door_width_mm")]    public double? DoorWidthMm { get; set; }
        [JsonProperty("door_swing")]       public string DoorSwing { get; set; } = "right";
        [JsonProperty("swing_angle")]      public double SwingAngle { get; set; } = 90.0;
        [JsonProperty("window_width_mm")]  public double? WindowWidthMm { get; set; }
        [JsonProperty("window_height_mm")] public double? WindowHeightMm { get; set; }
        [JsonProperty("window_sill_mm")]   public double? WindowSillMm { get; set; }
        [JsonProperty("column_width_mm")]  public double? ColumnWidthMm { get; set; }
        [JsonProperty("column_depth_mm")]  public double? ColumnDepthMm { get; set; }
        [JsonProperty("label_text")]       public string? LabelText { get; set; }
        [JsonProperty("label_area_sqm")]   public double? LabelAreaSqm { get; set; }
        [JsonProperty("font_height_mm")]   public double FontHeightMm { get; set; } = 300;
        [JsonProperty("hatch_pattern")]    public string? HatchPattern { get; set; }
        [JsonProperty("hatch_scale")]      public double HatchScale { get; set; } = 1.0;
        [JsonProperty("hatch_angle")]      public double HatchAngle { get; set; }
        [JsonProperty("hatch_boundary")]   public List<Point2D> HatchBoundary { get; set; } = new();
        [JsonProperty("layer_color")]      public int? LayerColor { get; set; }
        [JsonProperty("layer_linetype")]   public string? LayerLinetype { get; set; }
        [JsonProperty("layer_lineweight")] public double? LayerLineweight { get; set; }
        [JsonProperty("properties")]       public Dictionary<string, object> Properties { get; set; } = new();
    }

    public class VariationPlan
    {
        [JsonProperty("variation_id")]      public int VariationId { get; set; }
        [JsonProperty("variation_name")]    public string VariationName { get; set; } = "";
        [JsonProperty("concept_rationale")] public string ConceptRationale { get; set; } = "";
        [JsonProperty("total_area_sqm")]    public double TotalAreaSqm { get; set; }
        [JsonProperty("floor_count")]       public int FloorCount { get; set; } = 1;
        [JsonProperty("scale")]             public string Scale { get; set; } = "1:100";
        [JsonProperty("units")]             public string Units { get; set; } = "mm";
        [JsonProperty("north_angle_deg")]   public double NorthAngleDeg { get; set; }
        [JsonProperty("actions")]           public List<DrawAction> Actions { get; set; } = new();
        [JsonProperty("space_summary")]     public List<SpaceSummary> SpaceSummary { get; set; } = new();
        [JsonProperty("constraint_report")] public ConstraintReport ConstraintReport { get; set; } = new();
        [JsonProperty("passive_notes")]     public string PassiveNotes { get; set; } = "";
        [JsonProperty("warnings")]          public List<string> Warnings { get; set; } = new();
        [JsonProperty("properties")]        public Dictionary<string, object> Properties { get; set; } = new();
    }

    public class DrawActionPlan
    {
        [JsonProperty("request_id")]            public string RequestId { get; set; } = "";
        [JsonProperty("api_version")]           public string ApiVersion { get; set; } = "v1";
        [JsonProperty("project_description")]   public string ProjectDescription { get; set; } = "";
        [JsonProperty("project_category")]      public string ProjectCategory { get; set; } = "";
        [JsonProperty("generated_at")]          public string GeneratedAt { get; set; } = "";
        [JsonProperty("variations")]            public List<VariationPlan> Variations { get; set; } = new();
        [JsonProperty("recommended_variation")] public int RecommendedVariation { get; set; } = 1;
        [JsonProperty("kb_scores")]             public List<double> KbScores { get; set; } = new();
        [JsonProperty("global_warnings")]       public List<string> GlobalWarnings { get; set; } = new();
        [JsonProperty("layer_standard")]        public string LayerStandard { get; set; } = "AIA";
    }
}
