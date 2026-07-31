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

    // Pedágio EXTRA por unidade de DARK WATER (distância à borda da rede de
    // bolhas): dentro da rede andar é de graça, fora custa lanterns + timer de
    // afogamento — o custo real de alcançar algo é a distância à REDE, não ao
    // jogador. Zero para objetivos já cobertos, então o comportamento local não
    // muda; só a travessia paga. AlcGo = queima local, quase nunca atravessa
    // (expansão vai para o alvo mais perto da borda); Divine = o tile âncora
    // vale a travessia.
    private static readonly Dictionary<string, double> StrategyDarkWaterToll = new()
    {
        ["AlcGo"] = 0.12,
        ["Speedrun"] = 0.08,
        ["Meatfish"] = 0.05,
        ["DivineBorder"] = 0.03,
        ["DivineBoxes"] = 0.03,
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

    // Histerese de alvo: os pedágios mudam continuamente conforme o jogador anda
    // e a rede de bolhas cresce — sem margem, dois alvos com scores parecidos
    // trocam de posto por frame e a rota/EXIT pulam de destino (relato 31/07).
    private Vector2? _pilotStickyPos;
    private const double PilotTargetSwitchMargin = 8;

    // Histerese TEMPORAL (telemetria 31/07: 12 de 28 trocas com held=0s — alvo
    // próximo aparece/some ao revelar chamas e a seta pingue-pongueia com o alvo
    // distante). Roubo de seta só após o alvo atual segurar o mínimo; alvo que
    // SOME entrega na hora. EXTRACT precisa do piso estável por 3s (gap de load
    // de entidades disparava EXTRACT de 1 frame no meio da run).
    private DateTime _pilotTargetSince = DateTime.MinValue;
    private DateTime _pilotFloorSince = DateTime.MinValue;
    private const double PilotMinHoldSeconds = 1.5;
    private const double PilotExtractDebounceSeconds = 3.0;

    // Coleta com prio abaixo disso é lixo (gold 8-25, clam, unique-gear 30) e
    // não segura a expansão; acima (ducats 45+, spirits 50, chests 55+), vale
    // varrer antes de expandir.
    private const int PilotSweepMinPriority = 40;

    // Alcance da varredura FORA da rede: coleta que preste dentro deste raio
    // entra na varredura mesmo no escuro (marker lembrado de baú a ~280un
    // perdia para o tile jackpot a 620un — o tile não paga dark water).
    private const float PilotSweepNearRadius = 400f;

    private Vector2? _extractionPos;
    private DateTime _extractionRead = DateTime.MinValue;

    /// <summary>Ponto de extração da voyage (entidade SummonExtractionObject),
    /// para a seta de fim de run. Cache de 2s — varre a lista inteira de entidades.</summary>
    private Vector2? FindExtractionPoint()
    {
        if ((DateTime.UtcNow - _extractionRead).TotalSeconds < 2)
        {
            return _extractionPos;
        }

        _extractionRead = DateTime.UtcNow;
        _extractionPos = null;
        try
        {
            foreach (var e in GameController.EntityListWrapper.OnlyValidEntities)
            {
                if (e?.Path?.Contains("ExtractionObject", StringComparison.OrdinalIgnoreCase) == true)
                {
                    _extractionPos = e.GridPosNum;
                    break;
                }
            }
        }
        catch
        {
            // lista ilegível em transição
        }

        return _extractionPos;
    }

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
        // 9 abertos numa AlcGo de 31/07 só por acaso de caminho (prio default 10
        // = invisível): espíritos possuem rares -> mais loot. Prio modesta para
        // entrar na rota quando perto; medir o retorno nas próximas runs.
        ["TormentedSpiritEncounter"] = 50,
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

        // Sem gate por caches de entidades: após um Reload Plugins os caches nascem
        // vazios (entidades já carregadas não re-disparam EntityAdded) e o painel
        // sumia no início da voyage — e ele é útil mesmo sem markers (fase, tiles
        // do plano, strongboxes). O call site já garante Handler != null.
        var strategyName = _activeStrategy?.Name ?? "Base";

        var inField = IsPlayerInField(_playerGridPos);

        if (!inField)
        {
            if (ImGui.Begin("Voyage Pilot", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextColored(Color.Gray.ToImguiVec4(),
                    $"{strategyName} - outside the bubbles (on the boat?). No active objectives.");
            }

            ImGui.End();
            return;
        }

        var overrides = PilotStrategyOverrides.GetValueOrDefault(strategyName);
        var elapsed = DateTime.UtcNow - _statsAreaStart;
        // Decaimento dos objetivos de buff: valor cheio no início, piso de 35% aos 13+ min.
        var buffDecay = Math.Max(0.35, 1.0 - elapsed.TotalMinutes / 20.0);

        // Rede de bolhas viva (posição + raio) para o custo de dark water. O ×1.5
        // é a mesma tolerância do in-field: objetivo "quase dentro" não paga
        // travessia. Sem bolhas legíveis, custo zero — vira o guloso antigo.
        List<(Vector2 Center, float Radius)> netBubbles = [];
        try
        {
            netBubbles = Bubbles.Select(b => (new Vector2(b.Position.X, b.Position.Y), b.Radius)).ToList();
        }
        catch
        {
            // bolhas ilegíveis em transição
        }

        float NetworkDist(Vector2 pos) => netBubbles.Count == 0
            ? 0
            : Math.Max(0, netBubbles.Min(b => Vector2.Distance(pos, b.Center) - b.Radius * 1.5f));

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
        var tileObjectivePositions = new HashSet<(int, int)>();
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
                    tileObjectivePositions.Add(QuantizeTarget(target));
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
            // Tudo filtrado por componente: re-inclui os de componente
            // desconhecido, mas NUNCA os blacklistados — o "apontar algo que
            // nada" ressuscitava o alvo sem rota e a reta voltava para sempre
            // (31/07). Se sobrar nada, ficar sem seta é o correto.
            reachable = objectives
                .Where(o => !_pilotUnreachable.ContainsKey(QuantizeTarget(o.Pos)))
                .ToList();
        }

        var distPenalty = StrategyDistancePenalty.GetValueOrDefault(strategyName, 0.03);
        var darkToll = StrategyDarkWaterToll.GetValueOrDefault(strategyName, 0.08);

        // Objetivos de EXPANSÃO (SÓ tiles do plano) não pagam o pedágio de dark
        // water: a travessia até eles É a exploração — as lanterns plantadas no
        // caminho viram rede. Unrevealed paga pedágio normal: a isenção fazia um
        // chart do outro lado do mapa (prio 80, travessia grátis) vencer com
        // score positivo e puxar reta eterna (31/07) — revelar chart perto da
        // rede continua valendo; do outro lado do mapa, não.
        bool IsExpansion((string Label, Vector2 Pos, int Prio) o) =>
            tileObjectivePositions.Contains(QuantizeTarget(o.Pos));

        double Score((string Label, Vector2 Pos, int Prio) o) =>
            o.Prio - Vector2.Distance(_playerGridPos, o.Pos) * distPenalty -
            (IsExpansion(o) ? 0 : NetworkDist(o.Pos) * darkToll);

        // Doutrina "catar antes de expandir" (Bruno, 31/07): enquanto houver
        // coleta que preste DENTRO da rede iluminada — OU logo ali fora, ao
        // alcance — ela fica com a seta até ser coletada; expansão (tiles do
        // plano/unrevealed) só depois. O raio de proximidade existe porque a
        // expansão não paga pedágio de dark water e o tile jackpot (prio ~100)
        // vencia baú de currency a 280un no escuro (relato 31/07): se vamos
        // gastar lantern atravessando, o que está no caminho vem primeiro.
        // Sobras muito distantes ainda cedem via piso (score <= 0).
        var sweep = reachable
            .Where(o => !IsExpansion(o) && o.Prio >= PilotSweepMinPriority &&
                        (NetworkDist(o.Pos) <= 0 ||
                         Vector2.Distance(_playerGridPos, o.Pos) <= PilotSweepNearRadius))
            .ToList();
        var pool = sweep.Count > 0 ? sweep : reachable;

        var next = pool
            .OrderByDescending(Score)
            .Cast<(string Label, Vector2 Pos, int Prio)?>()
            .FirstOrDefault();

        // LIXO nunca pré-empta exploração (telemetria 31/07): no fim da run um
        // MapsChest (prio 20) a 182un venceu o tile (0,2) x2.6 a 777un por DOIS
        // pontos, e a célula inteira ficou por explorar. Coleta abaixo do corte
        // da varredura só leva a seta quando não há expansão que se pague.
        if (next is { } lowValue && !IsExpansion(lowValue) && lowValue.Prio < PilotSweepMinPriority)
        {
            var expansionAlt = pool
                .Where(IsExpansion)
                .OrderByDescending(Score)
                .Cast<(string Label, Vector2 Pos, int Prio)?>()
                .FirstOrDefault();
            if (expansionAlt is { } alt && Score(alt) > 0)
            {
                next = alt;
            }
        }

        // Alvo atual só cede para um desafiante com folga de margem E depois de
        // segurar o tempo mínimo — ou quando some do pool (coletado/blacklist/
        // célula visitada/troca de doutrina), aí a entrega é imediata.
        if (next is { } cand && _pilotStickyPos is { } sticky &&
            Vector2.Distance(cand.Pos, sticky) >= 40)
        {
            var current = pool
                .Where(o => Vector2.Distance(o.Pos, sticky) < 40)
                .Cast<(string Label, Vector2 Pos, int Prio)?>()
                .FirstOrDefault();
            if (current is { } cur &&
                (Score(cand) < Score(cur) + PilotTargetSwitchMargin ||
                 (DateTime.UtcNow - _pilotTargetSince).TotalSeconds < PilotMinHoldSeconds))
            {
                next = cur;
            }
        }

        // Piso de valor: se nem o MELHOR alvo sobrevive aos pedágios (score <= 0),
        // andar custa mais que o prêmio — ex.: gold chest prio 8 a 1.237un
        // (score -48) puxando reta pelo mapa inteiro por ser o último objetivo
        // vivo (31/07). MAS o piso é condição de FIM de run: enquanto houver
        // exploração viva (tile não visitado / unrevealed), guiar para a melhor
        // expansão mesmo negativa — EXTRACT falso aos 00:50 com 59 lanterns na
        // mão (relato 31/07) era o piso disparando no começo da run.
        var nothingWorthTheWalk = false;
        if (next is { } best && Score(best) <= 0)
        {
            // Expansão busca em OBJECTIVES (não em reachable): tile do plano é
            // alcançável por construção — blacklist de tile significa rota LENTA,
            // não tile impossível, e com lanterns livres insistir no stub vence
            // extrair com células virgens (o EXTRACT falso de 31/07 nasceu de 4
            // tiles blacklistados em sequência esvaziando o pool de expansão).
            var bestExpansion = objectives
                .Where(o => IsExpansion(o))
                .OrderByDescending(Score)
                .Cast<(string Label, Vector2 Pos, int Prio)?>()
                .FirstOrDefault();
            if (bestExpansion != null)
            {
                next = bestExpansion;
                _pilotFloorSince = DateTime.MinValue;
            }
            else if (_pilotFloorSince == DateTime.MinValue)
            {
                // Debounce: 1 frame de lista vazia (gap de load) não é fim de
                // run — segura o melhor alvo atual até o piso ficar estável.
                _pilotFloorSince = DateTime.UtcNow;
            }
            else if ((DateTime.UtcNow - _pilotFloorSince).TotalSeconds >= PilotExtractDebounceSeconds)
            {
                nothingWorthTheWalk = true;
                // Fim de run de verdade: em vez de seta apagada, apontar para a
                // EXTRAÇÃO (medido 31/07: rede varrida, extração a 88un e a seta
                // antiga cruzando o mapa atrás de chart não revelado).
                next = FindExtractionPoint() is { } extraction
                    ? ("EXTRACT", extraction, 200)
                    : null;
            }
        }
        else
        {
            _pilotFloorSince = DateTime.MinValue;
        }

        if (next is { } selected &&
            (_pilotStickyPos is not { } prevSticky || Vector2.Distance(selected.Pos, prevSticky) >= 40))
        {
            _pilotTargetSince = DateTime.UtcNow; // identidade nova: reinicia o hold
        }

        _pilotStickyPos = next?.Pos;

        // Telemetria: cada troca de alvo vira evento no zone_stats, classificada
        // como reached/gone/abandoned — "abandoned" é a mudança de ideia que
        // queremos medir e reduzir.
        ZoneStatsPilotTargetTick(next, objectives, _playerGridPos);

        // Seta/rota para o próximo objetivo (mundo + mapa). Com o Radar instalado, a
        // rota segue o terreno andável (Radar.LookForRoute via PluginBridge).
        if (settings.ShowObjectiveArrow && next is { } obj)
        {
            var color = settings.ObjectiveColor.Value;
            var targetScreen = GetWorldScreenPosition(obj.Pos);

            // Re-pede a rota quando o alvo muda OU quando a última tentativa falhou
            // (alvo inandável, pathfinding ainda não pronto) — senão fica preso na reta.
            // PACIÊNCIA por distância (telemetria 31/07): rota de 1.000un+ demora
            // mais que 2s para calcular; contar "ainda calculando" como falha
            // blacklistou os 4 tiles restantes em sequência e disparou EXTRACT
            // com mapa por explorar. Falha só conta após 4s (7s para alvos longe).
            var targetChanged = Vector2.Distance(_pilotRouteTarget, obj.Pos) >= 40;
            var sinceRequest = (DateTime.UtcNow - _lastRouteRequest).TotalSeconds;
            var routePatience = Vector2.Distance(_playerGridPos, obj.Pos) > 800 ? 7.0 : 4.0;
            if (settings.UseRadarRoute.Value &&
                ((targetChanged && sinceRequest >= 2) ||
                 (!targetChanged && _pilotRoute == null && sinceRequest >= routePatience)))
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
            // Com o mapa grande aberto (overlay transparente em tela cheia), a linha
            // do MAPA cruza a tela e parece uma reta no mundo — com este toggle, o
            // desenho fica só no mapa enquanto ele estiver aberto.
            var worldDrawing = !(settings.RouteOnlyOnLargeMap.Value && _largeMapOpen);
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
                    if (worldDrawing &&
                        (Vector2.Distance(_playerGridPos, a) <= 150 || Vector2.Distance(_playerGridPos, b) <= 150))
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
                if (worldDrawing)
                {
                    var playerScreen = GetWorldScreenPosition(_playerGridPos);
                    var toTarget = obj.Pos - _playerGridPos;
                    var dist = toTarget.Length();
                    var stubEnd = dist > 150f ? _playerGridPos + toTarget * (150f / dist) : obj.Pos;
                    var stubScreen = GetWorldScreenPosition(stubEnd);
                    if (IsRoughlyOnScreen(playerScreen) || IsRoughlyOnScreen(stubScreen))
                    {
                        Graphics.DrawLine(playerScreen, stubScreen, 3, color);
                    }
                }

                if (_largeMapOpen)
                {
                    Graphics.DrawLine(Graphics.GridToMap(_playerGridPos, _playerGridPos),
                        Graphics.GridToMap(obj.Pos, _playerGridPos), 2, color);
                }
            }

            // Alvo FORA da rede: marca o ponto de SAÍDA da rede. Com rota válida,
            // o EXIT é onde a ROTA cruza a fronteira das bolhas — andável e no
            // caminho por construção, e avança suave conforme a rede cresce. A
            // borda geométrica (fallback sem rota) caía em terreno intransponível
            // quando a bolha vazava pela borda do mapa, e flapava entre bolhas
            // quase equidistantes (relatos 31/07) — agora com snap para andável.
            if (netBubbles.Count > 0 && NetworkDist(obj.Pos) > 0)
            {
                Vector2? exitPos = null;
                if (routeValid)
                {
                    var startIsPlayer =
                        Vector2.Distance(new Vector2(route[0].X, route[0].Y), _playerGridPos) <=
                        Vector2.Distance(new Vector2(route[^1].X, route[^1].Y), _playerGridPos);
                    Vector2? lastInside = null;
                    for (var i = 0; i < route.Count; i++)
                    {
                        var idx = startIsPlayer ? i : route.Count - 1 - i;
                        var p = new Vector2(route[idx].X, route[idx].Y);
                        if (NetworkDist(p) <= 0)
                        {
                            lastInside = p;
                        }
                        else if (lastInside != null)
                        {
                            exitPos = lastInside; // primeira travessia luz -> escuro
                            break;
                        }
                    }
                }

                if (exitPos == null)
                {
                    var nearest = netBubbles.MinBy(b => Vector2.Distance(obj.Pos, b.Center) - b.Radius);
                    var dir = obj.Pos - nearest.Center;
                    if (dir.LengthSquared() > 1)
                    {
                        var edge = nearest.Center + dir * (nearest.Radius / dir.Length());
                        exitPos = SnapToWalkable(edge) ?? edge;
                    }
                }

                if (exitPos is { } exit)
                {
                    if (_largeMapOpen)
                    {
                        Graphics.DrawTextWithBackground("EXIT",
                            Graphics.GridToMap(exit, _playerGridPos), color, Color.Black);
                    }

                    if (worldDrawing)
                    {
                        var exitScreen = GetWorldScreenPosition(exit);
                        if (IsRoughlyOnScreen(exitScreen))
                        {
                            Graphics.DrawTextWithBackground("EXIT",
                                exitScreen + new Vector2(0, -18), color, Color.Black);
                        }
                    }
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
            (extractLimit > 0 ? $" / {extractLimit}min{(overTime ? "  EXTRACT!" : "")}" : ""));

        var behavior = strategyName switch
        {
            "Speedrun" => "BOX tiles first, juice with Alch/Scour/Ex, open everything. Extract at the limit.",
            "Meatfish" when !meatfishKillPhase => $"PHASE 1: collect the lanterns ({lanternsLeft} left)",
            "Meatfish" => "PHASE 2: full clear rares/uniques (Starfish/Pantheon). Extract last.",
            "DivineBorder" or "DivineBoxes" when _plannedDivineCell >= 0 =>
                $"Divine tile = ({_plannedDivineCell / 3},{_plannedDivineCell % 3}): rares THERE drop 1 div each. Feeders adjacent.",
            "DivineBorder" or "DivineBoxes" => "Full clear rares around the Divine border.",
            "Ethereal" => "Wisps on the sides early, lanterns in the corners, kill the magic packs.",
            "AlcGo" => "Local burn: lanterns, click everything nearby, no detours. Leave fast.",
            _ => "Follow the markers; open whatever has value.",
        };
        ImGui.TextUnformatted(behavior);

        var currentCell = GridCellIndex(_playerGridPos);
        if (currentCell >= 0)
        {
            var cellText = $"Cell: ({currentCell / 3},{currentCell % 3})";
            if (_plannedMults != null)
            {
                var cellMult = _plannedMults[currentCell / 3, currentCell % 3];
                ImGui.TextUnformatted(cellText + $"  mult x{cellMult:F2}");

                // Dica de permanência: o mult roteia ATÉ o tile, mas nada dizia
                // "fique" — na AlcGo de 31/07 a célula x8.75 (2º melhor tile)
                // recebeu os menores 18s da run, dinheiro deixado na mesa.
                var better = 0;
                for (var r = 0; r < 3; r++)
                {
                    for (var c = 0; c < 3; c++)
                    {
                        if (_plannedMults[r, c] > cellMult)
                        {
                            better++;
                        }
                    }
                }

                if (better <= 1 && cellMult >= 1.5)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Color.LightGreen.ToImguiVec4(), "TOP CELL - farm it dry");
                }
                else if (cellMult <= 1.2)
                {
                    ImGui.SameLine();
                    ImGui.TextColored(Color.Gray.ToImguiVec4(), "low mult - grab & go");
                }
            }
            else
            {
                ImGui.TextUnformatted(cellText);
            }
        }

        var maxLanterns = Handler?.MaxLanternCount ?? 0;
        if (maxLanterns > 0 && PlacedLanternCount >= maxLanterns * 0.9)
        {
            ImGui.TextColored(Color.OrangeRed.ToImguiVec4(),
                "Lanterns almost done - plan the RETURN (they extinguish in reverse order)");
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
            ImGui.TextUnformatted($"Remaining: {summary}");
        }

        if (next is { } n)
        {
            var nextLine = $"Next: {n.Label} ({Vector2.Distance(_playerGridPos, n.Pos):F0})";
            var netDist = NetworkDist(n.Pos);
            if (netDist > 0 && netBubbles.Count > 0)
            {
                // Orçamento de travessia: cada lantern plantada na borda estende o
                // alcance em ~1.5×raio (bolha nova tangente à rede, com a mesma
                // tolerância do in-field) — estimativa, não contrato.
                var avgRadius = netBubbles.Average(b => b.Radius);
                var lanternCost = avgRadius > 0 ? (int)Math.Ceiling(netDist / (avgRadius * 1.5f)) : 0;
                nextLine += lanternCost > 0
                    ? $"  - outside network: ~{lanternCost} lantern{(lanternCost > 1 ? "s" : "")}"
                    : "  - outside network";
            }

            ImGui.TextColored(settings.ObjectiveColor.Value.ToImguiVec4(), nextLine);
        }

        if (nothingWorthTheWalk)
        {
            ImGui.TextColored(Color.OrangeRed.ToImguiVec4(),
                "Map swept, nothing worth the walk - EXTRACT!");
        }

        ImGui.End();
    }
}
