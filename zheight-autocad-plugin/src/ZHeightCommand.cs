// ZHeightCommand.cs
// Fix: _pendingPlan must be a plain static (not [ThreadStatic]).
// [ThreadStatic] means each thread has its own copy — setting it on the
// Dispatcher thread then reading it in the ZHEIGHT_DRAW CommandMethod
// (a different thread) always returns null.
//
// Flow: Task.Run calls API → sets _pendingPlan → SendStringToExecute queues
// ZHEIGHT_DRAW → AutoCAD main thread runs ZHEIGHT_DRAW → reads _pendingPlan.
// SendStringToExecute is thread-safe; no Dispatcher.Invoke needed.

using System;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using zHeight.Plugin.Client;
using zHeight.Plugin.Config;
using zHeight.Plugin.Engine;
using zHeight.Plugin.Solver;
using zHeight.Plugin.Models;

[assembly: CommandClass(typeof(zHeight.Plugin.ZHeightCommand))]

namespace zHeight.Plugin
{
    public class ZHeightCommand
    {
        // Plain statics — visible across ALL threads (single-user desktop app, safe).
        private static DrawActionPlan?   _pendingPlan;
        private static SiteConstraints?  _pendingSiteConstraints;

        // ── ZHEIGHT_SETUP ─────────────────────────────────────────────────────
        [CommandMethod("ZHEIGHT_SETUP", CommandFlags.Modal)]
        public void Setup()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;

            ed.WriteMessage("\n[zHeight] Configuration setup");

            var urlPrompt = new PromptStringOptions(
                "\nEnter API base URL (e.g. https://rag-api-583598998751.us-central1.run.app): ")
            { AllowSpaces = true };
            var urlResult = ed.GetString(urlPrompt);
            if (urlResult.Status != PromptStatus.OK) return;

            var keyPrompt = new PromptStringOptions("\nEnter API key: ")
            { AllowSpaces = false };
            var keyResult = ed.GetString(keyPrompt);
            if (keyResult.Status != PromptStatus.OK) return;

            var idPrompt = new PromptStringOptions(
                "\nEnter your architect ID (optional, press Enter to skip): ")
            { AllowSpaces = false };
            var idResult = ed.GetString(idPrompt);

            PluginConfig.Configure(
                urlResult.StringResult.Trim(),
                keyResult.StringResult.Trim(),
                idResult.Status == PromptStatus.OK
                    ? idResult.StringResult.Trim()
                    : "");

            ed.WriteMessage("\n[zHeight] Configuration saved.");
            ed.WriteMessage("\n[zHeight] Type ZHEIGHT to start generating layouts.");
        }

        // ── ZHEIGHT — main command ────────────────────────────────────────────
        [CommandMethod("ZHEIGHT", CommandFlags.Modal)]
        public void RunZHeight()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;

            var cfg = PluginConfig.Load();
            if (cfg.ApiKey == "REPLACE_WITH_ACTUAL_KEY" || string.IsNullOrEmpty(cfg.ApiKey))
            {
                ed.WriteMessage("\n[zHeight] Not configured. Run ZHEIGHT_SETUP first.");
                return;
            }

            var promptOpt = new PromptStringOptions(
                "\nDescribe the project (e.g. '3-bedroom house, 2500 sqft, open plan kitchen and living, suburban US'): ")
            { AllowSpaces = true };
            var promptResult = ed.GetString(promptOpt);
            if (promptResult.Status != PromptStatus.OK ||
                string.IsNullOrWhiteSpace(promptResult.StringResult))
            {
                ed.WriteMessage("\n[zHeight] Cancelled.");
                return;
            }

            string units = GetUnitsString(doc.Database);
            ed.WriteMessage($"\n[zHeight] Calling AI backend (units: {units})...");
            ed.WriteMessage("\n[zHeight] This takes 30-60 seconds. Please wait...");

            var request = new GenerateRequest
            {
                Prompt         = promptResult.StringResult.Trim(),
                AutocadUnits   = units,
                AutocadVersion = Application.Version.ToString(),
            };

            // Task.Run so the AutoCAD UI stays responsive during the API call.
            // _pendingPlan is plain static → visible to ZHEIGHT_DRAW on any thread.
            // SendStringToExecute is thread-safe → no Dispatcher needed.
            Task.Run(async () =>
            {
                try
                {
                    using var client = new ApiClient();
                    var plan = await client.GenerateAsync(request);

                    // Default site constraints (extend later via RequirementPanel UI)
                    var site = new SiteConstraints();
                    _pendingSiteConstraints = site;

                    // Run constraint solver on each variation
                    foreach (var v in plan.Variations)
                    {
                        var sr = ConstraintSolver.Validate(v, site);
                        if (sr.Warnings.Count > 0)
                            v.Warnings.AddRange(sr.Warnings);
                    }

                    // Store plan BEFORE queuing the draw command
                    _pendingPlan = plan;

                    // Queue ZHEIGHT_DRAW on AutoCAD's command thread
                    doc.SendStringToExecute("_.ZHEIGHT_DRAW\n", true, false, true);
                }
                catch (TimeoutException ex)
                {
                    doc.SendStringToExecute(
                        $"_.ZHEIGHT_MSG TIMEOUT\n", true, false, false);
                    _ = ex; // message shown in ZHEIGHT_MSG fallback
                    doc.Editor.WriteMessage($"\n[zHeight TIMEOUT] {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    doc.Editor.WriteMessage($"\n[zHeight ERROR] {ex.Message}");
                }
            });
        }

        // ── ZHEIGHT_DRAW — draws the last received plan ───────────────────────
        [CommandMethod("ZHEIGHT_DRAW", CommandFlags.Modal)]
        public void DrawPlan()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc.Editor;

            var plan = _pendingPlan;
            _pendingPlan = null;    // clear so a second call doesn't redraw

            if (plan == null)
            {
                ed.WriteMessage("\n[zHeight] No pending plan — generate one with ZHEIGHT first.");
                return;
            }

            ed.WriteMessage(
                $"\n[zHeight] Drawing {plan.Variations.Count} variations " +
                $"({plan.ProjectCategory}, {plan.ProjectDescription})...");

            foreach (var v in plan.Variations)
            {
                int acts  = v.Actions.Count;
                int warns = v.Warnings.Count;
                ed.WriteMessage(
                    $"\n  V{v.VariationId}: {v.VariationName} " +
                    $"— {acts} draw actions, {warns} warnings");
            }

            using var docLock = doc.LockDocument();

            try
            {
                var engine = new DrawingEngine(doc);
                // Fix #8: wire site constraints so GridLayoutEngine uses correct plot dims
                if (_pendingSiteConstraints != null)
                    engine.SetSiteConstraints(_pendingSiteConstraints);

                engine.ExecutePlan(plan);

                foreach (var w in plan.GlobalWarnings)
                    ed.WriteMessage($"\n[zHeight WARNING] {w}");

                ed.WriteMessage(
                    $"\n[zHeight] Layout complete. Request ID: {plan.RequestId}");
                ed.WriteMessage(
                    "\n[zHeight] Ctrl+Z undoes the entire generation in one step.");

                // Best-effort async feedback
                var requestId = plan.RequestId;
                var selected  = plan.RecommendedVariation;
                var archId    = PluginConfig.Load().ArchitectId;
                Task.Run(async () =>
                {
                    try
                    {
                        using var client = new ApiClient();
                        await client.SendFeedbackAsync(new FeedbackPayload
                        {
                            RequestId         = requestId,
                            ArchitectId       = archId,
                            SelectedVariation = selected,
                            CorrectionType    = "accepted",
                        });
                    }
                    catch { /* best-effort */ }
                });
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[zHeight ERROR] Draw failed: {ex.Message}");
            }
        }

        private static string GetUnitsString(
            Autodesk.AutoCAD.DatabaseServices.Database db) =>
            db.Insunits switch
            {
                Autodesk.AutoCAD.DatabaseServices.UnitsValue.Millimeters => "mm",
                Autodesk.AutoCAD.DatabaseServices.UnitsValue.Centimeters => "cm",
                Autodesk.AutoCAD.DatabaseServices.UnitsValue.Meters      => "m",
                Autodesk.AutoCAD.DatabaseServices.UnitsValue.Inches      => "in",
                Autodesk.AutoCAD.DatabaseServices.UnitsValue.Feet        => "ft",
                _                                                         => "mm",
            };
    }
}
