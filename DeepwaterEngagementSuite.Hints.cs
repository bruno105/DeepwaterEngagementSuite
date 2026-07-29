using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Helpers;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // Chamas de lantern (Deepwater/Objects/Pointer) apontam na direção de recompensas
    // escondidas — mesma lógica do hint de Ritual. Cache via EntityAdded para não
    // varrer a lista de entidades a cada frame.
    private readonly Dictionary<uint, Entity> _pointerEntities = new();

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

        foreach (var e in _pointerEntities.Values.ToList())
        {
            if (e is not { IsValid: true })
            {
                continue;
            }

            var startGrid = e.GridPosNum;
            Vector2 endGrid;
            if (e.TryGetComponent<Beam>(out var beam) && beam != null)
            {
                endGrid = beam.BeamEndNum.WorldToGrid();
            }
            else if (e.GetComponent<Positioned>() is { } pos)
            {
                var (sin, cos) = MathF.SinCos(pos.Rotation);
                var dir = new Vector2(sin, -cos);
                endGrid = startGrid + dir * settings.RayLengthGridUnits.Value;
            }
            else
            {
                continue;
            }

            if (settings.HideResolvedRays && RayResolvesToKnownEntity(startGrid, endGrid))
            {
                continue;
            }

            if (settings.ShowHintsInWorld)
            {
                Graphics.DrawLine(GetWorldScreenPosition(startGrid), GetWorldScreenPosition(endGrid), 2,
                    settings.RayColor);
            }

            if (settings.ShowHintsOnMap && _largeMapOpen)
            {
                Graphics.DrawLine(Graphics.GridToMap(startGrid, _playerGridPos),
                    Graphics.GridToMap(endGrid, _playerGridPos), 1, settings.RayColor);
            }
        }
    }

    private bool RayResolvesToKnownEntity(Vector2 rayStart, Vector2 rayEnd)
    {
        const float toleranceGridUnits = 30f;
        foreach (var cached in _cachedEntities.Values)
        {
            if (DistancePointToSegment(cached.GridPos, rayStart, rayEnd) < toleranceGridUnits)
            {
                return true;
            }
        }

        return false;
    }

    private static float DistancePointToSegment(Vector2 point, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared();
        if (lengthSq < 1e-6f)
        {
            return Vector2.Distance(point, a);
        }

        var t = Math.Clamp(Vector2.Dot(point - a, ab) / lengthSq, 0f, 1f);
        return Vector2.Distance(point, a + ab * t);
    }
}
