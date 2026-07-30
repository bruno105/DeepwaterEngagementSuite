using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // "Voyage Pilot": painel in-run com o comportamento da estratégia ativa + seta para
    // o próximo objetivo. Prioridades por tipo de marker, com overrides por estratégia.
    // Objetivos que dão valor para o RESTO do run (buffs/recursos): a ordem importa —
    // pegá-los cedo multiplica tudo que vem depois, então a prioridade decai com o tempo.
    private static readonly HashSet<string> PilotBuffKinds =
        ["GoldenLantern", "LanternReplenishEncounter", "AltarCrab", "AltarOctopus"];

    private List<Vector2i> _pilotRoute;
    private Vector2 _pilotRouteTarget;
    private CancellationTokenSource _pilotRouteCts;
    private DateTime _lastRouteRequest = DateTime.MinValue;

    private static readonly Dictionary<string, int> PilotBasePriority = new()
    {
        ["GoldenLantern"] = 96,
        ["Unrevealed"] = 80,
        ["CurrencyTreasureChest"] = 70,
        ["BottledItemChest"] = 70,
        ["ScarabChest"] = 65,
        ["StackedDecksChest"] = 65,
        ["MapsChest"] = 60,
        ["AllflameEmbersChest"] = 55,
        ["LanternReplenishEncounter"] = 55,
        ["CursedDucatDrop"] = 45,
        ["IzaroObject"] = 40,
        ["GoldTreasureChest"] = 35,
        ["AltarCrab"] = 35,
        ["AltarOctopus"] = 35,
        ["ClamTreasureChest"] = 30,
        ["UniqueWeaponChest"] = 30,
        ["UniqueArmourChest"] = 30,
    };

    private static readonly Dictionary<string, Dictionary<string, int>> PilotStrategyOverrides = new()
    {
        ["Speedrun"] = new Dictionary<string, int>
        {
            ["CurrencyTreasureChest"] = 100,
            ["BottledItemChest"] = 95,
            ["ScarabChest"] = 90,
            ["StackedDecksChest"] = 90,
            ["Unrevealed"] = 85,
            ["MapsChest"] = 80,
            ["GoldTreasureChest"] = 20,
            ["ClamTreasureChest"] = 15,
        },
        ["Meatfish"] = new Dictionary<string, int>
        {
            ["GoldenLantern"] = 110,
            ["LanternReplenishEncounter"] = 100,
            ["AllflameEmbersChest"] = 70,
        },
        ["DivineBorder"] = new Dictionary<string, int>
        {
            ["GoldenLantern"] = 70,
            ["Unrevealed"] = 50,
        },
    };

    /// <summary>
    /// Barco e fundo do mar são a MESMA instância (sair do chart = teleporte no mapa):
    /// "no campo" = dentro de alguma bolha de ar do handler (raio ×1.5).
    /// Sem bolhas/handler (mapas normais em debug), considera sempre no campo.
    /// </summary>
    private bool IsPlayerInField(Vector2 playerGridPos)
    {
        try
        {
            var bubbles = Bubbles;
            if (bubbles is { Count: > 0 })
            {
                return bubbles.Any(b =>
                    Vector2.Distance(playerGridPos, new Vector2(b.Position.X, b.Position.Y)) <= b.Radius * 1.5f);
            }
        }
        catch
        {
            // sem dados de bolhas, não esconder nada
        }

        return true;
    }

    private void DrawVoyagePilot()
    {
        var settings = Settings.PilotSettings;
        if (!settings.ShowPilotPanel)
        {
            return;
        }

        if (_cachedEntities.Count == 0 && _pointerEntities.Count == 0)
        {
            return;
        }

        var strategyName = _activeStrategy?.Name ?? "Base";

        var inField = IsPlayerInField(_playerGridPos);

        if (!inField)
        {
            if (ImGui.Begin("Voyage Pilot", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextColored(Color.Gray.ToImguiVec4(),
                    $"{strategyName} - fora das bolhas (no barco?). Sem objetivos ativos.");
            }

            ImGui.End();
            return;
        }

        var overrides = PilotStrategyOverrides.GetValueOrDefault(strategyName);
        var elapsed = DateTime.UtcNow - _statsAreaStart;
        // Decaimento dos objetivos de buff: valor cheio no início, piso de 35% aos 13+ min.
        var buffDecay = Math.Max(0.35, 1.0 - elapsed.TotalMinutes / 20.0);

        int Priority(string kind) =>
            overrides?.GetValueOrDefault(kind, PilotBasePriority.GetValueOrDefault(kind, 10))
            ?? PilotBasePriority.GetValueOrDefault(kind, 10);

        int EffectivePriority(string kind)
        {
            var prio = Priority(kind);
            return PilotBuffKinds.Contains(kind) ? (int)(prio * buffDecay) : prio;
        }

        // Objetivos: markers conhecidos (não abertos), alvos unrevealed e (fase de kill) rares vivos.
        var objectives = new List<(string Label, Vector2 Pos, int Prio)>();
        var kindCounts = new Dictionary<string, int>();
        foreach (var cached in _cachedEntities.Values)
        {
            var kind = GetChestType(cached.Path).ToString();
            kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
            objectives.Add((kind, cached.GridPos, EffectivePriority(kind)));
        }

        var seenTargets = new HashSet<(int, int)>();
        foreach (var e in _pointerEntities.Values.ToList())
        {
            if (e is not { IsValid: true })
            {
                continue;
            }

            foreach (var target in ReadPointerTargets(e, out _))
            {
                if (!seenTargets.Add(((int)(target.X / 20), (int)(target.Y / 20))) ||
                    ResolveTargetLabel(target) != null)
                {
                    continue;
                }

                kindCounts["Unrevealed"] = kindCounts.GetValueOrDefault("Unrevealed") + 1;
                objectives.Add(("Unrevealed", target, Priority("Unrevealed")));
            }
        }

        // Tiles do PLANO ainda não visitados viram objetivos: o de maior multiplicador
        // puxa a rota — evita deixar o tile jackpot para o fim (e não alcançá-lo).
        if (_plannedMults != null && _regionComputed)
        {
            var maxPlanned = 0.01;
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    maxPlanned = Math.Max(maxPlanned, _plannedMults[r, c]);
                }
            }

            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    var idx = r * 3 + c;
                    if (_cellFirstOrder[idx] > 0)
                    {
                        continue; // já visitado
                    }

                    var center = new Vector2(
                        _gridOrigin.X + (c + 0.5f) / 3f * _gridSize.X,
                        _gridOrigin.Y + (r + 0.5f) / 3f * _gridSize.Y);
                    // Centro geométrico pode ser inandável — snap para o terreno,
                    // senão o Radar não consegue rotear e a seta vira linha reta.
                    var target = SnapToWalkable(center) ?? center;
                    var prio = 40 + (int)(60 * _plannedMults[r, c] / maxPlanned);
                    objectives.Add(($"Tile ({r},{c}) x{_plannedMults[r, c]:F1}", target, prio));
                }
            }
        }

        var lanternsLeft = kindCounts.GetValueOrDefault("LanternReplenishEncounter");
        var meatfishKillPhase = strategyName == "Meatfish" && lanternsLeft == 0;
        if (strategyName == "DivineBorder" || meatfishKillPhase)
        {
            try
            {
                foreach (var monster in Handler?.Monsters ?? [])
                {
                    if (monster is { IsValid: true, IsAlive: true } &&
                        monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique)
                    {
                        objectives.Add(($"{monster.Rarity}", monster.GridPosNum,
                            strategyName == "DivineBorder" ? 95 : 75));
                    }
                }
            }
            catch
            {
                // lista de monstros pode falhar em transição de área
            }
        }

        var next = objectives
            .OrderByDescending(o => o.Prio)
            .ThenBy(o => Vector2.Distance(_playerGridPos, o.Pos))
            .Cast<(string Label, Vector2 Pos, int Prio)?>()
            .FirstOrDefault();

        // Seta/rota para o próximo objetivo (mundo + mapa). Com o Radar instalado, a
        // rota segue o terreno andável (Radar.LookForRoute via PluginBridge).
        if (settings.ShowObjectiveArrow && next is { } obj)
        {
            var color = settings.ObjectiveColor.Value;
            var targetScreen = GetWorldScreenPosition(obj.Pos);

            // Re-pede a rota quando o alvo muda OU quando a última tentativa falhou
            // (alvo inandável, pathfinding ainda não pronto) — senão fica preso na reta.
            var targetChanged = Vector2.Distance(_pilotRouteTarget, obj.Pos) >= 40;
            if (settings.UseRadarRoute.Value &&
                (targetChanged || _pilotRoute == null) &&
                (DateTime.UtcNow - _lastRouteRequest).TotalSeconds >= 2)
            {
                _lastRouteRequest = DateTime.UtcNow;
                var lookForRoute = GameController.PluginBridge
                    .GetMethod<Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>>("Radar.LookForRoute");
                if (lookForRoute != null)
                {
                    _pilotRouteCts?.Cancel();
                    _pilotRouteCts = new CancellationTokenSource();
                    _pilotRouteTarget = obj.Pos;
                    _pilotRoute = null;
                    try
                    {
                        // Baús/drops/alvos do Pointer podem estar sobre decoração
                        // inandável — rotear até o ponto andável mais próximo, senão
                        // o Radar falha para sempre (o retry repete o mesmo alvo).
                        var routeTarget = SnapToWalkable(obj.Pos) ?? obj.Pos;
                        lookForRoute(routeTarget, route => _pilotRoute = route, _pilotRouteCts.Token);
                    }
                    catch
                    {
                        // Radar pode não ter pathfinding pronto ainda
                    }
                }
            }

            var route = _pilotRoute;
            var routeValid = settings.UseRadarRoute.Value && route is { Count: > 1 } &&
                             Vector2.Distance(_pilotRouteTarget, obj.Pos) < 40;
            if (routeValid)
            {
                var step = Math.Max(1, route.Count / 80);
                for (var i = step; i < route.Count; i += step)
                {
                    var a = new Vector2(route[i - step].X, route[i - step].Y);
                    var b = new Vector2(route[i].X, route[i].Y);
                    if (_largeMapOpen)
                    {
                        Graphics.DrawLine(Graphics.GridToMap(a, _playerGridPos),
                            Graphics.GridToMap(b, _playerGridPos), 2, color);
                    }

                    // No mundo, só o trecho próximo ao jogador (custo de terrain height).
                    if (Vector2.Distance(_playerGridPos, a) <= 150 || Vector2.Distance(_playerGridPos, b) <= 150)
                    {
                        Graphics.DrawLine(GetWorldScreenPosition(a), GetWorldScreenPosition(b), 3, color);
                    }
                }
            }
            else
            {
                var playerScreen = GetWorldScreenPosition(_playerGridPos);
                if (IsRoughlyOnScreen(playerScreen) || IsRoughlyOnScreen(targetScreen))
                {
                    Graphics.DrawLine(playerScreen, targetScreen, 3, color);
                }

                if (_largeMapOpen)
                {
                    Graphics.DrawLine(Graphics.GridToMap(_playerGridPos, _playerGridPos),
                        Graphics.GridToMap(obj.Pos, _playerGridPos), 2, color);
                }
            }

            if (IsRoughlyOnScreen(targetScreen))
            {
                Graphics.DrawTextWithBackground(
                    $"> {obj.Label} ({Vector2.Distance(_playerGridPos, obj.Pos):F0})",
                    targetScreen + new Vector2(0, -18), color, Color.Black);
            }
        }

        // Painel.
        if (!ImGui.Begin("Voyage Pilot", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var extractLimit = strategyName switch
        {
            "Speedrun" => settings.SpeedrunExtractMinutes.Value,
            "Meatfish" => settings.MeatfishExtractMinutes.Value,
            _ => 0,
        };
        var overTime = extractLimit > 0 && elapsed.TotalMinutes > extractLimit;
        ImGui.TextColored((overTime ? Color.OrangeRed : Color.LightGreen).ToImguiVec4(),
            $"{strategyName}  {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}" +
            (extractLimit > 0 ? $" / {extractLimit}min{(overTime ? "  EXTRAIA!" : "")}" : ""));

        var behavior = strategyName switch
        {
            "Speedrun" => "Abra boxes/eventos, ignore trash. Extraia no limite.",
            "Meatfish" when !meatfishKillPhase => $"FASE 1: colete os lanterns ({lanternsLeft} restantes)",
            "Meatfish" => "FASE 2: full clear de rares/uniques. Extraia por ultimo.",
            "DivineBorder" => "Full-clear de rares na regiao do border Divine.",
            _ => "Siga os marcadores; abra o que tiver valor.",
        };
        ImGui.TextUnformatted(behavior);

        var currentCell = GridCellIndex(_playerGridPos);
        if (currentCell >= 0)
        {
            var cellText = $"Celula: ({currentCell / 3},{currentCell % 3})";
            if (_plannedMults != null)
            {
                cellText += $"  mult x{_plannedMults[currentCell / 3, currentCell % 3]:F2}";
            }

            ImGui.TextUnformatted(cellText);
        }

        var maxLanterns = Handler?.MaxLanternCount ?? 0;
        if (maxLanterns > 0 && PlacedLanternCount >= maxLanterns * 0.9)
        {
            ImGui.TextColored(Color.OrangeRed.ToImguiVec4(),
                "Lanterns no fim - planeje o RETORNO (apagam em ordem reversa)");
        }

        int? sulphur = null;
        try
        {
            sulphur = Handler?.Sulphur;
        }
        catch
        {
            // fora de contexto deepwater
        }

        var goldenLanternStacks = 0;
        try
        {
            goldenLanternStacks = GameController.Player?.GetComponent<Buffs>()?.BuffsList?
                .Where(b => b?.Name == "deepwater_golden_lantern")
                .Sum(b => b.Charges) ?? 0;
        }
        catch
        {
            // buffs ilegíveis em transição
        }

        var rares = _statsMonsters.GetValueOrDefault(MonsterRarity.Rare);
        var uniques = _statsMonsters.GetValueOrDefault(MonsterRarity.Unique);
        ImGui.TextUnformatted(
            $"Kills R/U: {rares}/{uniques}   Lanterns: {PlacedLanternCount}/{Handler?.MaxLanternCount ?? 0}" +
            (goldenLanternStacks > 0 ? $"   GL buff: {goldenLanternStacks}" : "") +
            (sulphur != null ? $"   Sulphur: {sulphur:N0}" : ""));

        if (kindCounts.Count > 0)
        {
            var summary = string.Join("  ", kindCounts
                .OrderByDescending(kv => Priority(kv.Key))
                .Take(4)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
            ImGui.TextUnformatted($"Restam: {summary}");
        }

        if (next is { } n)
        {
            ImGui.TextColored(settings.ObjectiveColor.Value.ToImguiVec4(),
                $"Proximo: {n.Label} ({Vector2.Distance(_playerGridPos, n.Pos):F0})");
        }

        ImGui.End();
    }
}
