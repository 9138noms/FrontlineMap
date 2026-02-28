using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace FrontlineMap
{
    [BepInPlugin("com.noms.frontlinemap", "FrontlineMap", "1.0.0")]
    public class FrontlineMapPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;

        // === Config (static so Helper can access) ===
        internal static ConfigEntry<int> cfgGridRes;
        internal static ConfigEntry<float> cfgUpdateInterval;
        internal static ConfigEntry<float> cfgInfluenceRadius;
        internal static ConfigEntry<float> cfgTerritoryAlpha;
        internal static ConfigEntry<KeyCode> cfgToggleKey;
        internal static ConfigEntry<float> cfgWeightAirbase;
        internal static ConfigEntry<float> cfgWeightShip;
        internal static ConfigEntry<float> cfgWeightVehicle;
        internal static ConfigEntry<float> cfgWeightAircraft;
        internal static ConfigEntry<bool> cfgUseFixedColors;
        internal static ConfigEntry<int> cfgSpreadFrames;

        static FrontlineHelper helperInstance;

        void Awake()
        {
            Log = Logger;

            cfgGridRes = Config.Bind("General", "GridResolution", 512,
                new ConfigDescription("Influence grid resolution", new AcceptableValueRange<int>(64, 2048)));
            cfgUpdateInterval = Config.Bind("General", "UpdateInterval", 5f,
                "Seconds between frontline recalculation");
            cfgInfluenceRadius = Config.Bind("General", "InfluenceRadius", 12000f,
                "How far each unit projects influence (meters)");
            cfgTerritoryAlpha = Config.Bind("Visual", "TerritoryAlpha", 0.012f,
                "Territory color overlay opacity (0-1)");
            cfgToggleKey = Config.Bind("General", "ToggleKey", KeyCode.F9,
                "Key to toggle frontline overlay");

            cfgWeightAirbase = Config.Bind("Weights", "Airbase", 10f, "Airbase influence weight");
            cfgWeightShip = Config.Bind("Weights", "Ship", 5f, "Ship influence weight");
            cfgWeightVehicle = Config.Bind("Weights", "Vehicle", 3f, "Ground vehicle influence weight");
            cfgWeightAircraft = Config.Bind("Weights", "Aircraft", 0.5f, "Aircraft influence weight");

            cfgUseFixedColors = Config.Bind("Visual", "UseFixedColors", true,
                "Use fixed Red/Blue colors instead of faction colors");

            cfgSpreadFrames = Config.Bind("Performance", "SpreadFrames", 10,
                new ConfigDescription(
                    "Spread influence computation across N frames (higher = less lag, slower update)",
                    new AcceptableValueRange<int>(1, 100)));

            // Create helper on scene load (same pattern as NOAIBridge)
            SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (helperInstance == null)
                {
                    var go = new GameObject("[FrontlineMapHelper]");
                    go.AddComponent<FrontlineHelper>();
                    Log.LogInfo($"[FLM] Helper created in scene: {scene.name}");
                }
            };

            Logger.LogInfo("FrontlineMap v1.0.0 loaded");
        }

        // ================================================================
        // Helper MonoBehaviour - runs on a DontDestroyOnLoad object
        // ================================================================
        internal class FrontlineHelper : MonoBehaviour
        {
            private GameObject overlayGO;
            private RawImage overlayImage;
            private Texture2D tex;
            private Color32[] pixels;
            private float[,] influenceGrid;
            private float[,] prevInfluenceGrid;
            private bool hasPrevGrid;
            private bool showOverlay = true;
            private int gridRes;
            private float lastUpdateTime;
            private int updateCount;
            private bool computing;

            // Faction cache
            private FactionHQ hq0;
            private FactionHQ hq1;
            private Color color0;
            private Color color1;

            // Cached unit data for coroutine
            private struct UnitStamp
            {
                public float x, z, weightedSign;
            }
            private List<UnitStamp> stampList = new List<UnitStamp>(512);

            void Awake()
            {
                DontDestroyOnLoad(gameObject);
                helperInstance = this;
                gridRes = cfgGridRes.Value;
                pixels = new Color32[gridRes * gridRes];
                influenceGrid = new float[gridRes, gridRes];
                prevInfluenceGrid = new float[gridRes, gridRes];
                Log.LogInfo("[FLM] Helper Awake + DontDestroyOnLoad");
            }

            void OnDestroy()
            {
                Log.LogInfo("[FLM] Helper destroyed");
                helperInstance = null;
                if (overlayGO != null) Destroy(overlayGO);
                if (tex != null) Destroy(tex);
            }

            void Update()
            {
                try
                {
                    if (Input.GetKeyDown(cfgToggleKey.Value))
                    {
                        showOverlay = !showOverlay;
                        if (overlayGO != null) overlayGO.SetActive(showOverlay);
                        Log.LogInfo($"[FLM] Overlay: {(showOverlay ? "ON" : "OFF")}");
                    }
                }
                catch { }

                if (!showOverlay) return;
                if (computing) return;
                if (Time.time - lastUpdateTime < cfgUpdateInterval.Value) return;
                lastUpdateTime = Time.time;

                try
                {
                    if (!EnsureOverlay())
                    {
                        if (updateCount % 10 == 0) Log.LogWarning("[FLM] EnsureOverlay failed");
                        updateCount++;
                        return;
                    }
                    if (!ResolveFactions())
                    {
                        if (updateCount % 10 == 0) Log.LogWarning("[FLM] ResolveFactions failed");
                        updateCount++;
                        return;
                    }
                    StartCoroutine(ComputeAndRenderAsync());
                }
                catch (Exception e)
                {
                    if (updateCount % 10 == 0)
                        Log.LogError($"[FLM] Error: {e}");
                    updateCount++;
                }
            }

            IEnumerator ComputeAndRenderAsync()
            {
                computing = true;

                // Swap grids
                var temp = prevInfluenceGrid;
                prevInfluenceGrid = influenceGrid;
                influenceGrid = temp;
                Array.Clear(influenceGrid, 0, gridRes * gridRes);

                // Phase 1: Collect unit data (fast, one frame)
                CollectUnitData();
                yield return null;

                // Phase 2: Stamp influence spread across frames
                DynamicMap map = null;
                try { map = SceneSingleton<DynamicMap>.i; } catch { }
                float mapSize = (map != null) ? map.mapDimension : 100000f;
                if (mapSize <= 0f) mapSize = 100000f;

                float halfMap = mapSize * 0.5f;
                float cellSize = mapSize / gridRes;
                float radius = cfgInfluenceRadius.Value;
                float radiusSq = radius * radius;

                int spread = Mathf.Max(1, cfgSpreadFrames.Value);
                int unitsPerFrame = Mathf.Max(1, stampList.Count / spread);
                for (int i = 0; i < stampList.Count; i++)
                {
                    var s = stampList[i];
                    StampInfluence(s.x, s.z, s.weightedSign, halfMap, cellSize, radiusSq);

                    if ((i + 1) % unitsPerFrame == 0)
                        yield return null;
                }
                yield return null;

                // Phase 3: Render pixels spread across frames
                RenderToTextureAsync();
                yield return null;

                // Phase 4: Upload to GPU
                tex.SetPixels32(pixels);
                tex.Apply(false);

                hasPrevGrid = true;
                if (updateCount % 5 == 0)
                    Log.LogInfo($"[FLM] Tick {updateCount} OK");
                updateCount++;
                computing = false;
            }

            // ==================== Setup ====================

            bool EnsureOverlay()
            {
                DynamicMap map;
                try { map = SceneSingleton<DynamicMap>.i; }
                catch { return false; }
                if (map == null || map.mapImage == null) return false;

                if (overlayGO != null) return true;

                // Child of mapImage → auto zoom/pan/move
                overlayGO = new GameObject("FrontlineOverlay");
                overlayGO.transform.SetParent(map.mapImage.transform, false);

                overlayImage = overlayGO.AddComponent<RawImage>();
                overlayImage.raycastTarget = false;

                tex = new Texture2D(gridRes, gridRes, TextureFormat.RGBA32, false);
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                overlayImage.texture = tex;

                // Stretch to fill parent
                RectTransform rt = overlayGO.GetComponent<RectTransform>();
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;

                Log.LogInfo("[FLM] Overlay created as child of mapImage");
                return true;
            }

            bool ResolveFactions()
            {
                var factions = FactionRegistry.factions;
                if (factions == null || factions.Count < 2) return false;

                hq0 = null; hq1 = null;
                color0 = Color.blue; color1 = Color.red;

                FactionHQ localHq = null;
                try { GameManager.GetLocalHQ(out localHq); } catch { }

                for (int i = 0; i < factions.Count; i++)
                {
                    Faction f = factions[i];
                    FactionHQ fhq = FactionRegistry.HQFromFaction(f);
                    if (fhq == null) continue;
                    if (hq0 == null) { hq0 = fhq; if (!cfgUseFixedColors.Value) color0 = f.color; }
                    else if (hq1 == null) { hq1 = fhq; if (!cfgUseFixedColors.Value) color1 = f.color; break; }
                }

                // Fixed colors: friendly=blue, enemy=red based on local player
                if (cfgUseFixedColors.Value && localHq != null && hq0 != null && hq1 != null)
                {
                    if (localHq == hq0)
                    {
                        color0 = Color.blue; color1 = Color.red;
                    }
                    else if (localHq == hq1)
                    {
                        color0 = Color.red; color1 = Color.blue;
                    }
                }

                return hq0 != null && hq1 != null;
            }

            // ==================== Influence ====================

            void CollectUnitData()
            {
                stampList.Clear();

                FactionHQ localHq = null;
                try { GameManager.GetLocalHQ(out localHq); } catch { }

                foreach (Unit unit in UnitRegistry.allUnits)
                {
                    if (unit == null) continue;
                    if (unit.unitState != Unit.UnitState.Active &&
                        unit.unitState != Unit.UnitState.Damaged) continue;

                    FactionHQ hq = unit.NetworkHQ;
                    if (hq == null) continue;

                    float sign;
                    if (hq == hq0) sign = 1f;
                    else if (hq == hq1) sign = -1f;
                    else continue;

                    if (localHq != null && hq != localHq)
                    {
                        if (!localHq.IsTargetBeingTracked(unit)) continue;
                    }

                    float weight = GetUnitWeight(unit);
                    if (weight <= 0f) continue;

                    GlobalPosition gp = unit.GlobalPosition();
                    stampList.Add(new UnitStamp { x = gp.x, z = gp.z, weightedSign = sign * weight });
                }

                // Airbases
                try
                {
                    foreach (var kvp in FactionRegistry.airbaseLookup)
                    {
                        Airbase ab = kvp.Value;
                        if (ab == null) continue;
                        FactionHQ hq = ab.CurrentHQ;
                        if (hq == null) continue;

                        float sign;
                        if (hq == hq0) sign = 1f;
                        else if (hq == hq1) sign = -1f;
                        else continue;

                        Vector3 pos = ab.transform.position;
                        stampList.Add(new UnitStamp { x = pos.x, z = pos.z, weightedSign = sign * cfgWeightAirbase.Value });
                    }
                }
                catch { }
            }

            void StampInfluence(float worldX, float worldZ, float weightedSign,
                float halfMap, float cellSize, float radiusSq)
            {
                float radius = Mathf.Sqrt(radiusSq);
                int minGX = Mathf.Max(0, Mathf.FloorToInt((worldX - radius + halfMap) / cellSize));
                int maxGX = Mathf.Min(gridRes - 1, Mathf.CeilToInt((worldX + radius + halfMap) / cellSize));
                int minGZ = Mathf.Max(0, Mathf.FloorToInt((worldZ - radius + halfMap) / cellSize));
                int maxGZ = Mathf.Min(gridRes - 1, Mathf.CeilToInt((worldZ + radius + halfMap) / cellSize));

                for (int gx = minGX; gx <= maxGX; gx++)
                {
                    float wx = (gx + 0.5f) * cellSize - halfMap;
                    float dx = wx - worldX;
                    float dxSq = dx * dx;

                    for (int gz = minGZ; gz <= maxGZ; gz++)
                    {
                        float wz = (gz + 0.5f) * cellSize - halfMap;
                        float dz = wz - worldZ;
                        float distSq = dxSq + dz * dz;

                        if (distSq < radiusSq)
                        {
                            float t = 1f - distSq / radiusSq;
                            influenceGrid[gx, gz] += weightedSign * t;
                        }
                    }
                }
            }

            float GetUnitWeight(Unit unit)
            {
                if (unit is Aircraft) return cfgWeightAircraft.Value;
                if (unit is Ship) return cfgWeightShip.Value;
                return cfgWeightVehicle.Value;
            }

            // ==================== Rendering ====================

            void RenderToTextureAsync()
            {
                float territoryAlpha = Mathf.Clamp01(cfgTerritoryAlpha.Value);
                Color32 clear = new Color32(0, 0, 0, 0);

                byte c0r = (byte)(Mathf.Clamp01(color0.r) * 255);
                byte c0g = (byte)(Mathf.Clamp01(color0.g) * 255);
                byte c0b = (byte)(Mathf.Clamp01(color0.b) * 255);
                byte c1r = (byte)(Mathf.Clamp01(color1.r) * 255);
                byte c1g = (byte)(Mathf.Clamp01(color1.g) * 255);
                byte c1b = (byte)(Mathf.Clamp01(color1.b) * 255);

                float lineWidthSq = 0.7f * 0.7f;
                float dashThreshSq = 0.15f * 0.15f;

                for (int x = 0; x < gridRes; x++)
                {
                    for (int y = 0; y < gridRes; y++)
                    {
                        float val = influenceGrid[x, y];
                        int idx = y * gridRes + x;
                        float absVal = val > 0 ? val : -val;

                        if (absVal > 5f)
                        {
                            float strength = absVal * 0.02f;
                            if (strength > 1f) strength = 1f;
                            byte a = (byte)(strength * territoryAlpha * 255f);
                            if (a < 2) { pixels[idx] = clear; }
                            else if (val > 0)
                                pixels[idx] = new Color32(c0r, c0g, c0b, a);
                            else
                                pixels[idx] = new Color32(c1r, c1g, c1b, a);
                            continue;
                        }

                        float gx = 0f, gy = 0f;
                        if (x > 0 && x < gridRes - 1)
                            gx = influenceGrid[x + 1, y] - influenceGrid[x - 1, y];
                        if (y > 0 && y < gridRes - 1)
                            gy = influenceGrid[x, y + 1] - influenceGrid[x, y - 1];
                        float gradSq = gx * gx + gy * gy;

                        float lineAlpha = 0f;
                        if (gradSq > 0.000001f)
                        {
                            float distSq = (absVal * absVal) / gradSq;
                            if (distSq < lineWidthSq)
                            {
                                lineAlpha = 1f - distSq / lineWidthSq;
                                if (gradSq < dashThreshSq && (x + y) % 8 < 4)
                                    lineAlpha = 0f;
                            }
                        }

                        if (lineAlpha > 0.01f)
                        {
                            byte a = (byte)(lineAlpha * lineAlpha * 255f);
                            if (val > 0)
                                pixels[idx] = new Color32(c0r, c0g, c0b, a);
                            else
                                pixels[idx] = new Color32(c1r, c1g, c1b, a);
                        }
                        else if (absVal > 0.01f)
                        {
                            float strength = absVal * 0.02f;
                            if (strength > 1f) strength = 1f;
                            byte a = (byte)(strength * territoryAlpha * 255f);
                            if (a < 2) { pixels[idx] = clear; continue; }

                            if (val > 0)
                                pixels[idx] = new Color32(c0r, c0g, c0b, a);
                            else
                                pixels[idx] = new Color32(c1r, c1g, c1b, a);
                        }
                        else
                        {
                            pixels[idx] = clear;
                        }
                    }
                }

                // Shift indicators
                if (hasPrevGrid)
                {
                    int step = gridRes / 64;
                    if (step < 4) step = 4;
                    for (int x = 2; x < gridRes - 2; x += step)
                    {
                        for (int y = 2; y < gridRes - 2; y += step)
                        {
                            float val = influenceGrid[x, y];

                            bool isFrontline = ZeroCross(val, influenceGrid[x - 1, y])
                                || ZeroCross(val, influenceGrid[x + 1, y])
                                || ZeroCross(val, influenceGrid[x, y - 1])
                                || ZeroCross(val, influenceGrid[x, y + 1]);
                            if (!isFrontline) continue;

                            float shift = influenceGrid[x, y] - prevInfluenceGrid[x, y];
                            if (Mathf.Abs(shift) < 0.05f) continue;

                            float ggx = influenceGrid[x + 1, y] - influenceGrid[x - 1, y];
                            float ggy = influenceGrid[x, y + 1] - influenceGrid[x, y - 1];
                            float gmSq = ggx * ggx + ggy * ggy;
                            if (gmSq < 0.0001f) continue;

                            float invGm = 1f / Mathf.Sqrt(gmSq);
                            float ndx = ggx * invGm;
                            float ndy = ggy * invGm;

                            int sign = shift > 0 ? 1 : -1;
                            Color32 shiftCol = shift > 0
                                ? new Color32(c0r, c0g, c0b, 200)
                                : new Color32(c1r, c1g, c1b, 200);

                            int chevLen = gridRes / 128;
                            if (chevLen < 3) chevLen = 3;
                            for (int s = 1; s <= chevLen; s++)
                            {
                                int px = x + Mathf.RoundToInt(ndx * s * sign);
                                int py = y + Mathf.RoundToInt(ndy * s * sign);
                                SetPixelSafe(px, py, shiftCol);
                                if (s < chevLen)
                                {
                                    SetPixelSafe(px + Mathf.RoundToInt(-ndy), py + Mathf.RoundToInt(ndx), shiftCol);
                                    SetPixelSafe(px + Mathf.RoundToInt(ndy), py + Mathf.RoundToInt(-ndx), shiftCol);
                                }
                            }
                        }
                    }
                }
            }

            void SetPixelSafe(int x, int y, Color32 col)
            {
                if (x >= 0 && x < gridRes && y >= 0 && y < gridRes)
                    pixels[y * gridRes + x] = col;
            }

            static bool ZeroCross(float a, float b)
            {
                return (a > 0f && b < 0f) || (a < 0f && b > 0f);
            }
        }
    }
}
