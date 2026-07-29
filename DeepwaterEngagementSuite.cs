using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeepwaterEngagementSuite.PathPlannerData;
using DeepwaterEngagementSuite.VoyagePlannerData;
using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Nodes;
using GameOffsets.Native;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using SixLabors.PolygonClipper;
using Direction = DeepwaterEngagementSuite.VoyagePlannerData.Direction;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite : BaseSettingsPlugin<DeepwaterEngagementSuiteSettings>
{
    private readonly ConcurrentDictionary<HashSet<(Vector2i, float)>, Polygon> _shapeCache = new(HashSet<(Vector2i,float)>.CreateSetComparer());

    private const string TextureName = "Icons.png";

    private const float GridToWorldMultiplier = 250 / 23f;

    private readonly Dictionary<uint, EntityCacheItem> _cachedEntities = new Dictionary<uint, EntityCacheItem>();
    private readonly ConcurrentDictionary<string, ExpeditionEntityType> _entityTypeCache = new();
    private bool _largeMapOpen;
    private Vector2 _playerGridPos;
    private float _bubbleRadius;
    private PathPlannerRunner _plannerRunner;
    private bool _zoneCleared;
    private int[][] _pathfindingData;
    private Vector2i _areaDimensions;
    private List<float> _scoreHistory = [];
    private List<Vector2i> _editedPath;
    private int? _editedIndex = null;
    private PathPlanner.DetailedLootScore _editedPathEval;
    private Polygon _placedBubblePolygon;

    private PathPlanner.DetailedLootScore EditedOrNativeScore => _editedPathEval ?? _plannerRunner?.CurrentBestPath;

    private Camera Camera => GameController.Game.IngameState.Camera;

    private int PlacedLanternCount => Handler.PlacedLanternCount;
    private List<(Vector2i Position, float Radius)> Bubbles => Handler.Bubbles.Select(x=>(x.Position, x.Radius)).ToList();

    private Vector2i? PlacementIndicatorPos => Handler.PlacementIndicator?.GridPosNum.TruncateToVector2I();

    private DeepwaterHandler Handler => GameController.IngameState.ServerData.DeepwaterHandler;
    private bool _initialized;

    public override bool Initialise()
    {
        InitOnce();
        _profilesDirectory = Path.Combine(ConfigDirectory, "profiles");
        Directory.CreateDirectory(_profilesDirectory);
        EnsureDefaultProfile();
        LoadProfiles();
        Graphics.InitImage(TextureName);
        Settings.PlannerSettings.StartSearch.OnPressed += StartSearch;
        Settings.PlannerSettings.StopSearch.OnPressed += StopSearch;
        Settings.PlannerSettings.ClearSearch.OnPressed += ClearSearch;
        Settings.VoyageSettings.AddProfile.OnPressed += OnAddProfile;
        Settings.VoyageSettings.ReloadProfiles.OnPressed += OnReloadProfiles;
        Settings.VoyageSettings.DeleteCurrentProfile.OnPressed += OnDeleteCurrentProfile;
        Settings.VoyageSettings.ProfileSelector.OnValueSelected += OnProfileSelected;
        Settings.VoyageSettings.ProfileSelector.Values = Settings.VoyageSettings.Profiles.Select(p => p.Name).ToList();
        if (Settings.VoyageSettings.Profiles.Count > 0)
        {
            ApplyProfile(Settings.VoyageSettings.Profiles[0].Name);
        }
        Settings.VoyageSettings.ProfileRenameNode.DrawDelegate = DrawProfileRenameNode;
        RegisterHotkey(Settings.PlannerSettings.StartSearchHotkey);
        RegisterHotkey(Settings.PlannerSettings.StopSearchHotkey);
        RegisterHotkey(Settings.PlannerSettings.ClearSearchHotkey);
        return base.Initialise();
    }

    public override void OnSaveSettings()
    {
        SyncCurrentProfileToMemory();
        SaveProfiles();
    }

    private static void RegisterHotkey(HotkeyNodeV2 hotkey)
    {
        Input.RegisterKey(hotkey.Value);
        hotkey.OnValueChanged += () => { Input.RegisterKey(hotkey.Value); };
    }

    private void StopSearch()
    {
        if (_plannerRunner is { } run)
        {
            run.Stop();
            Settings.PlannerSettings.SearchState = SearchState.Stopped;
        }
        else
        {
            Settings.PlannerSettings.SearchState = SearchState.Empty;
        }
    }

    private void StartSearch()
    {
        _scoreHistory = [];
        _plannerRunner?.Stop();
        var plannerRunner = new PathPlannerRunner();
        plannerRunner.Start(Settings.PlannerSettings, PlannerEnvironment, GameController.SoundController);
        _plannerRunner = plannerRunner;
        Settings.PlannerSettings.SearchState = SearchState.Searching;
    }

    private void ClearSearch()
    {
        if (_plannerRunner is { } run)
        {
            run.Stop();
            _plannerRunner = null;
            _scoreHistory = [];
            _editedPath = null;
            _editedIndex = null;
            _editedPathEval = null;
        }
    }

    public override void AreaChange(AreaInstance area)
    {
        _plannerRunner?.Stop();
        _plannerRunner = null;
        _scoreHistory = [];
        _editedPath = null;
        _editedIndex = null;
        _editedPathEval = null;
        _cachedEntities.Clear();
        _zoneCleared = false;
        _pathfindingData = GameController.IngameState.Data.RawPathfindingData;
        _areaDimensions = GameController.IngameState.Data.AreaDimensions;
        _shapeCache.Clear();
        _rerollCount = 0;
        _lastBorderKey = null;
        _baselineScore = null;
        _baselineKey = null;
    }

    private ExpeditionEntityType GetEntityType(string path)
    {
        return _entityTypeCache.GetOrAdd(path, p => p switch
        {
            var a when a.StartsWith("Metadata/Chests/LeagueDeepwater/", StringComparison.Ordinal) => ExpeditionEntityType.Marker,
            var a when a.StartsWith("Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterIzaroObject", StringComparison.Ordinal) => ExpeditionEntityType.Marker,
            var a when a.StartsWith("Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterAltarCrab", StringComparison.Ordinal) => ExpeditionEntityType.Marker,
            var a when a.StartsWith("Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterAltarOctopus", StringComparison.Ordinal) => ExpeditionEntityType.Marker,
            var a when a.StartsWith("Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterTormentedSpiritEncounter", StringComparison.Ordinal) => ExpeditionEntityType.Marker,
            var a when a.StartsWith("Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterCursedDucatDrop", StringComparison.Ordinal) => ExpeditionEntityType.Marker,
            var a when a.StartsWith("Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterLanternReplenishEncounter", StringComparison.Ordinal) => ExpeditionEntityType.Marker,
            _ => ExpeditionEntityType.None,
        });
    }

    private static IconPickerIndex GetChestType(string path) => path switch
    {
        var p when p.Contains("BottledItemChest", StringComparison.Ordinal) => IconPickerIndex.BottledItemChest,
        var p when p.Contains("ClamTreasureChest", StringComparison.Ordinal) => IconPickerIndex.ClamTreasureChest,
        var p when p.Contains("CurrencyTreasureChest", StringComparison.Ordinal) => IconPickerIndex.CurrencyTreasureChest,
        var p when p.Contains("DeepwaterAnchorUniqueWeapon", StringComparison.Ordinal) => IconPickerIndex.UniqueWeaponChest,
        var p when p.Contains("DeepwaterAnchorUniqueArmour", StringComparison.Ordinal) => IconPickerIndex.UniqueArmourChest,
        var p when p.Contains("DeepwaterChestScarabs", StringComparison.Ordinal) => IconPickerIndex.ScarabChest,
        var p when p.Contains("DeepwaterChestStackedDecks", StringComparison.Ordinal) => IconPickerIndex.StackedDecksChest,
        var p when p.Contains("DeepwaterChestMaps", StringComparison.Ordinal) => IconPickerIndex.MapsChest,
        var p when p.Contains("DeepwaterChestAllflameEmbers", StringComparison.Ordinal) => IconPickerIndex.AllflameEmbersChest,
        var p when p.Contains("GoldTreasureChest", StringComparison.Ordinal) => IconPickerIndex.GoldTreasureChest,
        var p when p.Contains("DeepwaterCursedDucatDrop", StringComparison.Ordinal) => IconPickerIndex.CursedDucatDrop,
        var p when p.Contains("RandomDucatChest", StringComparison.Ordinal) => IconPickerIndex.RandomDucatChest,
        var p when p.Contains("DeepwaterIzaroObject", StringComparison.Ordinal) => IconPickerIndex.IzaroObject,
        var p when p.Contains("DeepwaterAltarCrab", StringComparison.Ordinal) => IconPickerIndex.AltarCrab,
        var p when p.Contains("DeepwaterAltarOctopus", StringComparison.Ordinal) => IconPickerIndex.AltarOctopus,
        var p when p.Contains("DeepwaterTormentedSpiritEncounter", StringComparison.Ordinal) => IconPickerIndex.TormentedSpiritEncounter,
        var p when p.Contains("DeepwaterLanternReplenishEncounter", StringComparison.Ordinal) => IconPickerIndex.LanternReplenishEncounter,
        _ => IconPickerIndex.OtherChests,
    };

    private Vector3 ExpandWithTerrainHeight(Vector2 gridPosition)
    {
        return new Vector3(gridPosition.GridToWorld(), GameController.IngameState.Data.GetTerrainHeightAt(gridPosition));
    }

    private void DrawCirclesInWorld(List<Vector3> positions, float radius, Color color)
    {
        const int segments = 90;
        const int segmentSpan = 360 / segments;
        var playerPos = GameController.Player?.GetComponent<Positioned>()?.WorldPosNum;
        if (playerPos == null)
        {
            return;
        }

        foreach (var position in positions
                     .Where(x => playerPos.Value.Distance(new Vector2(x.X, x.Y)) < 80 * GridToWorldMultiplier + radius))
        {
            foreach (var segmentId in Enumerable.Range(0, segments))
            {
                (Vector2, Vector2) GetVector(int i)
                {
                    var (sin, cos) = MathF.SinCos(MathF.PI / 180 * i);
                    var offset = new Vector2(cos, sin) * radius;
                    var xy = position.Xy() + offset;
                    var screen = Camera.WorldToScreen(ExpandWithTerrainHeight(xy.WorldToGrid()));
                    return (xy, screen);
                }

                var segmentOrigin = segmentId * segmentSpan;
                var (w1, c1) = GetVector(segmentOrigin);
                var (w2, c2) = GetVector(segmentOrigin + segmentSpan);
                if (Settings.BubbleSettings.EnableBubbleRadiusMerging)
                {
                    if (positions
                        .Where(x => x != position)
                        .Select(x => new Vector2(x.X, x.Y))
                        .Any(x => Vector2.Distance(w1, x) < radius &&
                                  Vector2.Distance(w2, x) < radius))
                    {
                        continue;
                    }
                }

                Graphics.DrawLine(c1, c2, 1, color);
            }
        }
    }

    public override Job Tick()
    {
        if (Handler == null)
        {
            return null;
        }

        Settings.PlannerSettings.SearchState = _plannerRunner switch
        {
            { IsRunning: true } => SearchState.Searching,
            { IsRunning: false, CurrentBestPath.PerPointScore.Count: > 0 } => SearchState.Stopped,
            _ => SearchState.Empty
        };

        var playerGridPos = GameController.Player?.GetComponent<Positioned>()?.WorldPosNum.WorldToGrid();
        if (playerGridPos == null)
        {
            return null;
        }

        _playerGridPos = playerGridPos.Value;

        var ingameUi = GameController.Game.IngameState.IngameUi;
        var map = ingameUi.Map;
        var largeMap = map.LargeMap.AsObject<SubMap>();
        _largeMapOpen = largeMap.IsVisible;

        _bubbleRadius = Settings.BubbleSettings.BubbleRadiusOverride.Value is > 0 and var o ? o : Bubbles.Min(x => x.Radius);

        foreach (var entity in new[] { EntityType.Chest, EntityType.Terrain, EntityType.IngameIcon }
                     .SelectMany(x => GameController.EntityListWrapper.ValidEntitiesByType[x]))
        {
            if (GetEntityType(entity.Path) == ExpeditionEntityType.None)
                continue;

            if (entity.IsOpened)
            {
                _cachedEntities.Remove(entity.Id);
                continue;
            }

            var newValue = BuildCacheItem(entity);
            _cachedEntities[entity.Id] = _cachedEntities.TryGetValue(entity.Id, out var oldValue)
                ? oldValue.Merge(newValue)
                : newValue;
        }

        return null;
    }

    private ExpeditionEnvironment PlannerEnvironment => BuildEnvironment();

    private ExpeditionEnvironment BuildEnvironment()
    {
        var loot = new List<(Vector2, IExpeditionLoot)>();
        foreach (var e in _cachedEntities.Values)
        {
            if (e.IsOpened)
                continue;

            switch (GetEntityType(e.Path))
            {
                case ExpeditionEntityType.Marker:
                {
                    loot.Add((e.GridPos, new PathPlannerData.Chest(GetChestType(e.Path))));
                    continue;
                }
            }
        }

        return new ExpeditionEnvironment(
            loot.FindAll(x => x.Item2 != null),
            Bubbles.Min(x => x.Radius),
            Handler.MaxLanternCount-Handler.PlacedLanternCount,
            IsValidPlacement,
            Bubbles);
    }

    private bool IsValidPlacement(Vector2 x)
    {
        return x.X >= 0 && x.Y >= 0 &&
               x.X < _areaDimensions.X &&
               x.Y < _areaDimensions.Y &&
               _pathfindingData[(int)x.Y][(int)x.X] > 3;
    }

    public override void Render()
    {
        DrawVoyageHighlights();
        var largePanelsOpen = GameController.IngameState.IngameUi.FullscreenPanels.Any(x => x.IsVisible) ||
                          GameController.IngameState.IngameUi.LargePanels.Any(x => x.IsVisible);

        if (Handler == null)
        {
            return;
        }

        if (!largePanelsOpen && (Settings.BubbleSettings.ShowBubblesOnMap ||
                                 Settings.BubbleSettings.ShowBubblesInWorld))
        {
            if (Bubbles is { Count: > 0 } bubbles)
            {
                _placedBubblePolygon = _shapeCache.GetOrAdd(bubbles.ToHashSet(), a => a.Select(x => GetCirclePolygon(x.Item1, x.Item2)).Aggregate(PolygonClipper.Union));
                if (Settings.BubbleSettings.ShowBubblesOnMap)
                {
                    foreach (var cont in _placedBubblePolygon)
                    {
                        var a = cont.Select(v => Graphics.GridToMap(new Vector2((float)v.X, (float)v.Y), _playerGridPos)).ToList();
                        Graphics.DrawPolyLine(a.ToArray(), Settings.BubbleSettings.BubbleColor.Value, 2);
                    }
                }

                if (Settings.BubbleSettings.ShowBubblesInWorld)
                {
                    foreach (var cont in _placedBubblePolygon)
                    {
                        
                        var a = cont.Select(v => Camera.WorldToScreen(GameController.IngameState.Data.ToWorldWithTerrainHeight(new Vector2((float)v.X, (float)v.Y)))).ToList();
                        Graphics.DrawPolyLine(a.ToArray(), Settings.BubbleSettings.BubbleColor.Value, 2);
                    }
                }
            }
        }

        if (!largePanelsOpen && Settings.BubbleSettings.MarkStartingBubble)
        {
            foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.IngameIcon]
                         .Where(x => x.Path == "Metadata/Terrain/Leagues/Deepwater/Objects/ExtractionObject"))
            {
                var pos = Graphics.GridToMap(entity.PosNum);
                Graphics.DrawTextWithBackground("Start", pos, Color.Black);
            }
        }
        
        if (Settings.PlannerSettings.ClearSearchHotkey.PressedOnce())
        {
            ClearSearch();
        }

        if (Settings.PlannerSettings.StopSearchHotkey.PressedOnce())
        {
            StopSearch();
        }

        if (_zoneCleared)
        {
            return;
        }

        if (Settings.PlannerSettings.StartSearchHotkey.PressedOnce())
        {
            StartSearch();
        }

        if (!largePanelsOpen)
        {
            foreach (var e in _cachedEntities.Values)
            {
                if (e.IsOpened)
                    continue;

                switch (GetEntityType(e.Path))
                {
                    case ExpeditionEntityType.Marker:
                    {
                        var chestType = GetChestType(e.Path);
                        var mapSettings = Settings.IconMapping.GetValueOrDefault(chestType, new IconDisplaySettings());
                        var icon = mapSettings.Icon ?? DeepwaterEngagementSuiteSettings.GetDefaultIcon(chestType);
                        var tint = mapSettings.Tint ?? DeepwaterEngagementSuiteSettings.GetDefaultTint(chestType);
                        if (mapSettings.ShowOnMap)
                        {
                            DrawIconOnMap(e, icon, tint, Vector2.Zero);
                        }

                        if (mapSettings.ShowInWorld)
                        {
                            DrawIconInWorld(e, icon, tint, Vector2.Zero);
                        }

                        continue;
                    }
                }
            }
        }

        if (EditedOrNativeScore is { PerPointScore.Count: > 0 } score)
        {
            var path = score.PerPointScore;
            var placedBubblePositions = Bubbles.Select(x=>x.Position).ToHashSet();
            var usedPath = (Settings.PlannerSettings.RemoveGraphicsForPlacedBubbles
                ? path
                : path.Where(x => !placedBubblePositions.Contains(x.Point))).DistinctBy(x => x.Point).ToDictionary(x => x.Point);
            var usedPathLines = usedPath.OrderBy(x => x.Key.DistanceSqr(_playerGridPos.TruncateToVector2I())).Take(Settings.PlannerSettings.ClosestNLanterns)
                .Select(x => x.Key).ToHashSet();
            for (var i = 0; i < path.Count; i++)
            {
                var point = path[i].Point;
                if (!usedPath.ContainsKey(point))
                {
                    continue;
                }

                var worldPos = GetWorldScreenPosition(point);
                if (Settings.PlannerSettings.DrawLinesToLanternsInWorld && usedPathLines.Contains(point))
                {
                    Graphics.DrawLine(GetWorldScreenPosition(_playerGridPos), worldPos, 1, Settings.PlannerSettings.WorldLineColor);
                }

                var text = $"#{i}";
                using (Graphics.SetTextScale(Settings.PlannerSettings.TextMarkerScale))
                {
                    Graphics.DrawBox(worldPos, worldPos + Graphics.MeasureText(text), Color.Black);
                    Graphics.DrawText(text, worldPos, Settings.PlannerSettings.BubbleColor.Value);
                }
            }

            if (Settings.PlannerSettings.IsSearchRunning)
            {
                _scoreHistory.Add((float)score.TotalScore);
            }

            ShowSearchWindow(score);

            Polygon plannedPoly = null;
            if (Settings.PlannerSettings.MergePlannedBubbles)
            {
                plannedPoly = _shapeCache.GetOrAdd(usedPath.Keys.Select(x => (x, _bubbleRadius)).ToHashSet(),
                    a => a.Select(x => GetCirclePolygon(x.Item1, x.Item2)).Aggregate(PolygonClipper.Union));
                if (_placedBubblePolygon != null)
                    plannedPoly = PolygonClipper.Difference(plannedPoly, _placedBubblePolygon);
              
            }

            if (Settings.PlannerSettings.DrawPlannedBubblesOnMap)
            {
                if (plannedPoly != null)
                {
                    var excludedVertices = (_placedBubblePolygon?.SelectMany(p => p) ?? []).ToHashSet();

                    IEnumerable<List<Vertex>> Segment(Contour cont)
                    {
                        var current = new List<Vertex>();
                        foreach (var v in cont)
                        {
                            if (excludedVertices.Contains(v))
                            {
                                if (current.Any())
                                {
                                    yield return current;
                                    current = [];
                                }
                            }
                            else
                            {
                                current.Add(v);
                            }
                        }

                        if (current.Any())
                        {
                            yield return current;
                        }
                    }

                    foreach (var cont in plannedPoly.SelectMany(Segment))
                    {
                        var a = cont.Select(v => Graphics.GridToMap(new Vector2((float)v.X, (float)v.Y), _playerGridPos)).ToArray();
                        Graphics.DrawPolyLine(a, Settings.PlannerSettings.BubbleColor.Value, 2);
                    }
                }
                else foreach (var point in usedPath)
                {
                    Graphics.DrawCircleOnMap(point.Key, false, _bubbleRadius, Settings.PlannerSettings.BubbleColor.Value, 2, 100);
                }
            }

            if (PlacementIndicatorPos is { } markerPos)
            {
                if (path.Any(x => x.Point.DistanceLessThanOrEqual(markerPos, 0.01f)))
                {
                    var isDuplicate = placedBubblePositions.Any(x => x.DistanceLessThanOrEqual(markerPos, 0.01f));
                    var screenPos = GetWorldScreenPosition(markerPos);
                    var iconSize = 60;
                    var iconCenter = screenPos + new Vector2(0, -iconSize / 2);
                    Graphics.DrawBox(iconCenter - Vector2.One * iconSize / 2, iconCenter + Vector2.One * iconSize / 2, Color.Black);
                    DrawIcon(isDuplicate ? MapIconsIndex.RedFlag : MapIconsIndex.BlueFlag,
                        null, iconCenter, Vector2.Zero, false,
                        Color.Transparent, 0, iconSize);
                }
            }
        }
    }

    public static Polygon GetCirclePolygon(Vector2 center, float radius)
    {
        var vertices = Enumerable.Range(0, 100).Select(v => center + Vector2.UnitX.Rotate(v * 360 / 100.0f) * radius).ToList();
        var p = new Polygon()
        {
            new Contour()
        };
        foreach (var vertex in vertices)
        {
            p.GetLastContour().Add(new Vertex(vertex.X, vertex.Y));
        }

        return p;
    }

    private void ShowSearchWindow(PathPlanner.DetailedLootScore score)
    {
        if (Settings.PlannerSettings.ShowScoreHistory &&
            (Settings.PlannerSettings.IsSearchRunning || Settings.PlannerSettings.ShowScoreHistoryAfterSearchEnds) &&
            ImGui.Begin("Expedition planning result"))
        {
            if (ImGui.TreeNode("Detailed view"))
            {
                PathPlanner.DetailedLootScore scoreDiff = null;
                if (_editedPath != null && _editedIndex is { } editedIndex)
                {
                    var pos = GameController.IngameState.ServerData.WorldMousePositionNum.WorldToGrid().TruncateToVector2I();
                    var pp = new PathPlanner(Settings.PlannerSettings);
                    pp.Init(score.Environment);
                    var path = _editedPath.ToList();
                    path[editedIndex] = pos;
                    scoreDiff = pp.GetDetailedScore(path, score.Environment);
                    DrawCirclesInWorld([ExpandWithTerrainHeight(pos)], _bubbleRadius * GridToWorldMultiplier, Color.LightBlue);
                    Graphics.DrawLine(GetWorldScreenPosition(_editedPath[editedIndex]), GetWorldScreenPosition(pos), 1, Settings.PlannerSettings.WorldLineColor);

                    if (Settings.PlannerSettings.ConfirmEditorPlacementHotkey.UnpressedOnce())
                    {
                        _editedPath[editedIndex] = pos;
                        _editedPathEval = pp.GetDetailedScore(_editedPath, score.Environment);
                        _editedIndex = null;
                    }

                    if (Input.IsKeyDown(Keys.Escape))
                    {
                        _editedIndex = null;
                    }
                }

                if (ImGui.BeginTable("Change per lantern", 7, ImGuiTableFlags.Hideable | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
                {
                    ImGui.TableSetupColumn("Id");
                    ImGui.TableSetupColumn("Pos");
                    ImGui.TableSetupColumn("Running score");
                    ImGui.TableSetupColumn("Score diff");
                    ImGui.TableSetupColumn("New relic mods");
                    ImGui.TableSetupColumn("New loot");
                    ImGui.TableSetupColumn("Edit");
                    ImGui.TableHeadersRow();

                    var runningScore = 0.0;
                    var runningScoreAfterDiff = 0.0;
                    for (var i = 0; i < score.PerPointScore.Count; i++)
                    {
                        var perPointLootScore = score.PerPointScore[i];
                        var diffOrOld = scoreDiff?.PerPointScore[i] ?? perPointLootScore;
                        ImGui.TableNextRow();
                        ImGui.PushID(i);
                        ImGui.TableNextColumn();
                        ImGui.Text($"{i,2}");
                        ImGui.TableNextColumn();
                        ImGui.Text($"{perPointLootScore.Point}");
                        ImGui.TableNextColumn();
                        runningScore += perPointLootScore.ScoreDiff;
                        if (scoreDiff != null)
                        {
                            runningScoreAfterDiff += scoreDiff.PerPointScore[i].ScoreDiff;
                            ImGui.Text($"{runningScoreAfterDiff,7:F2}");
                            var valueDiff = runningScoreAfterDiff - runningScore;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(GetCompareColor(runningScoreAfterDiff, runningScore), $"{valueDiff:(+0.00);(-0.00);''}");
                            }
                        }
                        else
                        {
                            ImGui.Text($"{runningScore,7:F2}");
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.ScoreDiff,7:F2}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].ScoreDiff - perPointLootScore.ScoreDiff;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].ScoreDiff, perPointLootScore.ScoreDiff),
                                    $"{valueDiff:(+0.00);(-0.00);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.NewRelics}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].NewRelics - perPointLootScore.NewRelics;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].NewRelics, perPointLootScore.NewRelics),
                                    $"{valueDiff:(+0);(-0);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        ImGui.Text($"{diffOrOld.Loot}");
                        if (scoreDiff != null)
                        {
                            var valueDiff = scoreDiff.PerPointScore[i].Loot - perPointLootScore.Loot;
                            if (valueDiff != 0)
                            {
                                ImGui.SameLine();
                                ImGui.TextColored(
                                    GetCompareColor(scoreDiff.PerPointScore[i].Loot, perPointLootScore.Loot),
                                    $"{valueDiff:(+0);(-0);''}");
                            }
                        }

                        ImGui.TableNextColumn();
                        if (i == _editedIndex)
                        {
                            ImGui.PushStyleColor(ImGuiCol.Button, Color.Green.ToImguiVec4());
                            if (ImGui.Button("Cancel"))
                            {
                                _editedIndex = null;
                            }

                            ImGui.PopStyleColor();
                        }
                        else if (ImGui.Button(" Edit "))
                        {
                            _editedPath ??= score.PerPointScore.Select(x => x.Point).ToList();
                            var pp = new PathPlanner(Settings.PlannerSettings);
                            pp.Init(score.Environment);
                            _editedPathEval = pp.GetDetailedScore(_editedPath, score.Environment);
                            _editedIndex = i;
                        }

                        ImGui.PopID();
                    }

                    ImGui.EndTable();
                }

                if (_editedPath != null && ImGui.Button("Reset edited path"))
                {
                    _editedIndex = null;
                    _editedPath = null;
                    _editedPathEval = null;
                }
            }

            ImGui.PlotLines("Score over time", ref CollectionsMarshal.AsSpan(_scoreHistory)[0],
                _scoreHistory.Count, 0, "", 0, _scoreHistory.Max(),
                new Vector2(0, ImGui.GetContentRegionAvail().Y));
            ImGui.End();
        }
    }

    private static Vector4 GetCompareColor(double @new, double old)
    {
        return @new.CompareTo(old) switch
        {
            > 0 => Color.Green.ToImguiVec4(), 0 => Color.White.ToImguiVec4(), < 0 => Color.Red.ToImguiVec4()
        };
    }

    private void DrawIconOnMap(EntityCacheItem entity, MapIconsIndex icon, Color? color, Vector2 offset)
    {
        if (_largeMapOpen)
        {
            var halfsize = Settings.MapIconSize / 2.0f;
            var point = GetEntityPosOnMapScreen(entity) + offset * halfsize * 2;
            var entityPos = entity.Pos;
            var entityPos2 = new Vector2(entityPos.X, entityPos.Y);

            DrawIcon(icon, color, point, entityPos2,
                Settings.BubbleSettings.HideCapturedEntitiesOnMap,
                Settings.PlannerSettings.CapturedEntityMapFrameColor,
                Settings.BubbleSettings.CapturedEntityMapFrameThickness,
                Settings.MapIconSize);
        }
    }

    private void DrawIconInWorld(EntityCacheItem entity, MapIconsIndex icon, Color? color, Vector2 offset)
    {
        var halfsize = Settings.WorldIconSize / 2.0f;
        var entityPos = entity.Pos;
        var entityPos2 = new Vector2(entityPos.X, entityPos.Y);
        var point = Camera.WorldToScreen(entityPos) + offset * halfsize * 2;
        DrawIcon(icon, color, point, entityPos2,
            Settings.BubbleSettings.HideCapturedEntitiesInWorld,
            Settings.PlannerSettings.CapturedEntityWorldFrameColor,
            Settings.BubbleSettings.CapturedEntityWorldFrameThickness,
            Settings.WorldIconSize);
    }

    private void DrawIcon(
        MapIconsIndex icon,
        Color? color,
        Vector2 displayPosition,
        Vector2 worldPosition,
        bool hideCaptured,
        Color plannerCapturedFrameColor,
        int frameThickness,
        float iconSize)
    {
        var halfsize = iconSize / 2.0f;
        var rect = new RectangleF(displayPosition.X, displayPosition.Y, 0, 0);
        rect.Inflate(halfsize, halfsize);
        var isInBubbleRadius = Bubbles.Any(x => Vector2.Distance(x.Position, worldPosition) < x.Radius);
        var gridPosition = worldPosition.WorldToGrid();
        var isInPlannedBubbleRadius = EditedOrNativeScore is { PerPointScore.Count: > 0 } path &&
                                         path.PerPointScore.Any(x => Vector2.Distance(x.Point, gridPosition) < _bubbleRadius);

        if (isInPlannedBubbleRadius)
        {
            var plannedRect = rect;
            Graphics.DrawFrame(plannedRect, plannerCapturedFrameColor, frameThickness);
        }

        if (!isInBubbleRadius || !hideCaptured)
        {
            Graphics.DrawImage(TextureName, rect, SpriteHelper.GetUV(icon), color ?? Color.White);
        }
    }

    private Vector2 GetWorldScreenPosition(Vector2 gridPos)
    {
        return Camera.WorldToScreen(ExpandWithTerrainHeight(gridPos));
    }

    private Vector2 GetEntityPosOnMapScreen(EntityCacheItem entity)
    {
        return Graphics.GridToMap(entity.GridPos, entity.GridPos);
    }

    private enum ExpeditionEntityType
    {
        None,
        Marker,
    }

    private record EntityCacheItem(
        string Path,
        Lazy<string> BaseAnimatedEntityMetadataCache,
        List<string> Mods,
        Vector3 Pos,
        Vector2 GridPos,
        float? RenderZ,
        float? RenderSize,
        bool? MinimapIconHide,
        bool IsOpened)
    {
        public string BaseAnimatedEntityMetadata => BaseAnimatedEntityMetadataCache.Value;

        public EntityCacheItem Merge(EntityCacheItem other)
        {
            return new EntityCacheItem(
                Path ?? other.Path,
                BaseAnimatedEntityMetadata == null ? other.BaseAnimatedEntityMetadataCache : BaseAnimatedEntityMetadataCache,
                Mods ?? other.Mods,
                Pos,
                GridPos,
                RenderZ ?? other.RenderZ,
                RenderSize ?? other.RenderSize,
                MinimapIconHide ?? other.MinimapIconHide,
                IsOpened || other.IsOpened);
        }
    }

    public override void EntityAdded(Entity entity)
    {
        if ((entity.Type is EntityType.Chest or EntityType.Terrain or EntityType.IngameIcon)
            && GetEntityType(entity.Path) != ExpeditionEntityType.None
            && !entity.IsOpened)
        {
            _cachedEntities[entity.Id] = BuildCacheItem(entity);
        }
    }

    public override void EntityRemoved(Entity entity)
    {
        _cachedEntities.Remove(entity.Id);
    }

    private static EntityCacheItem BuildCacheItem(Entity entity)
    {
        return new EntityCacheItem(
            entity.Path,
            new Lazy<string>(() => entity.GetComponent<Animated>()?.BaseAnimatedObjectEntity?.Metadata, LazyThreadSafetyMode.None),
            entity.GetComponent<ObjectMagicProperties>()?.Mods,
            entity.PosNum,
            entity.PosNum.WorldToGrid(),
            entity.GetComponent<Render>()?.Z,
            entity.GetComponent<Render>()?.BoundsNum is { } b ? Math.Min(b.X, b.Y) : null,
            entity.GetComponent<MinimapIcon>()?.IsHide,
            entity.IsOpened);
    }
}
