// ApiClient.cs
// Fixed issues:
//   - API key loaded from encrypted config, not process environment
//   - Offline fallback returns last cached response with warning
//   - Timeout set to 90s (generation can be slow on first call)
//   - Request includes autocad_units for correct unit scale

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using zHeight.Plugin.Models;
using zHeight.Plugin.Config;

namespace zHeight.Plugin.Client
{
    public class ApiClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string     _baseUrl;
        private string?             _lastResponseJson;

        public ApiClient()
        {
            var cfg  = PluginConfig.Load();
            _baseUrl = cfg.ApiBaseUrl.TrimEnd('/');

            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
            _http.DefaultRequestHeaders.Add("X-API-Key",  cfg.ApiKey);
            _http.DefaultRequestHeaders.Add("User-Agent", "zHeightPlugin/3.1");
        }

        public async Task<DrawActionPlan> GenerateAsync(
            GenerateRequest req,
            CancellationToken ct = default)
        {
            var json    = JsonConvert.SerializeObject(req);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var resp = await _http.PostAsync($"{_baseUrl}/v1/orchestrate", content, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    throw new Exception($"API error {(int)resp.StatusCode}: {body}");

                _lastResponseJson = body;

                return JsonConvert.DeserializeObject<DrawActionPlan>(body)
                       ?? throw new Exception("Empty API response");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    "Request timed out (90s). Check network and try again.");
            }
            catch (HttpRequestException) when (_lastResponseJson != null)
            {
                // Offline fallback: return last successful plan with a warning
                var cached = JsonConvert.DeserializeObject<DrawActionPlan>(
                                 _lastResponseJson)!;
                cached.GlobalWarnings.Insert(0,
                    "OFFLINE MODE: showing last cached response. " +
                    "Connect to internet for fresh generation.");
                return cached;
            }
        }

        public async Task SendFeedbackAsync(FeedbackPayload payload,
                                             CancellationToken ct = default)
        {
            try
            {
                var json    = JsonConvert.SerializeObject(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                await _http.PostAsync($"{_baseUrl}/v1/feedback", content, ct);
            }
            catch
            {
                // Feedback is best-effort; don't surface errors to architect
            }
        }

        public void Dispose() => _http.Dispose();
    }

    public class GenerateRequest
    {
        [JsonProperty("prompt")]                 public string Prompt { get; set; } = "";
        [JsonProperty("project_category")]       public string? ProjectCategory { get; set; }
        [JsonProperty("site_context")]           public object SiteContext { get; set; } = new { };
        [JsonProperty("regulatory_constraints")] public object Regulatory { get; set; } = new { };
        [JsonProperty("design_intent")]          public object DesignIntent { get; set; } = new { };
        [JsonProperty("total_area_sqm")]         public double? TotalAreaSqm { get; set; }
        [JsonProperty("floor_count")]            public int FloorCount { get; set; } = 1;
        [JsonProperty("autocad_units")]          public string AutocadUnits { get; set; } = "mm";
        [JsonProperty("plugin_version")]         public string PluginVersion { get; set; } = "3.1";
        [JsonProperty("autocad_version")]        public string AutocadVersion { get; set; } = "";
    }

    public class FeedbackPayload
    {
        [JsonProperty("request_id")]          public string RequestId { get; set; } = "";
        [JsonProperty("architect_id")]        public string? ArchitectId { get; set; }
        [JsonProperty("selected_variation")]  public int SelectedVariation { get; set; }
        [JsonProperty("correction_type")]     public string CorrectionType { get; set; } = "accepted";
        [JsonProperty("corrected_dwg_notes")] public string Notes { get; set; } = "";
        [JsonProperty("severity")]            public string Severity { get; set; } = "minor";
    }
}
