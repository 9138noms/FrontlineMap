using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

namespace FrontlineMap
{
    [BepInPlugin("com.yuulf.frontlinemap", "FrontlineMap", "1.0.0")]
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

        static FrontlineHelper helperInstance;

        void Awake()
        {
            Log = Logger;

            cfgGridRes = Config.Bind("General", "GridResolution", 400,
                new ConfigDescription("Influence grid resolution", new AcceptableValueRange<int>(64, 512)));
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
            private float[,] prevInfluenceGrid;  // previous tick for shift detection
            private bool hasPrevGrid;
            private bool showOverlay = true;
            private int gridRes;
            private int frameCount;
            private int updateCount;

            // Faction cache
            private FactionHQ hq0;
            private FactionHQ hq1;
            private Color color0;
            private Color color1;

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
                frameCount++;

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
                if (frameCount % 300 != 0) return; // ~5s at 60fps

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
                    // Save previous grid for shift detection
                    Array.Copy(influenceGrid, prevInfluenceGrid, gridRes * gridRes);
                    ComputeInfluence();
                    RenderToTexture();
                    hasPrevGrid = true;

                    if (updateCount % 5 == 0)
                        Log.LogInfo($"[FLM] Tick {updateCount} OK");
                    updateCount++;
                }
                catch (Exception e)
                {
                    if (updateCount % 10 == 0)
                        Log.LogError($"[FLM] Error: {e}");
                    updateCount++;
                }
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

                for (int i = 0; i < factions.Count; i++)
                {
                    Faction f = factions[i];
                    FactionHQ fhq = FactionRegistry.HQFromFaction(f);
                    if (fhq == null) continue;
                    if (hq0 == null) { hq0 = fhq; color0 = f.color; }
                    else if (hq1 == null) { hq1 = fhq; color1 = f.color; break; }
                }

                return hq0 != null && hq1 != null;
            }

            // ==================== Influence ====================

            void ComputeInfluence()
            {
                Array.Clear(influenceGrid, 0, gridRes * gridRes);

                DynamicMap map = SceneSingleton<DynamicMap>.i;
                float mapSize = map.mapDimension;
                if (mapSize <= 0f) mapSize = 100000f;

                float halfMap = mapSize * 0.5f;
                float cellSize = mapSize / gridRes;
                float radius = cfgInfluenceRadius.Value;
                float radiusSq = radius * radius;

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

                    float weight = GetUnitWeight(unit);
                    if (weight <= 0f) continue;

                    GlobalPosition gp = unit.GlobalPosition();
                    StampInfluence(gp.x, gp.z, sign * weight, halfMap, cellSize, radius, radiusSq);
                }

                // Airbases (not in UnitRegistry)
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
                        StampInfluence(pos.x, pos.z, sign * cfgWeightAirbase.Value,
                            halfMap, cellSize, radius, radiusSq);
                    }
                }
                catch { }
            }

            void StampInfluence(float worldX, float worldZ, float weightedSign,
                float halfMap, float cellSize, float radius, float radiusSq)
            {
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
                            float t = 1f - Mathf.Sqrt(distSq) / radius;
                            influenceGrid[gx, gz] += weightedSign * t * t;
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

            void RenderToTexture()
            {
                float territoryAlpha = Mathf.Clamp01(cfgTerritoryAlpha.Value);
                Color32 clear = new Color32(0, 0, 0, 0);

                byte c0r = (byte)(Mathf.Clamp01(color0.r) * 255);
                byte c0g = (byte)(Mathf.Clamp01(color0.g) * 255);
                byte c0b = (byte)(Mathf.Clamp01(color0.b) * 255);
                byte c1r = (byte)(Mathf.Clamp01(color1.r) * 255);
                byte c1g = (byte)(Mathf.Clamp01(color1.g) * 255);
                byte c1b = (byte)(Mathf.Clamp01(color1.b) * 255);

                // First pass: clear + territory tint
                for (int x = 0; x < gridRes; x++)
                {
                    for (int y = 0; y < gridRes; y++)
                    {
                        float val = influenceGrid[x, y];
                        int idx = y * gridRes + x;

                        if (Mathf.Abs(val) > 0.01f)
                        {
                            float strength = Mathf.Clamp01(Mathf.Abs(val) * 0.02f);
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

                // Second pass: frontline with intensity-based dashing + thickness for minimap
                for (int x = 1; x < gridRes - 1; x++)
                {
                    for (int y = 1; y < gridRes - 1; y++)
                    {
                        float val = influenceGrid[x, y];

                        bool isFrontline = ZeroCross(val, influenceGrid[x - 1, y])
                            || ZeroCross(val, influenceGrid[x + 1, y])
                            || ZeroCross(val, influenceGrid[x, y - 1])
                            || ZeroCross(val, influenceGrid[x, y + 1]);

                        if (!isFrontline) continue;

                        // Gradient magnitude = conflict intensity
                        float gx = influenceGrid[x + 1, y] - influenceGrid[x - 1, y];
                        float gy = influenceGrid[x, y + 1] - influenceGrid[x, y - 1];
                        float gradMag = Mathf.Sqrt(gx * gx + gy * gy);

                        // Feature 2: dashed line for weak conflicts, solid for strong
                        // gradMag < 0.3 = weak → dash pattern (skip every 3rd pixel)
                        // gradMag >= 0.3 = strong → solid line
                        bool dashed = gradMag < 0.3f;
                        if (dashed && ((x + y) % 4 < 2)) continue; // skip = gap in dash

                        Color32 lineCol = new Color32(255, 255, 255, 255);

                        // 1px at 400 res, bilinear filter smooths it
                        SetPixelSafe(x, y, lineCol);
                    }
                }

                // Third pass: frontline shift indicators (Feature 5)
                if (hasPrevGrid)
                {
                    for (int x = 2; x < gridRes - 2; x += 3)
                    {
                        for (int y = 2; y < gridRes - 2; y += 3)
                        {
                            float val = influenceGrid[x, y];

                            bool isFrontline = ZeroCross(val, influenceGrid[x - 1, y])
                                || ZeroCross(val, influenceGrid[x + 1, y])
                                || ZeroCross(val, influenceGrid[x, y - 1])
                                || ZeroCross(val, influenceGrid[x, y + 1]);
                            if (!isFrontline) continue;

                            // Compare influence shift: positive shift = faction0 advancing
                            float shift = influenceGrid[x, y] - prevInfluenceGrid[x, y];
                            if (Mathf.Abs(shift) < 0.05f) continue; // no significant change

                            // Draw small colored dot on advancing side
                            // Faction0 advancing (shift > 0): dot in faction0 color, offset into faction1 territory
                            // Faction1 advancing (shift < 0): dot in faction1 color, offset into faction0 territory
                            float gx = influenceGrid[x + 1, y] - influenceGrid[x - 1, y];
                            float gy = influenceGrid[x, y + 1] - influenceGrid[x, y - 1];
                            float gm = Mathf.Sqrt(gx * gx + gy * gy);
                            if (gm < 0.01f) continue;

                            float ndx = gx / gm;
                            float ndy = gy / gm;

                            // Offset 2-3 pixels in push direction
                            int sign = shift > 0 ? 1 : -1;
                            Color32 shiftCol = shift > 0
                                ? new Color32(c0r, c0g, c0b, 180)
                                : new Color32(c1r, c1g, c1b, 180);

                            // Draw a small 3px chevron pointing in advance direction
                            for (int s = 1; s <= 3; s++)
                            {
                                int px = x + Mathf.RoundToInt(ndx * s * sign);
                                int py = y + Mathf.RoundToInt(ndy * s * sign);
                                SetPixelSafe(px, py, shiftCol);
                                // Perpendicular spread for visibility
                                int px1 = px + Mathf.RoundToInt(-ndy);
                                int py1 = py + Mathf.RoundToInt(ndx);
                                int px2 = px + Mathf.RoundToInt(ndy);
                                int py2 = py + Mathf.RoundToInt(-ndx);
                                if (s < 3)
                                {
                                    SetPixelSafe(px1, py1, shiftCol);
                                    SetPixelSafe(px2, py2, shiftCol);
                                }
                            }
                        }
                    }
                }

                tex.SetPixels32(pixels);
                tex.Apply();
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
