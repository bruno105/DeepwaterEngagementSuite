using System;
using System.Collections.Generic;
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

    private int GridCellIndex(Vector2 gridPos)
    {
        Vector2i dims;
        try
        {
            dims = GameController.IngameState.Data.AreaDimensions;
        }
        catch
        {
            return -1;
        }

        if (dims.X <= 0 || dims.Y <= 0)
        {
            return -1;
        }

        // Convenção do board da voyage: linha 0 = embaixo do canvas. Com o Y do mundo
        // crescendo para o norte (flip no canvas), gridY baixo = embaixo = linha 0.
        var c = Math.Clamp((int)(gridPos.X * 3 / dims.X), 0, 2);
        var row = Math.Clamp((int)(gridPos.Y * 3 / dims.Y), 0, 2);
        return row * 3 + c;
    }

    private void GridTrackReset()
    {
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

        if (dims.X <= 0 || dims.Y <= 0 || playerPos == default)
        {
            return;
        }

        if (!ImGui.Begin("Grid Tracker", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var canvasW = (float)settings.GridWindowSize.Value;
        var canvasH = canvasW * dims.Y / dims.X;
        var origin = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Y do mundo cresce para o norte: canvas desenha gridY alto no TOPO (flip).
        Vector2 ToCanvas(Vector2 grid) =>
            origin + new Vector2(grid.X / dims.X * canvasW, (1f - grid.Y / dims.Y) * canvasH);

        var gridCol = ImGui.ColorConvertFloat4ToU32(settings.GridColor.Value.ToImguiVec4());
        var pathCol = ImGui.ColorConvertFloat4ToU32(settings.PathColor.Value.ToImguiVec4());
        var markerCol = ImGui.ColorConvertFloat4ToU32(Color.Gold.ToImguiVec4());
        var playerCol = ImGui.ColorConvertFloat4ToU32(Color.White.ToImguiVec4());
        var bgCol = ImGui.ColorConvertFloat4ToU32(new Vector4(0f, 0f, 0f, 0.45f));

        drawList.AddRectFilled(origin, origin + new Vector2(canvasW, canvasH), bgCol);

        // Terreno gerado pelo Radar (textura "radar_minimap" cobre a área inteira),
        // esticado para o canvas. Sem o Radar carregado, fica só o fundo escuro.
        try
        {
            if (Graphics.HasImage("radar_minimap"))
            {
                // UV com V invertido para acompanhar o flip do eixo Y do canvas.
                drawList.AddImage(Graphics.GetTextureId("radar_minimap"),
                    origin, origin + new Vector2(canvasW, canvasH),
                    new Vector2(0, 1), new Vector2(1, 0));
            }
        }
        catch
        {
            // Radar ausente ou textura ainda não gerada para a área
        }

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

        // Markers conhecidos (chests/eventos ainda não consumidos).
        foreach (var cached in _cachedEntities.Values)
        {
            drawList.AddCircleFilled(ToCanvas(cached.GridPos), 2.5f, markerCol);
        }

        // Trilha do personagem (amostrada).
        var step = Math.Max(1, _pathBreadcrumbs.Count / 300);
        for (var i = step; i < _pathBreadcrumbs.Count; i += step)
        {
            drawList.AddLine(ToCanvas(_pathBreadcrumbs[i - step]), ToCanvas(_pathBreadcrumbs[i]), pathCol, 1.5f);
        }

        // Jogador.
        drawList.AddCircleFilled(ToCanvas(playerPos), 4f, playerCol);

        ImGui.Dummy(new Vector2(canvasW, canvasH));
        ImGui.End();
    }
}
