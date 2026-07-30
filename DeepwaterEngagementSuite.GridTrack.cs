using System;
using System.Collections.Generic;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

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

        var c = Math.Clamp((int)(gridPos.X * 3 / dims.X), 0, 2);
        var r = Math.Clamp((int)(gridPos.Y * 3 / dims.Y), 0, 2);
        return r * 3 + c;
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

        bool largeMapOpen;
        Vector2i dims;
        Vector2 playerPos;
        try
        {
            largeMapOpen = GameController.Game.IngameState.IngameUi.Map.LargeMap.AsObject<SubMap>().IsVisible;
            dims = GameController.IngameState.Data.AreaDimensions;
            playerPos = GameController.Player?.GetComponent<Positioned>()?.WorldPosNum.WorldToGrid() ?? default;
        }
        catch
        {
            return;
        }

        if (!largeMapOpen || dims.X <= 0 || dims.Y <= 0 || playerPos == default)
        {
            return;
        }

        var gridColor = settings.GridColor.Value;

        // Bordas + linhas internas do grid 3x3.
        for (var i = 0; i <= 3; i++)
        {
            var x = dims.X * i / 3f;
            var y = dims.Y * i / 3f;
            Graphics.DrawLine(Graphics.GridToMap(new Vector2(x, 0), playerPos),
                Graphics.GridToMap(new Vector2(x, dims.Y), playerPos), 1, gridColor);
            Graphics.DrawLine(Graphics.GridToMap(new Vector2(0, y), playerPos),
                Graphics.GridToMap(new Vector2(dims.X, y), playerPos), 1, gridColor);
        }

        // Labels por célula: coordenada, ordem de entrada e tempo acumulado.
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                var idx = r * 3 + c;
                var center = new Vector2(dims.X * (c + 0.5f) / 3f, dims.Y * (r + 0.5f) / 3f);
                var label = $"({r},{c})";
                if (_cellFirstOrder[idx] > 0)
                {
                    label += $" #{_cellFirstOrder[idx]} {(int)(_cellSeconds[idx] / 60)}:{(int)(_cellSeconds[idx] % 60):D2}";
                }

                Graphics.DrawTextWithBackground(label, Graphics.GridToMap(center, playerPos), gridColor, Color.Black);
            }
        }

        // Trilha do personagem (amostrada para limitar draw calls).
        var pathColor = settings.PathColor.Value;
        var step = Math.Max(1, _pathBreadcrumbs.Count / 250);
        for (var i = step; i < _pathBreadcrumbs.Count; i += step)
        {
            Graphics.DrawLine(Graphics.GridToMap(_pathBreadcrumbs[i - step], playerPos),
                Graphics.GridToMap(_pathBreadcrumbs[i], playerPos), 2, pathColor);
        }
    }
}
