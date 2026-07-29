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
    // Renderização anti-poluição: a informação de lucro é ONDE o alvo está, então o
    // padrão é 1 marcador por alvo NÃO revelado (dedup entre chamas). Linhas
    // chama→alvo são opcionais e limitadas às chamas mais próximas do jogador.
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

        var flames = new List<(Entity E, float Dist, List<Vector2> Targets, Vector2 GridDir)>();
        foreach (var e in _pointerEntities.Values.ToList())
        {
            if (e is not { IsValid: true })
            {
                continue;
            }

            var targets = ReadPointerTargets(e, out var gridDir);
            flames.Add((e, Vector2.Distance(_playerGridPos, e.GridPosNum), targets, gridDir));
        }

        // 1) Marcadores dedupados: só alvos NÃO revelados (os revelados já têm ícone).
        if (settings.ShowTargetMarkers)
        {
            var seenTargets = new HashSet<(int, int)>();
            foreach (var (e, _, targets, _) in flames)
            {
                foreach (var target in targets)
                {
                    if (!seenTargets.Add(((int)(target.X / 20), (int)(target.Y / 20))))
                    {
                        continue;
                    }

                    var label = ResolveTargetLabel(target);
                    debugRows?.Add(new HintRow(label ?? "Unrevealed", e.Id, false, e.GridPosNum, target));
                    if (label != null)
                    {
                        continue;
                    }

                    var text = $"? {Vector2.Distance(_playerGridPos, target):F0}";
                    if (settings.ShowHintsInWorld)
                    {
                        var screen = GetWorldScreenPosition(target);
                        if (IsRoughlyOnScreen(screen))
                        {
                            Graphics.DrawTextWithBackground(text, screen, settings.UnrevealedColor, Color.Black);
                        }
                    }

                    if (settings.ShowHintsOnMap && _largeMapOpen)
                    {
                        Graphics.DrawTextWithBackground(text, Graphics.GridToMap(target, _playerGridPos),
                            settings.UnrevealedColor, Color.Black);
                    }
                }
            }
        }

        // 2) Linhas: opcionais, só das chamas mais próximas dentro do alcance.
        if (settings.ShowRayLines)
        {
            var maxRange = settings.MaxPointerRangeGridUnits.Value;
            foreach (var (e, dist, targets, gridDir) in flames
                         .Where(f => maxRange == 0 || f.Dist <= maxRange)
                         .OrderBy(f => f.Dist)
                         .Take(settings.MaxRayFlames.Value))
            {
                var startGrid = e.GridPosNum;
                if (targets.Count == 0)
                {
                    // Fallback: componente ilegível, raio pela rotação da entidade.
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
                    var resolved = ResolveTargetLabel(targets[i]) != null;
                    if (resolved && settings.HideResolvedRays)
                    {
                        continue;
                    }

                    var color = resolved ? settings.RayColor.Value : settings.UnrevealedColor.Value;
                    DrawHintLine(startGrid, targets[i], color, i == activeIdx ? 3 : 1);
                }
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
            var s = GetWorldScreenPosition(startGrid);
            var t = GetWorldScreenPosition(endGrid);
            if (IsRoughlyOnScreen(s) || IsRoughlyOnScreen(t))
            {
                Graphics.DrawLine(s, t, thickness, color);
            }
        }

        if (settings.ShowHintsOnMap && _largeMapOpen)
        {
            Graphics.DrawLine(Graphics.GridToMap(startGrid, _playerGridPos),
                Graphics.GridToMap(endGrid, _playerGridPos), thickness, color);
        }
    }

    private bool IsRoughlyOnScreen(Vector2 screenPos)
    {
        var rect = GameController.Window.GetWindowRectangleTimeCache;
        const float margin = 100f;
        return screenPos.X > -margin && screenPos.Y > -margin &&
               screenPos.X < rect.Width + margin && screenPos.Y < rect.Height + margin;
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
