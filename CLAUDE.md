# zHeight AI AutoCAD Plugin — Project Guide for Claude Code

## What this repo is

Two linked products:

1. **`zheight-autocad-plugin/`** — C# .NET AutoCAD plugin that generates architectural floor-plan DWG entities from JSON
2. **`zheight-kb-services/`** — GCP-hosted Python/FastAPI backend (Cloud Run) that uses Gemini 2.5 Flash to produce the layout JSON

The plugin calls the backend, gets a `DrawActionPlan` JSON, and draws rooms, walls, doors, windows, hatches, setback lines in AutoCAD.

---

## Architecture

```
AutoCAD (user types ZHEIGHT)
  └─► ZHeightCommand.cs  (reads DPAPI config, sends prompt)
       └─► POST /generate  →  Cloud Run rag-api
            ├─ Gemini: extract intent (sqft, setbacks, rooms)
            ├─ Gemini: generate zones_json layout
            └─► DrawActionPlan JSON
  └─► DrawingEngine.cs    (draws entities in AutoCAD)
       └─► GridLayoutEngine.cs  (computes room XY positions)
```

---

## Key files

### Plugin (C#)
| File | Purpose |
|------|---------|
| `src/Commands/ZHeightCommand.cs` | AutoCAD command entry point (`ZHEIGHT`, `ZHEIGHT_SETUP`, `ZHEIGHT_DRAW`) |
| `src/Engine/DrawingEngine.cs` | Draws all AutoCAD entities — walls, doors, windows, hatches, labels, setback lines, enclair glass walls |
| `src/Solver/GridLayoutEngine.cs` | v6 wing-based layout engine — Living LEFT \| Hallway CENTER \| Bedroom RIGHT |
| `src/Models/DrawActionPlan.cs` | JSON model that matches backend response |
| `zheight-autocad-plugin.csproj` | Targets `net10.0-windows`, references AutoCAD 2027 DLLs |

### Backend (Python / FastAPI)
| File | Purpose |
|------|---------|
| `services/rag-api/app/routers/orchestrate.py` | Main `/generate` endpoint — intent extraction, layout generation, compile to plan |
| `services/rag-api/app/core/retriever.py` | pgvector similarity search for reference projects |
| `services/rag-api/app/main.py` | FastAPI app, auth middleware, Redis health |
| `shared/db/client.py` | asyncpg DB connection (URL-encoded password required) |
| `cloudbuild-ragapi.yaml` | Cloud Build config — run from repo root `zheight-kb-services/` |

---

## Build & deploy

### Build the plugin (close AutoCAD first!)
```powershell
cd "d:\AI Product\Plug-in Knowledgebase\zheight-autocad-plugin"
dotnet build zheight-autocad-plugin.csproj -c Release
# Then copy DLL to bundle:
Copy-Item bin\Release\net10.0-windows\zHeightPlugin.dll `
  "$env:APPDATA\Autodesk\ApplicationPlugins\AutoCAD_AI_LayoutDesigner.bundle\Contents\zHeightPlugin.dll" -Force
```

### Deploy backend to Cloud Run
```powershell
cd "d:\AI Product\Plug-in Knowledgebase\zheight-kb-services"
gcloud builds submit --config cloudbuild-ragapi.yaml --project=zheight-ai-kb
gcloud run deploy rag-api --image us-central1-docker.pkg.dev/zheight-ai-kb/zheight-services/rag-api:v2.0 --region us-central1 --project=zheight-ai-kb
```

### Check server health
```powershell
Invoke-RestMethod -Uri "https://rag-api-583598998751.us-central1.run.app/health"
```

### Check error logs
```powershell
gcloud logging read "resource.type=cloud_run_revision AND resource.labels.service_name=rag-api AND severity>=ERROR" --project=zheight-ai-kb --limit=5 --format="value(textPayload)"
```

---

## GCP project
- **Project ID**: `zheight-ai-kb`
- **Region**: `us-central1`
- **RAG API URL**: `https://rag-api-583598998751.us-central1.run.app`
- **API key secret**: `rag-api-key` in Secret Manager
- **Gemini model**: `gemini-2.5-flash` via Vertex AI endpoint

---

## Critical bugs fixed (do not regress)

### orchestrate.py
- **Null setbacks**: Use `(reg_ctx.get("front_setback_m") or 7.5) * 1000` — Gemini returns `null` for setbacks; `.get(key, default)` returns `None` because the key exists with a null value
- **Gemini list response**: After `json.loads(raw)`, check `isinstance(generation, list)` and unwrap `generation[0]` — Gemini sometimes wraps the JSON object in an array
- **sqft regex**: Require "lot/site/plot/land/property" nearby to avoid treating house area as lot size
- **Plot ratio**: `plot_w_m = sqrt(site_area_sqm / 2.0)` — gives 2:1 depth:width US lot shape

### GridLayoutEngine.cs (v6)
- **Wing orientation**: Living wing at `bx` (LEFT), bedroom wing at `bx + livWingW + hallW` (RIGHT)
- **Garage**: Only add if brief mentions "garage"/"car"/"parking"/"ADU" — do NOT auto-add for 2-bed CA urban
- **Enclair**: Recognized types: `enclair, covered_porch, sunroom, sun_room, lanai, veranda, covered_outdoor_room, conservatory, screened_porch, florida_room` — drawn with A-GLAZ triple-line glass walls
- **Bedroom row height cap**: primary ≤ `STRUCT * 4 * 1.15` (~5600mm), secondary ≤ `STRUCT * 3 * 1.15` (~4200mm)

### DrawingEngine.cs
- **CalculateOffsets**: Uses `PlotWidthMm` for variation spacing (not sqrt of area)
- **All 4 setback lines**: Front + rear + left side + right side drawn on lot boundary
- **Suite connecting doors**: bed↔bath pairs, foyer↔hallway with 4× tolerance on shared-wall detection
- **Hatch by type**: NET for baths/kitchen, ANSI37 for utility/garage, ANSI31 for bedrooms, none for open/living/enclair

---

## AutoCAD bundle location
```
%APPDATA%\Autodesk\ApplicationPlugins\AutoCAD_AI_LayoutDesigner.bundle\
├── PackageContents.xml        ← ModuleName = ./Contents/zHeightPlugin.dll
└── Contents\
    ├── zHeightPlugin.dll      ← copy here after every build
    └── AutoCAD_AI_LayoutDesigner.addin
```

## Plugin config (DPAPI encrypted)
```
%APPDATA%\zHeightPlugin\config.dat
```
Run `ZHEIGHT_SETUP` in AutoCAD to set the API URL and key — stored encrypted via Windows DPAPI.

---

## SDK notes
- Use `google-genai` (NOT `google-generativeai`)
- `thinking_config=types.ThinkingConfig(thinking_budget=0)` — required to prevent truncated JSON output
- Redis SSL: `ssl_cert_reqs="none"` (string), `ssl_check_hostname=False` via ConnectionPool — no `ssl_context` kwarg
- asyncpg: password must be `urllib.parse.quote(password, safe='')` encoded in the connection URL
- DB CAST: use `CAST(:param AS VECTOR)` not `::VECTOR` — asyncpg rejects `::TYPE` after named params
