using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeepwaterEngagementSuite.VoyagePlannerData;
using ExileCore;
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
using SharpDX;
using Direction = DeepwaterEngagementSuite.VoyagePlannerData.Direction;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    private VoyageSolutionResult _result;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _unknownBiomes = new();
    private int _rerollCount;
    private string _lastBorderKey;
    private double? _baselineScore;
    private string _baselineKey;
    private Task _run;
    private SyncTask<bool> _voyagePlaceTask;
    private VoyagePlanner _voyagePlanner;
    private int _selectedSolutionIndex = 0;
    private bool _voyageSolving;
    private bool _voyageTimedOut;
    private long _voyageNodesExplored;
    private long _voyageNodesPruned;
    private double _voyageElapsed;
    private System.Diagnostics.Stopwatch _voyageStopwatch;

    public List<NormalInventoryItem> GetAvailableCharts()
    {
        if (GameController.IngameState.IngameUi.VoyageWindow is { IsValid: true, IsVisible: true } voyageWindow)
        {

            var charts = voyageWindow.AvailableCharts;
            if (!charts.Any())
            {
                return [];
            }
            var filters = Settings.VoyageSettings.IgnoredCharts.Content.Where(x => x.Enabled).Select(x => x.Query).ToList();
            if (!filters.Any())
            {
                return charts;
            }

            var chartSize = charts[0].GetClientRectCache.Size;
            var containerRect = voyageWindow.ChartContainer.GetClientRectCache;
            var containerSize = containerRect.Size;
            var inventorySize = new Vector2i(
                (int)Math.Round(containerSize.Width/chartSize.Width),
                (int)Math.Round(containerSize.Height / chartSize.Height)); //TODO: is this gettable somewhere?
            var filtered = charts.Select(x =>
                {
                    var coord = ((x.GetClientRectCache.TopLeft - containerRect.TopLeft).ToVector2Num()
                                 / new Vector2(containerSize.Width, containerSize.Height)
                                 * inventorySize)
                        .RoundToVector2I();
                    return (x, new ChartData(x.Item, GameController, coord));
                })
                .Where(x => !filters.Any(f => f.Matches(x.Item2)))
                .Select(x => x.x)
                .ToList();
            return filtered;
        }

        return [];
    }

    private async SyncTask<bool> PlacePieces(VoyageSolution solution)
    {
        var tree = GameController.IngameState.IngameUi.VoyageWindow;
        var clearPos = tree.ClearButton.GetClientRectCache.Center.ToVector2Num();
        Input.SetCursorPos(GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num() + clearPos);
        await TaskUtils.CheckEveryFrameWithThrow(() => tree.ClearButton.HasShinyHighlight, TimeSpan.FromSeconds(1));
        Input.LeftDown();
        await TaskUtils.NextFrame();
        Input.LeftUp();
        await TaskUtils.CheckEveryFrameWithThrow(() => tree.Tiles.All(x => x.ItemContainer == null), TimeSpan.FromSeconds(1));
        var availableCharts = GetAvailableCharts();
        for (int i = 0; i < 9; i++)
        {
            var tile = tree.Tiles[i];
            var p = solution.Grid[i / 3, i % 3];
            var pieceElem = availableCharts[p.Piece.Id];
            var click1Pos = pieceElem.GetClientRectCache.Center.ToVector2Num();
            var click2Pos = tile.GetClientRectCache.Center.ToVector2Num();
            Input.SetCursorPos(GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num() + click1Pos);
            await TaskUtils.CheckEveryFrameWithThrow(() => GameController.IngameState.UIHover?.Address.Equals(pieceElem.Address) ?? false,
                () => $"Hover address was {GameController.IngameState.UIHover?.Address:X} not {pieceElem.Address:X}",
                TimeSpan.FromSeconds(1));
            Input.LeftDown();
            await TaskUtils.NextFrame();
            Input.LeftUp();
            await TaskUtils.CheckEveryFrameWithThrow(() => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.HoldItemForSell, TimeSpan.FromSeconds(1));
            Input.SetCursorPos(GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num() + click2Pos);
            await TaskUtils.CheckEveryFrameWithThrow(() => GameController.IngameState.UIHoverElement?.Address.Equals(tile.Address) ?? false,
                () => $"Hover address was {GameController.IngameState.UIHoverElement?.Address:X} not {tile.Address:X}",
                TimeSpan.FromSeconds(1));
            Input.LeftDown();
            await TaskUtils.NextFrame();
            Input.LeftUp();
            await TaskUtils.CheckEveryFrameWithThrow(() => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.Free, TimeSpan.FromSeconds(1));

            while (tile.ItemContainer?.Entity.GetComponent<DeepwaterChart>().Rotation is {} rot && rot != p.Rotation)
            {
                DebugWindow.LogMsg($"{rot}, {p.Rotation}");
                var click3Pos = tile.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num() + click3Pos);
                await TaskUtils.CheckEveryFrameWithThrow(() => GameController.IngameState.UIHover?.Address.Equals(tile.ItemContainer.Address) ?? false, TimeSpan.FromSeconds(1));
                Input.RightDown();
                await TaskUtils.NextFrame();
                Input.RightUp();
                await TaskUtils.CheckEveryFrameWithThrow(() => tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>()?.Rotation is {} rot2 && rot2 != rot, TimeSpan.FromSeconds(1));
            }
        }

        return true;
    }

    private void DrawVoyageHighlights()
    {
        var settings = Settings.VoyageSettings;
        if (!settings.EnableVoyageHandling)
            return;

        if (Input.IsKeyDown(Keys.Escape) && _voyagePlaceTask != null)
        {
            _voyagePlaceTask = null;
        }

        VoyageWindow tree;
        try
        {
            tree = GameController?.IngameState?.IngameUi?.VoyageWindow;
        }
        catch (Exception ex)
        {
            _voyagePlaceTask = null;
            DebugWindow.LogError(ex.ToString());
            return;
        }

        if (tree is not { IsValid: true, IsVisible: true })
        {
            _voyagePlaceTask = null;
            return;
        }

        TaskUtils.RunOrRestart(ref _voyagePlaceTask, () => null);

        while (_unknownBiomes.TryDequeue(out var newBiomeId))
        {
            if (!Settings.VoyageSettings.BiomeWeights.Content
                    .Any(b => b.Id.Value.Equals(newBiomeId, StringComparison.OrdinalIgnoreCase)))
            {
                Settings.VoyageSettings.BiomeWeights.Content.Add(new BiomeWeightSetting
                {
                    Id = new TextNode(newBiomeId),
                });
            }
        }

        var modsPerTileIndex = GetTileMods(tree);

        var borderKey = string.Join("|", tree.Data.BorderMods.Select(m => m.RawName));
        if (!string.IsNullOrEmpty(borderKey) && _lastBorderKey != null && borderKey != _lastBorderKey)
        {
            _rerollCount++;
        }

        if (!string.IsNullOrEmpty(borderKey))
        {
            _lastBorderKey = borderKey;
        }

        var tiles = tree.Tiles;
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var mods = modsPerTileIndex.GetValueOrDefault(index) ?? [];
            var tileTopLeft = tile.GetClientRectCache.TopLeft.ToVector2Num();
            Graphics.DrawTextWithBackground($"({index / 3}, {index % 3})", tileTopLeft, Color.Black);
            var tileCenter = tile.GetClientRectCache.Center.ToVector2Num();
            // Chart name above center
            var chart = tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>();
            if (chart != null)
            {
                var chartMods = tile.ItemContainer.Entity.GetComponent<Mods>()?.ImplicitMods ?? [];
                var chartModOffset = -10f;
                foreach (var im in chartMods)
                {
                    var chartMod = Settings.VoyageSettings.ChartModifiers.Content
                        .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
                    var displayName = TrimChartPrefix(im.RawName);
                    var prefix = chartMod?.EffectiveScope switch
                    {
                        ModScope.Voyage => "[G] ",
                        ModScope.Self => "[S] ",
                        _ => "",
                    };
                    var weight = chartMod?.Weight.Value ?? 0;
                    var chartName = $"{prefix}{displayName}\n({weight:F1})";
                    var textSize = Graphics.MeasureText(chartName);
                    if (!string.IsNullOrEmpty(chartName))
                    {
                        chartModOffset -= textSize.Y;
                        Graphics.DrawTextWithBackground(chartName, tileCenter + new Vector2(0, chartModOffset),
                            chartMod != null && chartMod.Weight.Value > Settings.VoyageSettings.ChartHighlightThreshold.Value
                                ? chartMod.HighlightColor
                                : Color.White, FontAlign.Center, Color.Black);
                    }
                }
            }
            // Border mods below center
            tileCenter = tileCenter + new Vector2(0, 10);
            foreach (var itemMod in mods)
            {
                var matchingSetting = Settings.VoyageSettings.BorderModifiers.Content.FirstOrDefault(c => c.Id.Value.Equals(itemMod.RawName, StringComparison.OrdinalIgnoreCase));
                var text = matchingSetting?.Abbreviation.Value is { Length: > 0 } abbv
                    ? abbv
                    : itemMod.RawName switch
                    {
                        var r when r.StartsWith("DeepwaterBorder", StringComparison.Ordinal) => r["DeepwaterBorder".Length..],
                        var r => r
                    };
                var size = Graphics.DrawTextWithBackground(text, tileCenter,
                    matchingSetting != null && matchingSetting.ValueMultiplier > Settings.VoyageSettings.BorderHighlightThreshold
                        ? matchingSetting.HighlightColor
                        : Color.Orange, FontAlign.Center, Color.Black);
                tileCenter.Y += size.Y;
            }
        }

        var charts = GetAvailableCharts();
        for (int i = 0; i < charts.Count; i++)
        {
            Graphics.DrawTextWithBackground($"#{i}", charts[i].GetClientRectCache.TopLeft.ToVector2Num(), Color.Black);
        }

        if (settings.ShowOptimizerWindow.Value)
        {
            ShowVoyageOptimizerWindow(tree,tiles);
        }
    }

    private static Dictionary<int, List<ItemMod>> GetTileMods(VoyageWindow tree)
    {
        var borderMods = tree.Data.BorderMods;
        Dictionary<int, List<ItemMod>> modsPerTileIndex = [];
        if (borderMods.Count >= 12)
        {
            modsPerTileIndex = new Dictionary<int, List<int>>
            {
                [0] = [0, 11],
                [1] = [1],
                [2] = [2, 3],
                [3] = [10],
                [4] = [],
                [5] = [4],
                [6] = [8, 9],
                [7] = [7],
                [8] = [5, 6],
            }.ToDictionary(
                x => x.Key,
                x => x.Value.Select(v => borderMods[v])
                    .ToList());
        }

        return modsPerTileIndex;
    }

    private void ShowVoyageOptimizerWindow(VoyageWindow tree, List<VoyageTileElement> tiles)
    {
        if (!ImGui.Begin("Voyage Optimizer"))
        {
            ImGui.End();
            return;
        }

        _voyageSolving = _run is { IsCompleted: false };
        
        if (ImGui.Button("Solve"))
        {
            _voyagePlanner?.Cancel();
            _result = null;
            _selectedSolutionIndex = 0;
            _voyageNodesExplored = 0;
            _voyageNodesPruned = 0;
            _voyageElapsed = 0;
            _voyageTimedOut = false;
            _voyageStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _run = Task.Run(() =>
            {
                var i = 0;
                var pieces = new List<MapPiece>();
                foreach (var chart in GetAvailableCharts())
                {
                    if (chart.Item.TryGetComponent(out DeepwaterChart c))
                    {
                        var rotation = ((Direction)c.Room.Path);
                        var itemMods = chart.Item.GetComponent<Mods>();
                        var modifiers = new List<Modifier> { new("Default", 1) };

                        void AddItemMods(IEnumerable<ItemMod> source, ModScope defaultScope)
                        {
                            foreach (var im in source ?? [])
                            {
                                var chartMod = Settings.VoyageSettings.ChartModifiers.Content
                                    .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
                                modifiers.Add(new Modifier(im.RawName, chartMod?.Weight.Value ?? 0,
                                    chartMod?.EffectiveScope ?? defaultScope));
                            }
                        }

                        AddItemMods(itemMods?.ImplicitMods, ModScope.Adjacent);
                        AddItemMods(itemMods?.ExplicitMods, ModScope.Self);

                        var biomeId = c.Room?.Biome?.Id;
                        if (!string.IsNullOrEmpty(biomeId))
                        {
                            var biome = Settings.VoyageSettings.BiomeWeights.Content
                                .FirstOrDefault(b => b.Id.Value.Equals(biomeId, StringComparison.OrdinalIgnoreCase));
                            if (biome == null)
                            {
                                _unknownBiomes.Enqueue(biomeId);
                            }

                            modifiers.Add(new Modifier($"Biome:{biomeId}", biome?.Weight.Value ?? 0, ModScope.Self));
                        }

                        var mp = new MapPiece(i,
                            int.PopCount((int)rotation) switch
                            {
                                4 => PieceType.Cross,
                                3 => PieceType.Tee,
                                1 => PieceType.Single,
                                2 => rotation.HasFlag(Direction.Left) == rotation.HasFlag(Direction.Right)
                                    ? PieceType.Straight
                                    : PieceType.Corner
                            }, rotation, modifiers);
                        pieces.Add(mp);
                    }

                    i++;
                }

                var modsPerTileIndex = GetTileMods(tree);
                var boardMultipliers = modsPerTileIndex.Select(x => (x.Key,
                    x.Value.Select(m => Settings.VoyageSettings.BorderModifiers.Content.FirstOrDefault(c => c.Id.Value == m.RawName)?.ValueMultiplier.Value ?? 1)
                        .Aggregate(1f, (a, b) => a * b))).ToList();
                var tileMultiplierArray = PositionWeightMap.ScreenToGrid(Settings.VoyageSettings.PositionWeights);
                foreach (var boardMultiplier in boardMultipliers)
                {
                    tileMultiplierArray[boardMultiplier.Key / 3, boardMultiplier.Key % 3] *= boardMultiplier.Item2;
                }

                _voyagePlanner = new VoyagePlanner();
                var timeLimitSetting = Settings.VoyageSettings.SolverTimeLimitSeconds.Value;
                foreach (var r in _voyagePlanner.Solve(new VoyagePuzzle(pieces, tileMultiplierArray, []),
                    new VoyagePlannerSettings(TimeLimitSeconds: timeLimitSetting)))
                {
                    _result = r;
                    _voyageNodesExplored = r.NodesExplored;
                    _voyageNodesPruned = r.NodesPruned;
                }

                // Baseline p/ reroll advisor: mesmo pool de charts, borders "médios".
                var piecesKey = string.Join("|", pieces.Select(p =>
                    $"{p.Id}:{p.Type}:{(int)p.BaseConnections}:{p.OwnModifier:F2}:{p.LocalModifier:F2}:{p.GlobalModifier:F2}"));
                var positionWeights = PositionWeightMap.ScreenToGrid(Settings.VoyageSettings.PositionWeights);
                if (piecesKey != _baselineKey)
                {
                    var avg = Settings.VoyageSettings.BorderModifiers.Content
                        .Select(b => (double)b.ValueMultiplier.Value)
                        .DefaultIfEmpty(1)
                        .Average();
                    var baselineMults = RerollAdvisor.BuildBaselineMultipliers(avg, positionWeights, RerollAdvisor.BorderModCountPerTile);
                    var baselinePlanner = new VoyagePlanner();
                    double baselineScore = 0;
                    foreach (var br in baselinePlanner.Solve(new VoyagePuzzle(pieces, baselineMults, []),
                                 new VoyagePlannerSettings(TopN: 1, TimeLimitSeconds: timeLimitSetting)))
                    {
                        baselineScore = br.Solutions.FirstOrDefault()?.TotalScore ?? baselineScore;
                    }

                    _baselineScore = baselineScore;
                    _baselineKey = piecesKey;
                }

                if (_voyageStopwatch.Elapsed.TotalSeconds >= timeLimitSetting)
                    _voyageTimedOut = true;

                _voyageSolving = false;
            });
        }

        if (_voyagePlanner != null && _voyageSolving)
        {
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                _voyagePlanner?.Cancel();
            }
        }

        if (_voyageSolving)
        {
            if (_voyageStopwatch != null)
                _voyageElapsed = _voyageStopwatch.Elapsed.TotalSeconds;
            ImGui.SameLine();
            var timeLimitSetting = Settings.VoyageSettings.SolverTimeLimitSeconds.Value;
            var progress = timeLimitSetting > 0 ? Math.Min(1f, (float)(_voyageElapsed / timeLimitSetting)) : 0.5f;
            ImGui.ProgressBar(progress, default, $"{_voyageElapsed:F1}s");
        }

        if (_result != null && _result.Solutions.Count > 0)
        {
            ImGui.SameLine();
            if (ImGui.Button("Place"))
            {
                if (_selectedSolutionIndex >= _result.Solutions.Count)
                    _selectedSolutionIndex = 0;
                var sol = _result.Solutions[_selectedSolutionIndex];
                _voyagePlaceTask = PlacePieces(sol);
            }
        }

        ImGui.Spacing();

        if (_voyageSolving || _result != null)
        {
            ImGui.Text($"Nodes: {_voyageNodesExplored:N0} explored, {_voyageNodesPruned:N0} pruned");
        }

        if (Settings.VoyageSettings.ShowRerollAdvisor.Value &&
            _result is { Solutions.Count: > 0 } &&
            _baselineScore is > 0)
        {
            var ratio = _result.Solutions[0].TotalScore / _baselineScore.Value;
            var keep = RerollAdvisor.ShouldKeep(ratio, Settings.VoyageSettings.RerollKeepThreshold.Value);

            int? sulphur = null;
            try
            {
                sulphur = GameController.IngameState.ServerData.DeepwaterHandler?.Sulphur;
            }
            catch
            {
                // fora de contexto deepwater o handler pode não estar legível
            }

            var nextCost = RerollAdvisor.NextCost(_rerollCount);
            if (keep)
            {
                ImGui.TextColored(Color.LightGreen.ToImguiVec4(), $"Borders: R={ratio:F2} — KEEP");
            }
            else if (sulphur is { } s && s < nextCost)
            {
                ImGui.TextColored(Color.Yellow.ToImguiVec4(),
                    $"Borders: R={ratio:F2} — REROLL quando puder (sulphur: {s:N0}/{nextCost:N0})");
            }
            else
            {
                ImGui.TextColored(Color.OrangeRed.ToImguiVec4(),
                    $"Borders: R={ratio:F2} — REROLL (próximo: {nextCost:N0} sulphur)");
            }

            if (sulphur != null && (keep || sulphur >= nextCost))
            {
                ImGui.SameLine();
                ImGui.Text($"(sulphur: {sulphur:N0})");
            }

            ImGui.Text($"Rerolls nesta board: {_rerollCount}");
            ImGui.SameLine();
            if (ImGui.SmallButton("+")) _rerollCount++;
            ImGui.SameLine();
            if (ImGui.SmallButton("-")) _rerollCount = Math.Max(0, _rerollCount - 1);
            ImGui.SameLine();
            if (ImGui.SmallButton("reset")) _rerollCount = 0;
        }

        if (_result == null || _result.Solutions.Count == 0)
        {
            if (_voyageSolving)
            {
                ImGui.TextColored(Color.Yellow.ToImguiVec4(), "Searching...");
            }
            else if (_voyageTimedOut)
            {
                ImGui.TextColored(Color.Orange.ToImguiVec4(), "Time limit reached — no valid solution found.");
            }
            else
            {
                ImGui.TextColored(Color.Gray.ToImguiVec4(), "No solutions yet. Press Solve.");
            }

            ImGui.End();
            return;
        }

        if (_voyageTimedOut)
        {
            ImGui.TextColored(Color.Orange.ToImguiVec4(), $"Time limit reached — showing best solutions found so far (may not be optimal).");
        }

        _selectedSolutionIndex = Math.Clamp(_selectedSolutionIndex, 0, _result.Solutions.Count - 1);
        var currentSolution = _result.Solutions[_selectedSolutionIndex];

        var asciiArt = BuildAsciiGrid(currentSolution.Grid, tiles);

        using (ImGuiHelpers.UseStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0, 0)))
            foreach (var line in asciiArt)
            {
                ImGui.TextUnformatted(line);
            }

        ImGui.Spacing();

        ImGui.Text($"Score: {currentSolution.TotalScore:F2}");
        ImGui.Text($"Valid: {(currentSolution.IsValid ? "Yes" : "No")}");

        if (_result.Solutions.Count > 0)
        {
            ImGui.Spacing();
            if (ImGui.BeginTable("SolutionsList", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("#");
                ImGui.TableSetupColumn("Score");
                ImGui.TableSetupColumn("Valid");
                ImGui.TableSetupColumn("Select");
                ImGui.TableHeadersRow();

                for (int i = 0; i < _result.Solutions.Count; i++)
                {
                    var sol = _result.Solutions[i];
                    ImGui.TableNextRow();
                    ImGui.PushID(i);
                    ImGui.TableNextColumn();
                    ImGui.Text($"{i + 1}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{sol.TotalScore:F2}");
                    ImGui.TableNextColumn();
                    ImGui.Text($"{sol.IsValid}");
                    ImGui.TableNextColumn();
                    var isSelected = i == _selectedSolutionIndex;
                    if (isSelected)
                        ImGui.PushStyleColor(ImGuiCol.Button, Color.Green.ToImguiVec4());
                    if (ImGui.Button(isSelected ? "Selected" : "Select"))
                    {
                        _selectedSolutionIndex = i;
                    }

                    if (isSelected)
                        ImGui.PopStyleColor();
                    ImGui.PopID();
                }

                ImGui.EndTable();
            }
        }

        if (ImGui.BeginTable("ScoreBreakdown", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Tile", ImGuiTableColumnFlags.WidthFixed, 25);
            ImGui.TableSetupColumn("Piece", ImGuiTableColumnFlags.WidthFixed, 20);
            ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn("Mods");
            ImGui.TableHeadersRow();

            for (int i = 0; i < 9; i++)
            {
                var r = i / 3;
                var c = i % 3;
                var placement = currentSolution.Grid[r, c];

                ImGui.TableNextRow();
                ImGui.PushID($"tile{i}");
                ImGui.TableNextColumn();
                ImGui.Text($"{r},{c}");
                ImGui.TableNextColumn();
                ImGui.Text($"#{placement.Piece.Id}");
                ImGui.TableNextColumn();
                ImGui.Text($"{placement.Piece.Type}");
                ImGui.TableNextColumn();
                var modText = string.Join(", ", placement.Piece.Modifiers.Where(m => m.Name != "Default").Select(m =>
                {
                    var displayName = TrimChartPrefix(m.Name);
                    var prefix = m.Scope switch
                    {
                        ModScope.Voyage => "[Global] ",
                        ModScope.Self => "[Self] ",
                        _ => "",
                    };
                    return $"{prefix}{displayName}({m.Weight:F1})";
                }));
                ImGui.Text(string.IsNullOrEmpty(modText) ? "-" : modText);
                ImGui.PopID();
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }

    private static string[] BuildAsciiGrid(MapPiecePlacement[,] grid, List<VoyageTileElement> tiles)
    {
        const int H = 5;
        const int W = 7;
        const int GH = H * 3 + 2;
        const int GW = W * 3 + 2;

        var buf = new char[GH, GW];
        for (int y = 0; y < GH; y++)
        for (int x = 0; x < GW; x++)
            buf[y, x] = ' ';

        FillBox(buf, '+', '+', '+', '+', '-', '|', 0, 0, GH - 1, GW - 1);

        for (int r = 0; r < 3; r++)
        {
            for (int c = 0; c < 3; c++)
            {
                var left = c * W + 1;
                var right = left + W - 1;
                var top = r * H + 1;
                var bot = top + H - 1;
                var cx = left + W / 2;
                var cy = top + H / 2;

                var p = grid[2 - r, c];
                var conn = p.Connections;

                for (int y = top; y <= bot; y++)
                for (int x = left; x <= right; x++)
                    buf[y, x] = ' ';

                if (conn.HasFlag(Direction.Up))
                    for (int y = top; y < cy; y++)
                        buf[y, cx] = '|';
                if (conn.HasFlag(Direction.Down))
                    for (int y = cy + 1; y <= bot; y++)
                        buf[y, cx] = '|';
                if (conn.HasFlag(Direction.Left))
                    for (int x = left; x < cx; x++)
                        buf[cy, x] = '-';
                if (conn.HasFlag(Direction.Right))
                    for (int x = cx + 1; x <= right; x++)
                        buf[cy, x] = '-';

                buf[cy, cx] = conn switch
                {
                    Direction.Up | Direction.Down => '|',
                    Direction.Left | Direction.Right => '-',
                    Direction.All => '+',
                    _ => '.',
                };

                // Match indicator
                var tileIdx = (2 - r) * 3 + c;
                bool matches = false;
                if (tileIdx < tiles.Count)
                {
                    var t = tiles[tileIdx];
                    if (t.ItemContainer?.Address != null)
                    {
                        var placed = t.ItemContainer.Entity.GetComponent<DeepwaterChart>();
                        if (placed != null)
                        {
                            var actualRot = ((Direction)placed.Room.Path).RotateCcw(placed.Rotation);
                            var expectedRot = p.Connections;
                            matches = actualRot == expectedRot;
                        }
                    }
                }

                buf[cy + 1, cx + 2] = matches ? 'O' : 'X';
            }
        }

        var lines = new string[GH];
        for (int y = 0; y < GH; y++)
        {
            var row = new char[GW];
            for (int x = 0; x < GW; x++)
                row[x] = buf[y, x];
            lines[y] = new string(row);
        }

        return lines;
    }

    private static void FillBox(char[,] buf, char tl, char tr, char bl, char br, char h, char v, int y1, int x1, int y2, int x2)
    {
        buf[y1, x1] = tl;
        buf[y1, x2] = tr;
        buf[y2, x1] = bl;
        buf[y2, x2] = br;
        for (int x = x1 + 1; x < x2; x++)
        {
            buf[y1, x] = h;
            buf[y2, x] = h;
        }

        for (int y = y1 + 1; y < y2; y++)
        {
            buf[y, x1] = v;
            buf[y, x2] = v;
        }
    }

    private static string TrimChartPrefix(string name)
    {
        if (name.StartsWith("MapDeepwaterChartVoyage", StringComparison.Ordinal))
            return name["MapDeepwaterChartVoyage".Length..];
        if (name.StartsWith("MapDeepwaterChartAdjacent", StringComparison.Ordinal))
            return name["MapDeepwaterChartAdjacent".Length..];
        return name;
    }
}