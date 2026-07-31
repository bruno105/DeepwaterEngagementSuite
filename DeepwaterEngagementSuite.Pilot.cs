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
        ["GoldenLantern", "LanternReplenishEncounter", "AltarCrab", "AltarOctopus", "WispTile"];

    // Peças-chave do PLANO viram objetivos sintéticos no tile onde foram colocadas:
    // entidades distantes não carregam até chegarmos perto, então sem isso o Pilot
    // só "descobre" o chart de GL no canto oposto quando já é tarde (decay). Cada
    // estratégia persegue as SUAS peças (doutrina do site/Milky); o kind reusa a
    // tabela de prioridades.
    private static readonly Dictionary<string, (string ModKey, string Kind)[]> StrategyPieceObjectives = new()
    {
        ["Meatfish"] =
        [
            ("AdjacentGoldenLanterns", "GoldenLantern"),
            ("AdjacentStarfish", "StarfishTile"),
            ("AdjacentPantheon", "PantheonTile"),
            ("Room:Sea Pillars", "PillarTile"),
        ],
        ["Speedrun"] =
        [
            ("AdjacentOperativeBox", "BoxTile"),
            ("AdjacentDivinerBox", "BoxTile"),
            ("AdjacentLostMessage", "BoxTile"),
            ("AdjacentArcanistBox", "BoxTile"),
            ("AdjacentStrongboxes", "BoxTile"),
        ],
        ["DivineBorder"] =
        [
            ("Room:Sea Pillars", "DivinePieceTile"),
            ("AdjacentStrongboxes", "FeederTile"),
            ("AdjacentStarfish", "FeederTile"),
        ],
        ["DivineBoxes"] =
        [
            ("Room:Pelagic Abyss", "DivinePieceTile"),
            ("AdjacentStrongboxes", "FeederTile"),
            ("AdjacentOperativeBox", "FeederTile"),
            ("AdjacentDivinerBox", "FeederTile"),
            ("AdjacentArcanistBox", "FeederTile"),
        ],
        ["Ethereal"] =
        [
            ("AdjacentWisps", "WispTile"),
            ("AdjacentGoldenLanterns", "GoldenLantern"),
        ],
    };

    // Sem estratégia (Base/AlcGo): só GL vale travessia — o resto é queima local.
    private static readonly (string ModKey, string Kind)[] DefaultPieceObjectives =
        [("AdjacentGoldenLanterns", "GoldenLantern")];

    // Pedágio de distância por estratégia (prio por unidade): queima = local e
    // rápido; Divine = o tile certo vale a caminhada.
    private static readonly Dictionary<string, double> StrategyDistancePenalty = new()
    {
        ["AlcGo"] = 0.06,
        ["Speedrun"] = 0.045,
        ["DivineBorder"] = 0.02,
        ["DivineBoxes"] = 0.02,
    };

    private List<Vector2i> _pilotRoute;
    private Vector2 _pilotRouteTarget;
    private CancellationTokenSource _pilotRouteCts;
    private DateTime _lastRouteRequest = DateTime.MinValue;
    // Falhas de rota POR ALVO: um contador global zerava quando a seleção alternava
    // entre dois alvos ruins e o blacklist nunca disparava.
    private readonly Dictionary<(int X, int Y), int> _pilotRouteFailCounts = new();
    private readonly Dictionary<(int X, int Y), DateTime> _pilotUnreachable = new();

    private static (int X, int Y) QuantizeTarget(Vector2 pos) => ((int)(pos.X / 20), (int)(pos.Y / 20));

    private static readonly Dictionary<string, int> PilotBasePriority = new()
    {
        ["GoldenLantern"] = 96,
        ["CurrencyTreasureChestOpulent"] = 90, // annul/divine — sempre prioridade alta
        ["Unrevealed"] = 80,
        ["CurrencyTreasureChest"] = 70,
        ["CurrencyGemcuttersChest"] = 60,
        ["BottledItemChest"] = 70,
        ["ScarabChest"] = 65,
        ["StackedDecksChest"] = 65,
        ["AllflameEmbersChest"] = 55,
        ["LanternReplenishEncounter"] = 55,
        ["CursedDucatDrop"] = 45,
        ["IzaroObject"] = 40,
        // Gold pile e Maps chest: praticamente sem lucro (feedback do Bruno 31/07) —
        // só valem quando estão no caminho, nunca como desvio.
        ["MapsChest"] = 20,
        ["GoldTreasureChest"] = 10,
        ["AltarCrab"] = 35,
        ["AltarOctopus"] = 35,
        ["ClamTreasureChest"] = 30,
        ["UniqueWeaponChest"] = 30,
        ["UniqueArmourChest"] = 30,
        ["Strongbox"] = 55, // box real spawnada (entidade StrongBoxes, não marker deepwater)
        // Tiles de peças do plano (objetivos sintéticos) — fora da estratégia dona valem pouco.
        ["StarfishTile"] = 40,
        ["PantheonTile"] = 40,
        ["PillarTile"] = 40,
        ["BoxTile"] = 45,
        ["FeederTile"] = 40,
        ["DivinePieceTile"] = 40,
        ["WispTile"] = 40,
    };

    private static readonly Dictionary<string, Dictionary<string, int>> PilotStrategyOverrides = new()
    {
        ["Speedrun"] = new Dictionary<string, int>
        {
            ["CurrencyTreasureChestOpulent"] = 105,
            ["Strongbox"] = 100, // box REAL vence o tile sintético — abrir o que está vivo
            ["CurrencyTreasureChest"] = 100,
            ["BoxTile"] = 95, // tiles dos charts de box: é lá que as boxes spawnam
            ["BottledItemChest"] = 95,
            ["ScarabChest"] = 90,
            ["StackedDecksChest"] = 90,
            ["Unrevealed"] = 85,
            ["MapsChest"] = 25,
            ["GoldTreasureChest"] = 8,
            ["ClamTreasureChest"] = 15,
        },
        ["Meatfish"] = new Dictionary<string, int>
        {
            ["GoldenLantern"] = 110,
            ["LanternReplenishEncounter"] = 100,
            // Starfish/Pantheon/Pillar SEM decay: matar os giga-rares depois dos
            // stacks é até melhor (rarity aplica no kill) — só as lanterns têm pressa.
            ["StarfishTile"] = 80,
            ["PantheonTile"] = 75,
            ["AllflameEmbersChest"] = 70,
            ["PillarTile"] = 60,
        },
        ["DivineBorder"] = new Dictionary<string, int>
        {
            ["DivinePieceTile"] = 105, // o tile do Divine É o farm inteiro
            ["Strongbox"] = 90,
            ["FeederTile"] = 85,
            ["GoldenLantern"] = 70,
            ["Unrevealed"] = 50,
        },
        ["DivineBoxes"] = new Dictionary<string, int>
        {
            ["DivinePieceTile"] = 105,
            ["Strongbox"] = 95, // as boxes SÃO os 7 div/cada — abrir no tile do Divine
            ["FeederTile"] = 90,
            ["GoldenLantern"] = 70,
            ["Unrevealed"] = 50,
        },
        ["Ethereal"] = new Dictionary<string, int>
        {
            ["GoldenLantern"] = 100,
            ["LanternReplenishEncounter"] = 90,
            ["WispTile"] = 90,
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

                    // Peça-chave do plano nesta célula: objetivo sintético com a
                    // prioridade do kind (GL puxa CEDO, com decay; some quando a
                    // célula é visitada — aí as entidades reais assumem). Cada
                    // estratégia persegue as suas peças.
                    if (_plannedPieceMods?[r, c] is { } placedMods)
                    {
                        var pieceTable = StrategyPieceObjectives.GetValueOrDefault(strategyName, DefaultPieceObjectives);
                        foreach (var (modKey, kind) in pieceTable)
                        {
                            if (placedMods.Any(m => m.Contains(modKey, StringComparison.OrdinalIgnoreCase)))
                            {
                                objectives.Add(($"{kind} ({r},{c})", target, EffectivePriority(kind)));
                                kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
                            }
                        }
                    }
                }
            }
        }

        // Strongboxes REAIS carregadas (spawnadas pelos box charts): o marker cache
        // só vê chests deepwater — sem isso o Pilot mandava ao BoxTile distante com
        // uma box viva do lado. Prioridade acima do tile sintético no Speedrun.
        try
        {
            foreach (var chest in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Chest])
            {
                if (chest is not { IsValid: true } ||
                    !chest.Path.Contains("StrongBox", StringComparison.OrdinalIgnoreCase) ||
                    chest.GetComponent<Chest>()?.IsOpened == true)
                {
                    continue;
                }

                kindCounts["Strongbox"] = kindCounts.GetValueOrDefault("Strongbox") + 1;
                objectives.Add(("Strongbox", chest.GridPosNum, EffectivePriority("Strongbox")));
            }
        }
        catch
        {
            // lista de chests ilegível em transição
        }

        var lanternsLeft = kindCounts.GetValueOrDefault("LanternReplenishEncounter");
        var meatfishKillPhase = strategyName == "Meatfish" && lanternsLeft == 0;
        var divineStrategy = strategyName is "DivineBorder" or "DivineBoxes";
        if (divineStrategy || meatfishKillPhase)
        {
            try
            {
                foreach (var monster in Handler?.Monsters ?? [])
                {
                    if (monster is not { IsValid: true, IsAlive: true } ||
                        monster.Rarity is not (MonsterRarity.Rare or MonsterRarity.Unique))
                    {
                        continue;
                    }

                    // Divine: SÓ os rares dentro da célula do border Divine pagam
                    // (1 div cada) — rare do outro lado do mapa é rare comum.
                    var prio = 75;
                    if (divineStrategy)
                    {
                        prio = _plannedDivineCell < 0 ? 95
                            : GridCellIndex(monster.GridPosNum) == _plannedDivineCell ? 115 : 60;
                    }

                    objectives.Add(($"{monster.Rarity}", monster.GridPosNum, prio));
                }
            }
            catch
            {
                // lista de monstros pode falhar em transição de área
            }
        }

        // Alvos que falharam rota 3x ficam na blacklist por 60s — objetivo em chart
        // inalcançável (outro componente do terreno) prendia a seta numa reta eterna
        // mesmo havendo objetivos bons ao lado. E prioridade agora paga pedágio de
        // distância (-3 por 100 unidades): chest decente perto > jackpot do outro
        // lado do mapa.
        var utcNow = DateTime.UtcNow;
        foreach (var expired in _pilotUnreachable.Where(kv => kv.Value <= utcNow).Select(kv => kv.Key).ToList())
        {
            _pilotUnreachable.Remove(expired);
        }

        // Alvo em bolsão de terreno DESCONEXO do jogador nem entra na seleção
        // (mata a reta-para-o-inalcançável sem esperar os 6s do blacklist).
        // Componente 0 = desconhecido: permissivo, o blacklist cobre.
        var playerComponent = ComponentAt(_playerGridPos);
        var reachable = objectives.Where(o =>
            {
                if (_pilotUnreachable.ContainsKey(QuantizeTarget(o.Pos)))
                {
                    return false;
                }

                if (playerComponent == 0)
                {
                    return true;
                }

                var targetComponent = ComponentAt(o.Pos);
                if (targetComponent == 0)
                {
                    // Componente desconhecido: perto tudo bem (decoração ao lado,
                    // anda-se até encostar); LONGE é quase sempre void intransponível.
                    return Vector2.Distance(_playerGridPos, o.Pos) < 200;
                }

                return targetComponent == playerComponent;
            })
            .ToList();
        if (reachable.Count == 0)
        {
            reachable = objectives; // tudo filtrado: melhor apontar algo que nada
        }

        var distPenalty = StrategyDistancePenalty.GetValueOrDefault(strategyName, 0.03);
        var next = reachable
            .OrderByDescending(o => o.Prio - Vector2.Distance(_playerGridPos, o.Pos) * distPenalty)
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
                if (!targetChanged)
                {
                    // Retry do MESMO alvo = a tentativa anterior não produziu rota.
                    var failedTarget = QuantizeTarget(obj.Pos);
                    var fails = _pilotRouteFailCounts.GetValueOrDefault(failedTarget) + 1;
                    if (fails >= 2)
                    {
                        _pilotUnreachable[failedTarget] = DateTime.UtcNow.AddSeconds(90);
                        _pilotRouteFailCounts.Remove(failedTarget);
                    }
                    else
                    {
                        _pilotRouteFailCounts[failedTarget] = fails;
                    }
                }

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
                _pilotRouteFailCounts.Remove(QuantizeTarget(obj.Pos));
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
                // Sem rota (Radar calculando ou alvo sem caminho): toco curto de
                // DIREÇÃO no mundo — a reta completa parecia um traçado válido
                // atravessando terreno. A linha inteira fica só no mapa grande.
                var playerScreen = GetWorldScreenPosition(_playerGridPos);
                var toTarget = obj.Pos - _playerGridPos;
                var dist = toTarget.Length();
                var stubEnd = dist > 150f ? _playerGridPos + toTarget * (150f / dist) : obj.Pos;
                var stubScreen = GetWorldScreenPosition(stubEnd);
                if (IsRoughlyOnScreen(playerScreen) || IsRoughlyOnScreen(stubScreen))
                {
                    Graphics.DrawLine(playerScreen, stubScreen, 3, color);
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
            "Speedrun" => "Tiles de BOX primeiro, juice com Alch/Scour/Ex, abra tudo. Extraia no limite.",
            "Meatfish" when !meatfishKillPhase => $"FASE 1: colete os lanterns ({lanternsLeft} restantes)",
            "Meatfish" => "FASE 2: full clear de rares/uniques (Starfish/Pantheon). Extraia por ultimo.",
            "DivineBorder" or "DivineBoxes" when _plannedDivineCell >= 0 =>
                $"Tile do Divine = ({_plannedDivineCell / 3},{_plannedDivineCell % 3}): rares LA valem 1 div cada. Feeders adjacentes.",
            "DivineBorder" or "DivineBoxes" => "Full-clear de rares na regiao do border Divine.",
            "Ethereal" => "Wisps nas laterais cedo, lanterns nos cantos, mate os packs magicos.",
            "AlcGo" => "Queima local: lanterns, clique tudo por perto, sem desvios. Saia rapido.",
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
