using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // Chamas de lantern (Deepwater/Objects/Pointer) apontam recompensas via o
    // componente Pointer (nativo no ExileCore desde 2026-07-29: Targets).
    // Cache via EntityAdded para não varrer a lista de entidades a cada frame.
    private readonly Dictionary<uint, Entity> _pointerEntities = new();

    private readonly record struct HintRow(string Label, uint EntityId, bool Active, Vector2 Start, Vector2 End);

    private void TrackPointerEntity(Entity entity)
    {
        if (entity?.Path?.Contains("Deepwater/Objects/Pointer", StringComparison.Ordinal) == true)
        {
            _pointerEntities[entity.Id] = entity;
        }
    }

    private void DrawPointerHints()
    {
        var settings = Settings.HintSettings;
        if (!settings.ShowPointerHints || _pointerEntities.Count == 0)
        {
            return;
        }

        var debugRows = settings.ShowHintsDebugWindow.Value ? new List<HintRow>() : null;

        foreach (var e in _pointerEntities.Values.ToList())
        {
            if (e is not { IsValid: true })
            {
                continue;
            }

            var startGrid = e.GridPosNum;
            var targets = ReadPointerTargets(e, out var gridDir);

            if (targets.Count == 0)
            {
                // Fallback: sem componente legível, desenha raio pela rotação da entidade.
                if (e.GetComponent<Positioned>() is not { } pos)
                {
                    continue;
                }

                var (sin, cos) = MathF.SinCos(pos.Rotation);
                var endGrid = startGrid + new Vector2(sin, -cos) * settings.RayLengthGridUnits.Value;
                DrawHintLine(startGrid, endGrid, settings.RayColor, 2);
                debugRows?.Add(new HintRow("(fallback ray)", e.Id, false, startGrid, endGrid));
                continue;
            }

            // Alvo "ativo" = o mais alinhado com a direção selecionada do componente.
            var activeIdx = -1;
            if (gridDir != default)
            {
                var bestDot = -2f;
                for (var i = 0; i < targets.Count; i++)
                {
                    var toTarget = targets[i] - startGrid;
                    if (toTarget.LengthSquared() < 1e-3f)
                    {
                        continue;
                    }

                    var dot = Vector2.Dot(Vector2.Normalize(toTarget), gridDir);
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        activeIdx = i;
                    }
                }
            }

            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var label = ResolveTargetLabel(target);
                var resolved = label != null;
                if (resolved && settings.HideResolvedRays)
                {
                    debugRows?.Add(new HintRow(label, e.Id, i == activeIdx, startGrid, target));
                    continue;
                }

                label ??= $"Unrevealed ({Vector2.Distance(_playerGridPos, target):F0})";
                var color = resolved ? settings.RayColor.Value : settings.UnrevealedColor.Value;
                var thickness = i == activeIdx ? 3 : 1;
                DrawHintLine(startGrid, target, color, thickness);

                if (settings.ShowHintsInWorld)
                {
                    Graphics.DrawTextWithBackground(label, GetWorldScreenPosition(target), color, Color.Black);
                }

                if (settings.ShowHintsOnMap && _largeMapOpen)
                {
                    Graphics.DrawTextWithBackground(label, Graphics.GridToMap(target, _playerGridPos), color, Color.Black);
                }

                debugRows?.Add(new HintRow(label, e.Id, i == activeIdx, startGrid, target));
            }
        }

        if (debugRows != null)
        {
            DrawHintsDebugWindow(debugRows);
        }
    }

    private void DrawHintLine(Vector2 startGrid, Vector2 endGrid, Color color, int thickness)
    {
        var settings = Settings.HintSettings;
        if (settings.ShowHintsInWorld)
        {
            Graphics.DrawLine(GetWorldScreenPosition(startGrid), GetWorldScreenPosition(endGrid), thickness, color);
        }

        if (settings.ShowHintsOnMap && _largeMapOpen)
        {
            Graphics.DrawLine(Graphics.GridToMap(startGrid, _playerGridPos),
                Graphics.GridToMap(endGrid, _playerGridPos), thickness, color);
        }
    }

    private List<Vector2> ReadPointerTargets(Entity e, out Vector2 gridDir)
    {
        gridDir = default;
        try
        {
            var pointer = e.GetComponent<Pointer>();
            if (pointer == null || pointer.Address == 0)
            {
                return [];
            }

            var targets = pointer.Targets;
            if (targets is not { Count: > 0 and <= 32 })
            {
                return [];
            }

            // A direção do alvo selecionado ainda não é exposta pelo componente nativo:
            // raw em +0x58, rotacionada 90° do espaço de grid (gridDir = (Y, -X)).
            var raw = e.M.Read<Vector2>(pointer.Address + 0x58);
            gridDir = new Vector2(raw.Y, -raw.X);
            if (gridDir != default)
            {
                gridDir = Vector2.Normalize(gridDir);
            }

            return targets.Select(t => new Vector2(t.X, t.Y)).ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>Nome do baú/evento conhecido perto do alvo, ou null se ainda não revelado.</summary>
    private string ResolveTargetLabel(Vector2 targetGrid)
    {
        const float toleranceGridUnits = 20f;
        foreach (var cached in _cachedEntities.Values)
        {
            if (Vector2.Distance(cached.GridPos, targetGrid) < toleranceGridUnits)
            {
                return GetChestType(cached.Path).ToString();
            }
        }

        return null;
    }

    private void DrawHintsDebugWindow(List<HintRow> rows)
    {
        if (!ImGui.Begin("Deepwater hints debug"))
        {
            ImGui.End();
            return;
        }

        ImGui.Text($"Rays: {rows.Count}");
        if (ImGui.BeginTable("HintRows", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Target");
            ImGui.TableSetupColumn("Id");
            ImGui.TableSetupColumn("Active");
            ImGui.TableSetupColumn("Start");
            ImGui.TableSetupColumn("End");
            ImGui.TableHeadersRow();
            foreach (var row in rows)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(row.Label);
                ImGui.TableNextColumn();
                ImGui.Text($"{row.EntityId}");
                ImGui.TableNextColumn();
                ImGui.Text(row.Active ? "yes" : "");
                ImGui.TableNextColumn();
                ImGui.Text($"{row.Start.X:F0},{row.Start.Y:F0}");
                ImGui.TableNextColumn();
                ImGui.Text($"{row.End.X:F0},{row.End.Y:F0}");
            }

            ImGui.EndTable();
        }

        ImGui.End();
    }
}
