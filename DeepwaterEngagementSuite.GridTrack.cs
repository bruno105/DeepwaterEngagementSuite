using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // Grid tracker 3x3: divide a área em 9 células (o layout da voyage é a costura 3x3
    // dos charts), grava a trilha do personagem e o tempo/ordem de visita por célula.
    // GridDebugMode ativa em qualquer mapa normal — validação sem gastar voyage.
    private readonly List<Vector2> _pathBreadcrumbs = new();
    private DateTime _lastBreadcrumb = DateTime.MinValue;
    private readonly double[] _cellSeconds = new double[9];
    private readonly int[] _cellFirstOrder = new int[9];
    private int _cellOrderCounter;
    private int _lastCellIndex = -1;
    private DateTime _lastCellSample = DateTime.MinValue;

    private void GridTrackTick()
    {
        var settings = Settings.PilotSettings;
        if (!settings.ShowGridTracker || (Handler == null && !settings.GridDebugMode))
        {
            return;
        }

        var playerPos = GameController.Player?.GetComponent<Positioned>()?.WorldPosNum.WorldToGrid();
        if (playerPos == null)
        {
            return;
        }

        // No barco (fora das bolhas) não acumula trilha nem tempo de célula — o tempo
        // de barco poluiria cellSeconds/duração do run (mesma instância, teleporte).
        if (Handler != null && !IsPlayerInField(playerPos.Value))
        {
            _lastCellIndex = -1;
            _lastCellSample = DateTime.MinValue;
            return;
        }

        var now = DateTime.UtcNow;
        if ((now - _lastBreadcrumb).TotalMilliseconds >= 500)
        {
            _lastBreadcrumb = now;
            if (_pathBreadcrumbs.Count == 0 || Vector2.Distance(_pathBreadcrumbs[^1], playerPos.Value) > 10)
            {
                _pathBreadcrumbs.Add(playerPos.Value);
                if (_pathBreadcrumbs.Count > 2000)
                {
                    _pathBreadcrumbs.RemoveAt(0);
                }
            }
        }

        var cell = GridCellIndex(playerPos.Value);
        if (cell >= 0)
        {
            if (_lastCellSample != DateTime.MinValue && _lastCellIndex == cell)
            {
                _cellSeconds[cell] += (now - _lastCellSample).TotalSeconds;
            }

            if (_lastCellIndex != cell && _cellFirstOrder[cell] == 0)
            {
                _cellFirstOrder[cell] = ++_cellOrderCounter;
            }

            _lastCellIndex = cell;
            _lastCellSample = now;
        }
    }

    // Região real do mapa jogável: a área inclui o barco (componente separado de
    // terreno) e padding. Âncora definitiva: MAIOR componente conexo do terreno
    // andável (pathfinding), conhecido inteiro desde o load — os 9 charts costurados
    // na voyage, a sala única no chart solo. Computado uma vez por área, em background.
    private Vector2 _gridOrigin;
    private Vector2 _gridSize;
    private bool _regionComputed;
    private volatile bool _regionComputing;
    // Rótulo de componente conexo por célula do downsample (0 = inandável). O Pilot
    // usa p/ descartar objetivos em bolsões sem caminho ANTES de tentar rota.
    private short[,] _terrainComponents;

    private void UpdateGridBounds()
    {
        if (_regionComputed || _regionComputing)
        {
            return;
        }

        Vector2i dims;
        try
        {
            dims = GameController.IngameState.Data.AreaDimensions;
        }
        catch
        {
            return;
        }

        if (dims.X <= 0 || dims.Y <= 0)
        {
            return;
        }

        // Fallback imediato (área toda) enquanto o cálculo roda.
        if (_gridSize == default)
        {
            _gridOrigin = default;
            _gridSize = new Vector2(dims.X, dims.Y);
        }

        _regionComputing = true;
        Task.Run(ComputeRegionFromTerrain);
    }

    private void ComputeRegionFromTerrain()
    {
        try
        {
            var pf = _pathfindingData ?? GameController.IngameState.Data.RawPathfindingData;
            if (pf is not { Length: > 0 } || pf[0] is not { Length: > 0 })
            {
                return;
            }

            var h = pf.Length;
            var w = pf[0].Length;
            const int step = 8;
            var gh = h / step;
            var gw = w / step;
            if (gh < 3 || gw < 3)
            {
                return;
            }

            var walkable = new bool[gh, gw];
            for (var y = 0; y < gh; y++)
            {
                for (var x = 0; x < gw; x++)
                {
                    walkable[y, x] = pf[y * step][x * step] > 3;
                }
            }

            // Flood fill: maior componente conexo = mapa jogável (barco é componente menor).
            var visited = new bool[gh, gw];
            var compIds = new short[gh, gw];
            short compId = 0;
            var bestCount = 0;
            int bestMinX = 0, bestMinY = 0, bestMaxX = 0, bestMaxY = 0;
            var stack = new Stack<(int Y, int X)>();
            for (var sy = 0; sy < gh; sy++)
            {
                for (var sx = 0; sx < gw; sx++)
                {
                    if (!walkable[sy, sx] || visited[sy, sx])
                    {
                        continue;
                    }

                    compId++;
                    var count = 0;
                    int minX = sx, minY = sy, maxX = sx, maxY = sy;
                    stack.Push((sy, sx));
                    visited[sy, sx] = true;
                    while (stack.TryPop(out var cur))
                    {
                        count++;
                        compIds[cur.Y, cur.X] = compId;
                        minX = Math.Min(minX, cur.X);
                        minY = Math.Min(minY, cur.Y);
                        maxX = Math.Max(maxX, cur.X);
                        maxY = Math.Max(maxY, cur.Y);
                        Span<(int, int)> neighbors = [(cur.Y - 1, cur.X), (cur.Y + 1, cur.X), (cur.Y, cur.X - 1), (cur.Y, cur.X + 1)];
                        foreach (var (ny, nx) in neighbors)
                        {
                            if (ny >= 0 && ny < gh && nx >= 0 && nx < gw && walkable[ny, nx] && !visited[ny, nx])
                            {
                                visited[ny, nx] = true;
                                stack.Push((ny, nx));
                            }
                        }
                    }

                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestMinX = minX;
                        bestMinY = minY;
                        bestMaxX = maxX;
                        bestMaxY = maxY;
                    }
                }
            }

            _terrainComponents = compIds; // atribuição de referência = atômica p/ leitores

            if (bestCount > 0)
            {
                const float pad = 12f;
                var originX = Math.Max(0, bestMinX * step - pad);
                var originY = Math.Max(0, bestMinY * step - pad);
                _gridOrigin = new Vector2(originX, originY);
                _gridSize = new Vector2(
                    Math.Min(w, (bestMaxX + 1) * step + pad) - originX,
                    Math.Min(h, (bestMaxY + 1) * step + pad) - originY);
                _regionComputed = true;
            }
        }
        catch
        {
            // terreno indisponível em transição; tenta de novo no próximo tick
        }
        finally
        {
            _regionComputing = false;
        }
    }

    private int GridCellIndex(Vector2 gridPos)
    {
        UpdateGridBounds();
        if (_gridSize.X <= 0 || _gridSize.Y <= 0)
        {
            return -1;
        }

        var nx = (gridPos.X - _gridOrigin.X) / _gridSize.X;
        var ny = (gridPos.Y - _gridOrigin.Y) / _gridSize.Y;
        if (nx is < -0.02f or > 1.02f || ny is < -0.02f or > 1.02f)
        {
            return -1; // fora da região dos charts (barco/padding)
        }

        // Convenção do board da voyage: linha 0 = embaixo do canvas. Com o Y do mundo
        // crescendo para o norte (flip no canvas), gridY baixo = embaixo = linha 0.
        var c = Math.Clamp((int)(nx * 3), 0, 2);
        var row = Math.Clamp((int)(ny * 3), 0, 2);
        return row * 3 + c;
    }

    /// <summary>
    /// Ponto andável mais próximo de uma coordenada (busca radial no pathfinding).
    /// Centros geométricos de célula podem cair em água/void — o Radar não rota até lá.
    /// </summary>
    private Vector2? SnapToWalkable(Vector2 grid)
    {
        try
        {
            var pf = _pathfindingData ?? GameController.IngameState.Data.RawPathfindingData;
            if (pf is not { Length: > 0 } || pf[0] is not { Length: > 0 })
            {
                return null;
            }

            var h = pf.Length;
            var w = pf[0].Length;
            var cx = (int)grid.X;
            var cy = (int)grid.Y;
            if (cx >= 0 && cx < w && cy >= 0 && cy < h && pf[cy][cx] > 3)
            {
                return grid;
            }

            for (var radius = 8; radius <= 280; radius += 8)
            {
                for (var angle = 0; angle < 360; angle += 20)
                {
                    var (sin, cos) = MathF.SinCos(angle * MathF.PI / 180f);
                    var x = cx + (int)(cos * radius);
                    var y = cy + (int)(sin * radius);
                    if (x >= 0 && x < w && y >= 0 && y < h && pf[y][x] > 3)
                    {
                        return new Vector2(x, y);
                    }
                }
            }
        }
        catch
        {
            // pathfinding indisponível
        }

        return null;
    }

    /// <summary>Componente conexo no ponto (0 = inandável/desconhecido; busca 3x3 no downsample).</summary>
    private short ComponentAt(Vector2 grid)
    {
        var comps = _terrainComponents;
        if (comps == null)
        {
            return 0;
        }

        const int step = 8;
        var y = (int)grid.Y / step;
        var x = (int)grid.X / step;
        for (var dy = -1; dy <= 1; dy++)
        {
            for (var dx = -1; dx <= 1; dx++)
            {
                var ny = y + dy;
                var nx = x + dx;
                if (ny >= 0 && ny < comps.GetLength(0) && nx >= 0 && nx < comps.GetLength(1) &&
                    comps[ny, nx] != 0)
                {
                    return comps[ny, nx];
                }
            }
        }

        return 0;
    }

    private void GridTrackReset()
    {
        _gridOrigin = default;
        _gridSize = default;
        _regionComputed = false;
        _terrainComponents = null;
        _pathBreadcrumbs.Clear();
        Array.Clear(_cellSeconds);
        Array.Clear(_cellFirstOrder);
        _cellOrderCounter = 0;
        _lastCellIndex = -1;
        _lastCellSample = DateTime.MinValue;
        _lastBreadcrumb = DateTime.MinValue;
    }

    private void DrawGridTracker()
    {
        var settings = Settings.PilotSettings;
        if (!settings.ShowGridTracker || (Handler == null && !settings.GridDebugMode))
        {
            return;
        }

        Vector2i dims;
        Vector2 playerPos;
        try
        {
            dims = GameController.IngameState.Data.AreaDimensions;
            playerPos = GameController.Player?.GetComponent<Positioned>()?.WorldPosNum.WorldToGrid() ?? default;
        }
        catch
        {
            return;
        }

        UpdateGridBounds();
        if (dims.X <= 0 || dims.Y <= 0 || playerPos == default || _gridSize.X <= 0 || _gridSize.Y <= 0)
        {
            return;
        }

        if (!ImGui.Begin("Grid Tracker", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var canvasW = (float)settings.GridWindowSize.Value;
        var canvasH = canvasW * _gridSize.Y / _gridSize.X;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Canvas mapeia a REGIÃO do grid (bbox dos monstros = os 9 charts, sem o barco).
        // Y do mundo cresce para o norte: canvas desenha gridY alto no TOPO (flip).
        Vector2 ToCanvas(Vector2 grid) =>
            origin + new Vector2(
                (grid.X - _gridOrigin.X) / _gridSize.X * canvasW,
                (1f - (grid.Y - _gridOrigin.Y) / _gridSize.Y) * canvasH);

        var gridCol = ImGui.ColorConvertFloat4ToU32(settings.GridColor.Value.ToImguiVec4());
        var pathCol = ImGui.ColorConvertFloat4ToU32(settings.PathColor.Value.ToImguiVec4());
        var markerCol = ImGui.ColorConvertFloat4ToU32(Color.Gold.ToImguiVec4());
        var playerCol = ImGui.ColorConvertFloat4ToU32(Color.White.ToImguiVec4());
        var bgCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.45f));

        drawList.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH), bgCol);

        // Terreno gerado pelo Radar: a textura cobre a área INTEIRA; recorta por UV
        // apenas a região do grid. V invertido para acompanhar o flip do canvas.
        try
        {
            if (Graphics.HasImage("radar_minimap"))
            {
                var u0 = _gridOrigin.X / dims.X;
                var u1 = (_gridOrigin.X + _gridSize.X) / dims.X;
                var vTop = (_gridOrigin.Y + _gridSize.Y) / dims.Y;
                var vBottom = _gridOrigin.Y / dims.Y;
                drawList.AddImage(Graphics.GetTextureId("radar_minimap"),
                    origin, origin + new Vector2(canvasW, canvasH),
                    new Vector2(u0, vTop), new Vector2(u1, vBottom));
            }
        }
        catch
        {
            // Radar ausente ou textura ainda não gerada para a área
        }

        // Grade 3x3 só faz sentido em voyage (costura de 9 charts); o modo debug força
        // a grade apenas em mapa NORMAL (sem Handler) — em chart solo deepwater a
        // janela é a visão da sala inteira, sem grade, mesmo com debug ligado.
        var showCells = Handler == null && settings.GridDebugMode.Value;
        try
        {
            showCells |= (Handler?.MaxLanternCount ?? 0) > 7;
        }
        catch
        {
            // handler ilegível
        }

        // Heat do plano: células pintadas pela força do multiplicador do solve.
        if (showCells && _plannedMults != null)
        {
            var maxMult = 0.01;
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    maxMult = Math.Max(maxMult, _plannedMults[r, c]);
                }
            }

            for (var rowFromTop = 0; rowFromTop < 3; rowFromTop++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var boardRow = 2 - rowFromTop;
                    var alpha = (float)(0.08 + 0.30 * _plannedMults[boardRow, c] / maxMult);
                    var heatCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0.1f, 0.9f, 0.2f, alpha));
                    var cellMin = origin + new Vector2(canvasW * c / 3f, canvasH * rowFromTop / 3f);
                    var cellMax = origin + new Vector2(canvasW * (c + 1) / 3f, canvasH * (rowFromTop + 1) / 3f);
                    drawList.AddRectFilled(cellMin, cellMax, heatCol);
                }
            }
        }

        if (showCells)
        {
            // Grid 3x3 (bordas + linhas internas).
            for (var i = 0; i <= 3; i++)
            {
                var x = canvasW * i / 3f;
                var y = canvasH * i / 3f;
                drawList.AddLine(origin + new Vector2(x, 0), origin + new Vector2(x, canvasH), gridCol, 1f);
                drawList.AddLine(origin + new Vector2(0, y), origin + new Vector2(canvasW, y), gridCol, 1f);
            }

            // Labels por célula (convenção do board: (0,0) = canto inferior esquerdo).
            for (var rowFromTop = 0; rowFromTop < 3; rowFromTop++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var boardRow = 2 - rowFromTop;
                    var idx = boardRow * 3 + c;
                    var label = $"({boardRow},{c})";
                    if (_cellFirstOrder[idx] > 0)
                    {
                        label += $" #{_cellFirstOrder[idx]} {(int)(_cellSeconds[idx] / 60)}:{(int)(_cellSeconds[idx] % 60):D2}";
                    }

                    var labelPos = origin + new Vector2(canvasW * c / 3f + 3, canvasH * rowFromTop / 3f + 3);
                    drawList.AddText(labelPos, gridCol, label);
                }
            }
        }
        else
        {
            // Chart solo: só a moldura da sala.
            drawList.AddRect(origin, origin + new Vector2(canvasW, canvasH), gridCol);
        }

        // Markers conhecidos (chests/eventos ainda não consumidos).
        foreach (var cached in _cachedEntities.Values)
        {
            drawList.AddCircleFilled(ToCanvas(cached.GridPos), 2.5f, markerCol);
        }

        // Trilha do personagem (amostrada); saltos grandes (teleporte) não viram linha.
        var step = Math.Max(1, _pathBreadcrumbs.Count / 300);
        for (var i = step; i < _pathBreadcrumbs.Count; i += step)
        {
            if (Vector2.Distance(_pathBreadcrumbs[i - step], _pathBreadcrumbs[i]) > 250)
            {
                continue;
            }

            drawList.AddLine(ToCanvas(_pathBreadcrumbs[i - step]), ToCanvas(_pathBreadcrumbs[i]), pathCol, 1.5f);
        }

        // Jogador.
        drawList.AddCircleFilled(ToCanvas(playerPos), 4f, playerCol);

        ImGui.Dummy(new Vector2(canvasW, canvasH));
        ImGui.End();
    }
}
