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

    private int _lockerSulphur;
    private DateTime _lockerSulphurRead = DateTime.MinValue;

    /// <summary>
    /// Dead Man's Sulphur no locker do barco (ServerData.PlayerInventories, slot
    /// DeepwaterLockerInventory1), como stacks de CurrencyDeepwater (50k/stack).
    /// É a MOEDA DO PLANNER (rerolls etc., debitada direto do locker) — não
    /// confundir com Handler.Sulphur, que é o contador de coleta do mergulho e
    /// congela no valor da última run. Cache de 2s.
    /// </summary>
    private int LockerSulphur()
    {
        if ((DateTime.UtcNow - _lockerSulphurRead).TotalSeconds < 2)
        {
            return _lockerSulphur;
        }

        _lockerSulphurRead = DateTime.UtcNow;
        var total = 0;
        try
        {
            foreach (var holder in GameController.IngameState.ServerData.PlayerInventories)
            {
                if (holder?.Inventory is not { } inv ||
                    !inv.InventSlot.ToString().Contains("DeepwaterLocker", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var item in inv.Items ?? [])
                {
                    if (item?.Path?.Contains("CurrencyDeepwater", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        total += item.GetComponent<Stack>()?.Size ?? 0;
                    }
                }
            }
        }
        catch
        {
            // inventários ilegíveis em transição
        }

        _lockerSulphur = total;
        return total;
    }
    private readonly Dictionary<long, (MapPiece Piece, int ExplicitCount)> _chartValueCache = new();
    private int _rerollCount;
    private string _lastBorderKey;
    private (string Name, double Score, bool ReqMet)[] _strategyScores = [];
    private volatile int _solveReservedCount;
    private volatile string _solveReserveShortfall;
    private volatile string _solveCentrePin;
    private readonly Dictionary<string, List<string>> _strategyMissing = new();
    private DateTime _lastStrategyEval = DateTime.MinValue;
    private VoyageStrategy _activeStrategy;
    private Task _run;

    private static readonly string[] StrategyChoices =
        ["Auto", .. VoyageStrategy.DocStrategies.Select(s => s.Name), "Base"];
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

    private static bool TileHasChart(VoyageTileElement tile) =>
        tile?.ItemContainer?.Entity?.GetComponent<DeepwaterChart>() != null;

    private static bool BoardIsClear(VoyageWindow tree) =>
        tree.Tiles.All(t => !TileHasChart(t));

    // Upstream PR#9: o jogo às vezes não registra hover no primeiro SetCursorPos
    // depois de interagir com ImGui — um wiggle devolve o foco de hover ao jogo.
    private static async SyncTask<bool> WiggleCursorToFocus(Vector2 screenPos)
    {
        const float delta = 4f;
        Input.SetCursorPos(screenPos + new Vector2(delta, 0));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos + new Vector2(-delta, 0));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos + new Vector2(0, delta));
        await TaskUtils.NextFrame();
        Input.SetCursorPos(screenPos);
        await TaskUtils.NextFrame();
        return true;
    }

    private async SyncTask<bool> PlacePieces(VoyageSolution solution)
    {
        try
        {
            var tree = GameController.IngameState.IngameUi.VoyageWindow;
            var winOrigin = GameController.Window.GetWindowRectangleTimeCache.TopLeft.ToVector2Num();
            var needsFocusWiggle = true;

            if (!BoardIsClear(tree))
            {
                var clearPos = winOrigin + tree.ClearButton.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(clearPos);
                if (needsFocusWiggle)
                {
                    await WiggleCursorToFocus(clearPos);
                    needsFocusWiggle = false;
                }

                await TaskUtils.CheckEveryFrameWithThrow(
                    () => tree.ClearButton.HasShinyHighlight,
                    () => "Clear button never highlighted (board may already be empty?)",
                    TimeSpan.FromSeconds(2));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => BoardIsClear(tree),
                    () => "Board still has charts after Clear",
                    TimeSpan.FromSeconds(3));
            }

            var availableCharts = GetAvailableCharts();
            for (var i = 0; i < 9; i++)
            {
                var tile = tree.Tiles[i];
                var p = solution.Grid[i / 3, i % 3];
                if (p?.Piece == null)
                    continue;
                if (p.Piece.Id < 0 || p.Piece.Id >= availableCharts.Count)
                {
                    DebugWindow.LogError($"Voyage Place: piece id {p.Piece.Id} out of range ({availableCharts.Count} charts)");
                    continue;
                }

                var pieceElem = availableCharts[p.Piece.Id];
                var click1Pos = winOrigin + pieceElem.GetClientRectCache.Center.ToVector2Num();
                var click2Pos = winOrigin + tile.GetClientRectCache.Center.ToVector2Num();
                Input.SetCursorPos(click1Pos);
                if (needsFocusWiggle)
                {
                    await WiggleCursorToFocus(click1Pos);
                    needsFocusWiggle = false;
                }

                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.UIHover?.Address.Equals(pieceElem.Address) ?? false,
                    () => $"Hover address was {GameController.IngameState.UIHover?.Address:X} not {pieceElem.Address:X}",
                    TimeSpan.FromSeconds(1));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.HoldItemForSell,
                    TimeSpan.FromSeconds(1));
                Input.SetCursorPos(click2Pos);
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.UIHoverElement?.Address.Equals(tile.Address) ?? false,
                    () => $"Hover address was {GameController.IngameState.UIHoverElement?.Address:X} not {tile.Address:X}",
                    TimeSpan.FromSeconds(1));
                Input.LeftDown();
                await TaskUtils.NextFrame();
                Input.LeftUp();
                await TaskUtils.CheckEveryFrameWithThrow(
                    () => GameController.IngameState.IngameUi.Cursor.Action == MouseActionType.Free &&
                          TileHasChart(tile),
                    TimeSpan.FromSeconds(1));

                while (tile.ItemContainer?.Entity.GetComponent<DeepwaterChart>()?.Rotation is { } rot &&
                       rot != p.Rotation)
                {
                    DebugWindow.LogMsg($"{rot}, {p.Rotation}");
                    var click3Pos = winOrigin + tile.GetClientRectCache.Center.ToVector2Num();
                    Input.SetCursorPos(click3Pos);
                    await TaskUtils.CheckEveryFrameWithThrow(
                        () => GameController.IngameState.UIHover?.Address.Equals(tile.ItemContainer.Address) ?? false,
                        TimeSpan.FromSeconds(1));
                    Input.RightDown();
                    await TaskUtils.NextFrame();
                    Input.RightUp();
                    await TaskUtils.CheckEveryFrameWithThrow(
                        () => tile.ItemContainer?.Entity?.GetComponent<DeepwaterChart>()?.Rotation is { } rot2 &&
                              rot2 != rot,
                        TimeSpan.FromSeconds(1));
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"Voyage Place failed: {ex.Message}");
            return false;
        }
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
            if (_chartValueCache.Count > 0)
            {
                _chartValueCache.Clear();
            }

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

        double[,] effectiveMults = null;
        var tileRanks = new int[9];
        if (Settings.VoyageSettings.ShowTilePriority.Value && tree.Data.BorderMods.Count >= 12)
        {
            effectiveMults = ComputeEffectiveMultipliers(tree);
            var order = Enumerable.Range(0, 9).OrderByDescending(t => effectiveMults[t / 3, t % 3]).ToList();
            for (var rank = 0; rank < order.Count; rank++)
            {
                tileRanks[order[rank]] = rank + 1;
            }
        }

        var tiles = tree.Tiles;
        for (var index = 0; index < tiles.Count; index++)
        {
            var tile = tiles[index];
            var mods = modsPerTileIndex.GetValueOrDefault(index) ?? [];
            var tileTopLeft = tile.GetClientRectCache.TopLeft.ToVector2Num();
            Graphics.DrawTextWithBackground($"({index / 3}, {index % 3})", tileTopLeft, Color.Black);
            if (effectiveMults != null && index < 9)
            {
                var rank = tileRanks[index];
                var rankColor = rank <= 3 ? Color.LightGreen : rank >= 8 ? Color.OrangeRed : Color.White;
                Graphics.DrawTextWithBackground($"prio {rank} (x{effectiveMults[index / 3, index % 3]:F2})",
                    tileTopLeft + new Vector2(0, 16), rankColor, Color.Black);
            }
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
        var chartEntries = new (MapPiece Piece, int ExplicitCount)[charts.Count];
        if (Settings.VoyageSettings.ShowChartValues.Value)
        {
            for (int i = 0; i < charts.Count; i++)
            {
                var addr = charts[i].Address;
                if (!_chartValueCache.TryGetValue(addr, out var entry))
                {
                    entry = (BuildMapPiece(charts[i], i),
                        charts[i].Item.GetComponent<Mods>()?.ExplicitMods?.Count ?? 0);
                    _chartValueCache[addr] = entry;
                }

                chartEntries[i] = entry;
            }
        }

        var maxSolveCharts = Settings.VoyageSettings.SolverMaxCharts.Value;
        var topChartSet = Settings.VoyageSettings.ShowChartValues.Value
            ? Enumerable.Range(0, charts.Count)
                .Where(i => chartEntries[i].Piece != null)
                .OrderByDescending(i => chartEntries[i].Piece.OwnModifier + chartEntries[i].Piece.LocalModifier + chartEntries[i].Piece.GlobalModifier)
                .Take(maxSolveCharts > 0 ? maxSolveCharts : charts.Count)
                .ToHashSet()
            : [];

        var hoverTooltipRect = GetHoverTooltipRect();
        for (int i = 0; i < charts.Count; i++)
        {
            if (OverlapsTooltip(charts[i].GetClientRectCache, hoverTooltipRect))
            {
                continue; // não desenhar por cima do tooltip do chart em hover
            }

            var chartTopLeft = charts[i].GetClientRectCache.TopLeft.ToVector2Num();
            Graphics.DrawTextWithBackground($"#{i}", chartTopLeft, Color.Black);
            if (!Settings.VoyageSettings.ShowChartValues.Value || chartEntries[i].Piece is not { } piece)
            {
                continue;
            }

            var value = piece.OwnModifier + piece.LocalModifier + piece.GlobalModifier;
            var inTop = topChartSet.Contains(i);
            Graphics.DrawTextWithBackground($"{value:F0}", chartTopLeft + new Vector2(0, 12),
                inTop ? Color.LightGreen : Color.Gray, Color.Black);
            // (Sem tag "craft" aqui: chart no pool já rodou solo e NÃO rola mais —
            // mecânica da liga. A janela de craft é o inventário, pré-run.)
            // Peça exclusiva/âncora de estratégia: NÃO queimar em Speedrun/AlcGo
            // (a reserva dura do Solve respeita; o overlay te mostra o porquê).
            if (PieceOwnerTag(piece) is { } ownerTag)
            {
                Graphics.DrawTextWithBackground(ownerTag,
                    chartTopLeft + new Vector2(0, 24), Color.Cyan, Color.Black);
            }
        }

        if (settings.ShowOptimizerWindow.Value)
        {
            ShowVoyageOptimizerWindow(tree,tiles);
        }
    }

    // Peças exclusivas/âncora de estratégias, p/ o overlay do pool (ordem =
    // especificidade; primeiro match vence). Espelha ReserveKeys + PieceRequirements.
    private static readonly (string Key, string Tag)[] PieceOwnerTags =
    [
        ("Room:Pelagic Abyss", "DIVBOX"),
        ("Room:Sea Pillars", "MEAT/DIV"),
        ("AdjacentWisps2", "MEAT/ETH"),
        ("AdjacentWisps", "ETH"),
        ("AdjacentStarfish", "MEAT"),
        ("AdjacentPantheon", "MEAT"),
        ("AdjacentGoldenLanterns", "MEAT/ETH"),
        ("VoyageMonstersPossessed", "MEAT"),
        ("VoyageNoEquipmentDrops", "MEAT"),
        ("VoyageRareFracture", "MEAT"),
        ("IncreasedRareMonsters", "DIV"),
        ("AdjacentOperativeBox", "SPEED"),
        ("AdjacentDivinerBox", "SPEED"),
        ("AdjacentLostMessage", "SPEED"),
        ("AdjacentArcanistBox", "DIVBOX"),
        ("AdjacentStrongboxes", "DIV"),
    ];

    private static string PieceOwnerTag(MapPiece piece)
    {
        foreach (var (key, tag) in PieceOwnerTags)
        {
            if (piece.Modifiers.Any(m => m.Name.Contains(key, StringComparison.OrdinalIgnoreCase)))
            {
                return tag;
            }
        }

        return null;
    }

    // Texto de UI seguro: TextUnformatted (o '%' de "110%+" vira format string no
    // ImGui.Text e corrompe a saída) + wrap fixo — a janela é auto-resize, então
    // wrap na borda (0) deixaria ela crescer sem limite.
    private static void WrappedText(Color color, string text, float wrapWidth = 620f)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color.ToImguiVec4());
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + wrapWidth);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    /// <summary>Constrói a peça do solver a partir de um chart do estoque (mods + bioma). Null se inválido.</summary>
    private MapPiece BuildMapPiece(NormalInventoryItem chart, int index)
    {
        if (!chart.Item.TryGetComponent(out DeepwaterChart c) || c.Room == null)
        {
            return null;
        }

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

        // Stats rolados (quant/sulphur/pack) como pseudo-mods Self — explicits fora da
        // config entram com peso 0, então o craft não contava no valor próprio. Escala
        // per-N do solver one-more-map (quantity/6, sulphur/8, packsize/8); alvo dos
        // boosts self:* das estratégias. PASSADA ÚNICA: cada leitura de StatNames/Key
        // é memória do jogo — três SumStatValues por chart custavam segundos no pool.
        var quantStat = 0;
        var sulphurStat = 0;
        var packStat = 0;
        foreach (var mod in itemMods?.ItemMods ?? [])
        {
            var statNames = mod.ModRecord?.StatNames;
            if (statNames == null)
            {
                continue;
            }

            for (var si = 0; si < statNames.Length && si < mod.Values.Count; si++)
            {
                var key = statNames[si]?.Key;
                if (string.IsNullOrEmpty(key))
                {
                    continue;
                }

                if (key.Contains("quantity", StringComparison.OrdinalIgnoreCase))
                {
                    quantStat += mod.Values[si];
                }
                else if (key.Contains("resource", StringComparison.OrdinalIgnoreCase))
                {
                    sulphurStat += mod.Values[si];
                }
                else if (key.Contains("pack", StringComparison.OrdinalIgnoreCase))
                {
                    packStat += mod.Values[si];
                }
            }
        }

        if (quantStat != 0)
        {
            modifiers.Add(new Modifier("Stat:Quantity", quantStat / 6.0, ModScope.Self));
        }

        if (sulphurStat != 0)
        {
            modifiers.Add(new Modifier("Stat:Resource", sulphurStat / 8.0, ModScope.Self));
        }

        if (packStat != 0)
        {
            modifiers.Add(new Modifier("Stat:PackSize", packStat / 8.0, ModScope.Self));
        }

        // Nome da sala como pseudo-mod (peso 0): identifica charts especiais no pool
        // (Sea Pillars, Kishara's Rest...) para requisitos de estratégia e pesos futuros.
        var roomName = c.Room?.Name;
        if (!string.IsNullOrEmpty(roomName))
        {
            modifiers.Add(new Modifier($"Room:{roomName}", 0, ModScope.Self));
        }

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

        return new MapPiece(index,
            int.PopCount((int)rotation) switch
            {
                4 => PieceType.Cross,
                3 => PieceType.Tee,
                1 => PieceType.Single,
                2 => rotation.HasFlag(Direction.Left) == rotation.HasFlag(Direction.Right)
                    ? PieceType.Straight
                    : PieceType.Corner
            }, rotation, modifiers);
    }

    /// <summary>Multiplicadores efetivos por célula: P[r,c] × produto dos border mods do tile (boostados pela estratégia, se houver).</summary>
    private double[,] ComputeEffectiveMultipliers(VoyageWindow tree, VoyageStrategy strategy = null)
    {
        var result = PositionWeightMap.ScreenToGrid(Settings.VoyageSettings.PositionWeights);
        foreach (var (tileIndex, mods) in GetTileMods(tree))
        {
            var mult = mods
                .Select(m =>
                {
                    var baseMult = (double)(Settings.VoyageSettings.BorderModifiers.Content
                        .FirstOrDefault(c => c.Id.Value == m.RawName)?.ValueMultiplier.Value ?? 1);
                    return strategy?.BoostBorderMultiplier(m.RawName, baseMult) ?? baseMult;
                })
                .Aggregate(1.0, (a, b) => a * b);
            result[tileIndex / 3, tileIndex % 3] *= mult;
        }

        return result;
    }

    /// <summary>Peças do pool completo (cache por address; Id pode divergir do índice atual — não usar para cliques).</summary>
    private List<MapPiece> GetChartPieces(List<NormalInventoryItem> charts)
    {
        var list = new List<MapPiece>();
        for (var i = 0; i < charts.Count; i++)
        {
            var addr = charts[i].Address;
            if (!_chartValueCache.TryGetValue(addr, out var entry))
            {
                entry = (BuildMapPiece(charts[i], i),
                    charts[i].Item.GetComponent<Mods>()?.ExplicitMods?.Count ?? 0);
                _chartValueCache[addr] = entry;
            }

            if (entry.Piece != null)
            {
                list.Add(entry.Piece);
            }
        }

        return list;
    }

    private VoyageStrategy ResolveStrategy(string choice)
    {
        return choice switch
        {
            "Base" => null,
            "Auto" => _strategyScores.Length > 0
                ? VoyageStrategy.DocStrategies.FirstOrDefault(s => s.Name == _strategyScores.MaxBy(x => x.Score).Name)
                : null,
            var name => VoyageStrategy.DocStrategies.FirstOrDefault(s => s.Name == name),
        };
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
            var solveStrategy = _activeStrategy;
            // Peças construídas AQUI (thread de render, via cache): reconstruir da
            // memória do jogo dentro da task relia ModRecord/StatNames de todos os
            // mods (~5s p/ 60 charts). O Id precisa refletir o índice ATUAL do pool
            // (PlacePieces clica availableCharts[Id]) — cache pode ter Id antigo.
            var basePieces = new List<MapPiece>();
            {
                var i = 0;
                foreach (var chart in GetAvailableCharts())
                {
                    var addr = chart.Address;
                    if (!_chartValueCache.TryGetValue(addr, out var entry))
                    {
                        entry = (BuildMapPiece(chart, i),
                            chart.Item.GetComponent<Mods>()?.ExplicitMods?.Count ?? 0);
                        _chartValueCache[addr] = entry;
                    }

                    if (entry.Piece is { } cached)
                    {
                        basePieces.Add(cached.Id == i ? cached : cached with { Id = i });
                    }

                    i++;
                }
            }

            _run = Task.Run(() =>
            {
                var pieces = basePieces.Select(p => solveStrategy?.BoostPiece(p) ?? p).ToList();

                // Reserva (site one-more-map): estratégias de queima não escalam
                // peças das outras. Reserva DURA por padrão: sem 9 peças livres,
                // NÃO há solução válida (não queima a peça do Meatfish só porque o
                // AlcGo pontua mais). O toggle "Allow burning reserves" devolve o
                // backfill antigo (readmite as reservadas de menor valor até 12).
                _solveReservedCount = 0;
                _solveReserveShortfall = null;
                if (solveStrategy?.ReserveKeys is { Length: > 0 })
                {
                    var usable = pieces.Where(p => !solveStrategy.IsReserved(p)).ToList();
                    var reservedCount = pieces.Count - usable.Count;
                    if (Settings.VoyageSettings.AllowBurningReserves.Value)
                    {
                        const int minPool = 12;
                        if (usable.Count < minPool)
                        {
                            usable.AddRange(pieces
                                .Where(solveStrategy.IsReserved)
                                .OrderBy(p => p.OwnModifier + p.LocalModifier + p.GlobalModifier)
                                .Take(minPool - usable.Count));
                        }
                    }
                    else if (usable.Count < 9 && reservedCount > 0)
                    {
                        _solveReserveShortfall =
                            $"No solution WITHOUT burning reserves: {usable.Count} free charts + {reservedCount} reserved " +
                            $"for other strategies ({9 - usable.Count} more free needed). Farm/buy charts or enable 'Allow burning reserves'.";
                        _result = new VoyageSolutionResult([], 0, 0);
                        _voyageSolving = false;
                        return;
                    }

                    _solveReservedCount = pieces.Count - usable.Count;
                    pieces = usable;
                }

                // Enforcement de layout do site/Milky (report de dev, 31/07): UMA
                // peça de box — Operative > Diviner > Bottle — TRAVADA no centro.
                // Adjacent-scope alcança 4 vizinhos só lá, e a 2ª peça de box é
                // substituta (rouba slot de chart de quant). O maximizador puro
                // punha o Operative na borda juiceada e PackSize no centro.
                MapPiece centrePiece = null;
                _solveCentrePin = null;
                if (solveStrategy?.CentrePieceKeys is { Length: > 0 } centreTiers)
                {
                    bool MatchesTier(MapPiece p, string[] tier) =>
                        tier.Any(k => p.Modifiers.Any(md => md.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));
                    bool IsBoxPiece(MapPiece p) => centreTiers.Any(t => MatchesTier(p, t));

                    string pinnedKey = null;
                    foreach (var tier in centreTiers)
                    {
                        centrePiece = pieces
                            .Where(p => MatchesTier(p, tier))
                            .OrderByDescending(p => p.OwnModifier + p.LocalModifier + p.GlobalModifier)
                            .FirstOrDefault();
                        if (centrePiece != null)
                        {
                            pinnedKey = tier[0];
                            break;
                        }
                    }

                    if (centrePiece != null)
                    {
                        var kept = pieces.Where(p => p.Id == centrePiece.Id || !IsBoxPiece(p)).ToList();
                        if (kept.Count < 9)
                        {
                            // Pool faminto: sem 9 peças o board não fecha — readmite
                            // as boxes excedentes por valor (a doutrina cede à física).
                            kept.AddRange(pieces
                                .Where(p => p.Id != centrePiece.Id && IsBoxPiece(p))
                                .OrderByDescending(p => p.OwnModifier + p.LocalModifier + p.GlobalModifier)
                                .Take(9 - kept.Count));
                        }

                        pieces = kept;
                        _solveCentrePin = $"(layout: {pinnedKey} pinned to centre)";
                    }
                }

                var maxCharts = Settings.VoyageSettings.SolverMaxCharts.Value;
                if (maxCharts > 0 && pieces.Count > maxCharts)
                {
                    pieces = pieces
                        .OrderByDescending(p => p.OwnModifier + p.LocalModifier + p.GlobalModifier)
                        .ThenByDescending(p => p.BaseConnections switch
                        {
                            Direction.All => 4,
                            var d when int.PopCount((int)d) == 3 => 3,
                            var d when int.PopCount((int)d) == 2 => 2,
                            _ => 1,
                        })
                        .Take(maxCharts)
                        .ToList();
                    if (centrePiece != null && pieces.All(p => p.Id != centrePiece.Id))
                    {
                        pieces.Add(centrePiece); // a peça travada nunca sai no cap
                    }
                }

                var tileMultiplierArray = ComputeEffectiveMultipliers(tree, solveStrategy);

                // Trava de layout: centro do grid = (1,1) em coords de solver.
                // Rotation 0 é nominal — o Fast escolhe a rotação por topologia.
                List<LockedPlacement> locks = centrePiece != null
                    ? [new LockedPlacement(1, 1, centrePiece.Id, 0)]
                    : [];

                var timeLimitSetting = Settings.VoyageSettings.SolverTimeLimitSeconds.Value;
                IEnumerable<VoyageSolutionResult> results;
                if (Settings.VoyageSettings.UseFastSolver.Value)
                {
                    // Exato (topologias + DP), sem timeout. Respeita o pool já
                    // filtrado (reserva) e o cap — otimalidade certificada dentro
                    // do pool; com max charts = 0 cobre o estoque inteiro.
                    _voyagePlanner = null;
                    results = new VoyagePlannerFast().Solve(new VoyagePuzzle(pieces, tileMultiplierArray, locks),
                        new VoyagePlannerSettings(TimeLimitSeconds: timeLimitSetting));
                }
                else
                {
                    _voyagePlanner = new VoyagePlanner();
                    results = _voyagePlanner.Solve(new VoyagePuzzle(pieces, tileMultiplierArray, locks),
                        new VoyagePlannerSettings(TimeLimitSeconds: timeLimitSetting));
                }

                foreach (var r in results)
                {
                    _result = r;
                    _voyageNodesExplored = r.NodesExplored;
                    _voyageNodesPruned = r.NodesPruned;
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
                // Contexto do plano para o registro da voyage (previsto vs realizado).
                _plannedStrategy = _activeStrategy?.Name ?? "Base";
                _plannedScore = sol.TotalScore;
                _plannedMults = ComputeEffectiveMultipliers(tree, _activeStrategy);
                _plannedPieceMods = new List<string>[3, 3];
                for (var pr = 0; pr < 3; pr++)
                {
                    for (var pc = 0; pc < 3; pc++)
                    {
                        _plannedPieceMods[pr, pc] =
                            sol.Grid[pr, pc]?.Piece?.Modifiers.Select(m => m.Name).ToList() ?? [];
                    }
                }

                _plannedDivineCell = -1;
                foreach (var (tileIndex, tileMods) in GetTileMods(tree))
                {
                    if (tileMods.Any(m => m.RawName.Contains("RareMonsterDivine", StringComparison.OrdinalIgnoreCase)))
                    {
                        _plannedDivineCell = tileIndex;
                        break;
                    }
                }
            }
        }

        ImGui.Spacing();

        // Avaliação das estratégias do doc sobre o POOL COMPLETO de charts + borders atuais.
        if (tree.Data.BorderMods.Count >= 12)
        {
            if ((DateTime.UtcNow - _lastStrategyEval).TotalSeconds >= 1)
            {
                _lastStrategyEval = DateTime.UtcNow;
                var allPieces = GetChartPieces(GetAvailableCharts());
                var borderIds = tree.Data.BorderMods.Select(m => m.RawName).ToList();
                _strategyScores = VoyageStrategy.DocStrategies.Select(s =>
                {
                    var eff = ComputeEffectiveMultipliers(tree, s);
                    var sum = 0.0;
                    for (var r = 0; r < 3; r++)
                        for (var c = 0; c < 3; c++)
                            sum += eff[r, c];
                    _strategyMissing[s.Name] = s.MissingPieces(allPieces);
                    return (s.Name, Score: s.ScoreBoard(allPieces, sum, borderIds), ReqMet: s.RequirementsMet(borderIds));
                }).ToArray();
            }

            if (_strategyScores.Length > 0)
            {
                var bestName = _strategyScores.MaxBy(x => x.Score).Name;
                ImGui.Text("Strategies:");
                for (var i = 0; i < _strategyScores.Length; i++)
                {
                    var (name, score, reqMet) = _strategyScores[i];
                    if (i == 0 || i % 3 != 0)
                    {
                        ImGui.SameLine();
                    }

                    var color = name == bestName ? Color.LightGreen : Color.Gray;
                    ImGui.TextColored(color.ToImguiVec4(), $"{name}: {score:F0}{(reqMet ? "" : " (no border)")}");
                }

                var current = Settings.VoyageSettings.SelectedStrategy.Value;
                if (!StrategyChoices.Contains(current))
                {
                    current = "Auto";
                }

                ImGui.SetNextItemWidth(140);
                if (ImGui.BeginCombo("Strategy", current))
                {
                    foreach (var choice in StrategyChoices)
                    {
                        if (ImGui.Selectable(choice, choice == current))
                        {
                            Settings.VoyageSettings.SelectedStrategy.Value = choice;
                        }
                    }

                    ImGui.EndCombo();
                }

                _activeStrategy = ResolveStrategy(Settings.VoyageSettings.SelectedStrategy.Value is { Length: > 0 } v ? v : "Auto");
                ImGui.SameLine();
                ImGui.Text($"using: {_activeStrategy?.Name ?? "Base"}");
                if (_solveReservedCount > 0)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Color.Gray.ToImguiVec4(),
                        $"(reserve: {_solveReservedCount} charts protected for other strategies)");
                }

                if (_solveCentrePin != null)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Color.Gray.ToImguiVec4(), _solveCentrePin);
                }

                if (_activeStrategy != null)
                {
                    if (_strategyMissing.GetValueOrDefault(_activeStrategy.Name) is { Count: > 0 } missing)
                    {
                        WrappedText(Color.Yellow,
                            $"MISSING pieces ({_activeStrategy.Name}): {string.Join(", ", missing)} - speedrun boxes meanwhile");
                    }

                    if (_activeStrategy.LayoutHint is { Length: > 0 } hint)
                    {
                        WrappedText(Color.Gray, hint);
                    }
                }
            }
        }

        if (Settings.VoyageSettings.ShowRerollAdvisor.Value && tree.Data.BorderMods.Count >= 12)
        {
            var effective = ComputeEffectiveMultipliers(tree, _activeStrategy);
            var avg = Settings.VoyageSettings.BorderModifiers.Content
                .Select(b => _activeStrategy?.BoostBorderMultiplier(b.Id.Value, b.ValueMultiplier.Value)
                             ?? (double)b.ValueMultiplier.Value)
                .DefaultIfEmpty(1)
                .Average();
            var positionWeights = PositionWeightMap.ScreenToGrid(Settings.VoyageSettings.PositionWeights);
            var baseline = RerollAdvisor.BuildBaselineMultipliers(avg, positionWeights, RerollAdvisor.BorderModCountPerTile);
            var ratio = RerollAdvisor.BorderRatio(effective, baseline);
            var keep = RerollAdvisor.ShouldKeep(ratio, Settings.VoyageSettings.RerollKeepThreshold.Value);

            // A moeda do planner é o Dead Man's Sulphur, debitado do LOCKER do
            // barco. NÃO usar Handler.Sulphur aqui: aquilo é o contador de coleta
            // do MERGULHO (recurso das minas; congela no valor da última run) —
            // moeda diferente da do planner (correção do Bruno, 31/07).
            var available = LockerSulphur();

            var nextCost = RerollAdvisor.NextCost(_rerollCount);
            if (keep)
            {
                ImGui.TextColored(Color.LightGreen.ToImguiVec4(), $"Borders: R={ratio:F2} - KEEP");
            }
            else if (available < nextCost)
            {
                ImGui.TextColored(Color.Yellow.ToImguiVec4(),
                    $"Borders: R={ratio:F2} - REROLL when you can (Dead Man's Sulphur: {available:N0}/{nextCost:N0})");
            }
            else
            {
                ImGui.TextColored(Color.OrangeRed.ToImguiVec4(),
                    $"Borders: R={ratio:F2} - REROLL (next: {nextCost:N0} sulphur)");
            }

            if (keep || available >= nextCost)
            {
                ImGui.SameLine();
                ImGui.Text($"(Dead Man's Sulphur: {available:N0})");
            }

            ImGui.Text($"Rerolls this board: {_rerollCount}");
            ImGui.SameLine();
            if (ImGui.SmallButton("+")) _rerollCount++;
            ImGui.SameLine();
            if (ImGui.SmallButton("-")) _rerollCount = Math.Max(0, _rerollCount - 1);
            ImGui.SameLine();
            if (ImGui.SmallButton("reset")) _rerollCount = 0;
        }

        if (_voyageSolving || _result != null)
        {
            ImGui.Text($"Nodes: {_voyageNodesExplored:N0} explored, {_voyageNodesPruned:N0} pruned");
        }

        if (_result == null || _result.Solutions.Count == 0)
        {
            if (_voyageSolving)
            {
                ImGui.TextColored(Color.Yellow.ToImguiVec4(), "Searching...");
            }
            else if (_voyageTimedOut)
            {
                ImGui.TextColored(Color.Orange.ToImguiVec4(), "Time limit reached - no valid solution found.");
            }
            else if (_solveReserveShortfall is { } shortfall)
            {
                WrappedText(Color.Orange, shortfall);
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
            ImGui.TextColored(Color.Orange.ToImguiVec4(), $"Time limit reached - showing best solutions found so far (may not be optimal).");
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