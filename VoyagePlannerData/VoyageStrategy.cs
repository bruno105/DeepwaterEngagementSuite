using System;
using System.Collections.Generic;
using System.Linq;

namespace DeepwaterEngagementSuite.VoyagePlannerData;

public record StrategyBoost(string[] Keys, double Mult);

/// <summary>Peça obrigatória: Count charts cujo pool contenha algum mod com uma das Keys.</summary>
public record PieceRequirement(string Label, int Count, string[] Keys);

/// <summary>
/// Estratégia de farm da planilha da comunidade ("Allflame stuff"). Cada estratégia
/// re-pondera mods de chart e border mods por substring do Id. O score do board para
/// uma estratégia considera o POOL COMPLETO de charts (média dos top-9 valores
/// boostados) × soma dos multiplicadores efetivos das bordas boostadas.
/// </summary>
public record VoyageStrategy(
    string Name,
    StrategyBoost[] ChartBoosts,
    StrategyBoost[] BorderBoosts,
    string[] RequiredBorderKeys,
    PieceRequirement[] PieceRequirements = null,
    string LayoutHint = null)
{
    public const double MissingRequirementPenalty = 0.2;

    // Pesos calibrados contra o bundle do solver one-more-map (2026-07-30), escala
    // deles 0-10 mapeada em multiplicadores: 10→2.5, 8-9→2.0-2.2, 6-7→1.8-2.0,
    // 3-5→1.3-1.7. Regras POSICIONAIS (centro/laterais/pins por border) e sistema de
    // RESERVA entre estratégias ficam para a integração upstream — por ora vão no hint.
    public static readonly VoyageStrategy[] DocStrategies =
    [
        // milky-speedrun. Regex: "bottle|divine|oper". Site: opbox 10 > divbox/msg 7;
        // self:quant 8, voyage:quant 5, sulph 3/3; borders quantconn 6 > divine 4 >
        // exalt/ancient 3. SEM boost de Strongboxes/Arcanist: são peças RESERVADAS
        // para as estratégias de Divine (o speedrun queima só o que ninguém quer).
        new("Speedrun",
            [
                new StrategyBoost(["OperativeBox"], 2.5),
                new StrategyBoost(["DivinerBox", "LostMessage"], 2.0),
                new StrategyBoost(["Quantity"], 1.8),
                new StrategyBoost(["Resource"], 1.3),
            ],
            [
                new StrategyBoost(["QuantityPerConnection"], 2.0),
                new StrategyBoost(["RareMonsterDivine"], 1.7),
                new StrategyBoost(["RareMonsterExalted", "RareMonsterAncient", "GiantOctopus"], 1.5),
            ],
            [],
            [
                new PieceRequirement("1x Operative/Diviner/Bottle", 1, ["OperativeBox", "DivinerBox", "LostMessage"]),
            ],
            "Milky: UM Operative no CENTRO (fallback Diviner/Bottle) | quant 110%+ ANTES de rodar | maiores quants nas 4 laterais | Alch/Scour/Ex p/ juicar boxes | border Filthscrabble (polvo ~4k sulphur): chart de mais sulphur NELE"),
        // milky-meatfish. Regex: "cannot|poss|lantern|pantheon". Site: star/pantheon/
        // lantern/possess 10; fracture 8, voyage:rare 8, adjacent:rare 6; border:rare 9;
        // self:quant 4, rarity 3. Composição: 2× Starfish, 1× Pantheon (ou 4k Wisps),
        // 2× Sea Pillars, 2× Golden Lanterns, 1× Possessed, 1× No-Equipment (fallback
        // Rares Fracture). All-or-nothing: sem as peças, Speedrun enquanto isso.
        new("Meatfish",
            [
                new StrategyBoost(["NoEquipmentDrops", "MonstersPossessed", "Pantheon", "GoldenLanterns", "Starfish", "Wisps2"], 2.5),
                new StrategyBoost(["RareFracture", "IncreasedRareMonsters"], 1.8),
                new StrategyBoost(["Quantity", "Rarity"], 1.3),
            ],
            [
                new StrategyBoost(["IncreasedRareMonsters"], 2.0),
                new StrategyBoost(["GoldenLanterns"], 1.5),
            ],
            [],
            [
                new PieceRequirement("2x Starfish", 2, ["Starfish"]),
                new PieceRequirement("1x Pantheon/4k Wisps", 1, ["Pantheon", "Wisps2"]),
                new PieceRequirement("2x Sea Pillars", 2, ["Room:Sea Pillars"]),
                new PieceRequirement("2x Golden Lanterns", 2, ["GoldenLanterns"]),
                new PieceRequirement("1x Possessed", 1, ["MonstersPossessed"]),
                new PieceRequirement("1x No-Equipment/Fracture", 1, ["NoEquipmentDrops", "RareFracture"]),
            ],
            "Layout Milky: Starfish SÓ topo/baixo-meio | Pantheon SÓ dir-meio | GL centro | Pillars cantos | coletar TODOS os lanterns (~280% quant, 840 rarity)"),
        // divine-border-rares (Milky). Regex: "rare monsters in all voy|strongbox".
        // Site: rare 10 (adj+voy+border), star 8, box 8 (genérico — specialty boxes são
        // do cutedog), possess/fracture 6, border:divine 10. Pillar PINADO no tile do
        // Divine (+100); feeders = "+5 Strongboxes" (+35) e Starfish (+15) adjacentes.
        new("DivineBorder",
            [
                new StrategyBoost(["IncreasedRareMonsters", "Strongboxes"], 2.5),
                new StrategyBoost(["Starfish"], 2.0),
                new StrategyBoost(["MonstersPossessed", "RareFracture"], 1.5),
            ],
            [
                new StrategyBoost(["RareMonsterDivine"], 3.0),
                new StrategyBoost(["IncreasedRareMonsters", "RareMonstersPerConnection"], 2.0),
            ],
            ["RareMonsterDivine"],
            [
                new PieceRequirement("1x Sea-Pillar", 1, ["Room:Sea Pillars"]),
                new PieceRequirement("3x Starfish/Strongbox", 3, ["Starfish", "Strongboxes"]),
                new PieceRequirement("5x Increased Rares", 5, ["IncreasedRareMonsters"]),
            ],
            "Milky: Pillar NO tile do Divine | feeders '+5 Strongboxes' adjacentes (rolar boxes p/ Stream of Monsters +4 / of Rarity +3 = 7 div/box; um +5 ≈ 35 div) | Starfish = feeder reserva"),
        // cutedog-divine-boxes. Regex de compra: 120%+ quant. Site: voyage:rare 10,
        // adjacent:rare 8, border:rare/divine 10, box 9, specialty boxes 8, self:pack 6.
        // Pelagic Abyss pack-size alto no tile do Divine (+80, packsize per 8);
        // QUALQUER strongbox adjacente serve de feeder (+25).
        new("DivineBoxes",
            [
                new StrategyBoost(["IncreasedRareMonsters"], 2.5),
                new StrategyBoost(["Strongboxes", "DivinerBox", "ArcanistBox", "OperativeBox"], 2.2),
                new StrategyBoost(["PackSize"], 1.8),
            ],
            [
                new StrategyBoost(["RareMonsterDivine"], 3.0),
                new StrategyBoost(["IncreasedRareMonsters", "RareMonstersPerConnection"], 2.0),
            ],
            ["RareMonsterDivine"],
            [
                new PieceRequirement("1x Pelagic Abyss (pack alto)", 1, ["Room:Pelagic Abyss"]),
                new PieceRequirement("3x Strongbox (qualquer)", 3, ["Strongboxes", "DivinerBox", "ArcanistBox", "OperativeBox"]),
                new PieceRequirement("5x Increased Rares (voyage)", 5, ["VoyageIncreasedRareMonsters"]),
            ],
            "cutedog: Pelagic pack-size ALTO no tile do Divine | 3× boxes (qualquer tipo) adjacentes | rolar boxes: '3 additional Rares'=3 div + 'Stream of Monsters'=4 (ambos=7) | comprar charts 120%+ quant no trade"),
        // milky-ethereal — DEPRECADO pelo próprio Milky (Palsteron: ~5 div). Mantido
        // como referência, igual ao site. Site: wisps 10, minmagic 10, magic 9,
        // lantern 8, border:minmagic 8; NoEquip pesa MAIS que no Meatfish.
        new("Ethereal",
            [
                new StrategyBoost(["Wisps"], 2.5),
                new StrategyBoost(["MagicMonsters", "NoEquipmentDrops"], 2.2),
                new StrategyBoost(["GoldenLanterns"], 2.0),
            ],
            [
                new StrategyBoost(["MonstersAtLeastMagic", "MagicMonsterMods"], 2.0),
            ],
            [],
            [
                new PieceRequirement("4x Wisps", 4, ["Wisps"]),
                new PieceRequirement("3x Golden Lanterns", 3, ["GoldenLanterns"]),
            ],
            "⚠ DEPRECADO (retornos fracos; Milky migrou p/ Meatfish) | Wisps nas 4 laterais | GL nos cantos | Cross no centro (3 Corner, 4 T, 1 Cross, 1 Straight = 11 conexões) | usar Infested Bathysphere"),
        // alc-and-go. Queima os charts que NENHUMA outra estratégia quer: sulphur,
        // loot espalhado e encontros aleatórios. Sem requisitos nem gates — vence no
        // Auto justamente quando o pool é refugo (as outras levam penalidade 0.2).
        new("AlcGo",
            [
                new StrategyBoost(["Quantity", "Resource"], 1.5),
            ],
            [],
            [],
            null,
            "Queimar o refugo: 3 pistas ligadas por baixo | Alc & Go, colocar lanterns, clicar tudo, sair | NÃO queimar peças das outras strats (Starfish/Pantheon/GL/Possessed/Fracture/IncRares/NoEquip/Wisps/Boxes/Pillar/Pelagic)"),
    ];

    public double BoostChartWeight(string modName, double weight)
    {
        foreach (var boost in ChartBoosts)
        {
            if (boost.Keys.Any(k => modName.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return weight * boost.Mult;
            }
        }

        return weight;
    }

    /// <summary>Boost sobre o excesso: multiplicador 1.0 continua 1.0 independente do boost.</summary>
    public double BoostBorderMultiplier(string borderId, double multiplier)
    {
        foreach (var boost in BorderBoosts)
        {
            if (boost.Keys.Any(k => borderId.Contains(k, StringComparison.OrdinalIgnoreCase)))
            {
                return 1 + (multiplier - 1) * boost.Mult;
            }
        }

        return multiplier;
    }

    /// <summary>Reconstrói a peça com pesos boostados (recalcula as somas Own/Local/Global).</summary>
    public MapPiece BoostPiece(MapPiece piece) =>
        new(piece.Id, piece.Type, piece.BaseConnections,
            piece.Modifiers.Select(m => m with { Weight = BoostChartWeight(m.Name, m.Weight) }).ToList());

    /// <summary>Peças da composição que faltam no pool (labels), vazio = pronto.</summary>
    public List<string> MissingPieces(IReadOnlyCollection<MapPiece> allPieces)
    {
        var missing = new List<string>();
        foreach (var req in PieceRequirements ?? [])
        {
            var have = allPieces.Count(p => p.Modifiers.Any(m =>
                req.Keys.Any(k => m.Name.Contains(k, StringComparison.OrdinalIgnoreCase))));
            if (have < req.Count)
            {
                missing.Add(req.Label);
            }
        }

        return missing;
    }

    public bool RequirementsMet(IEnumerable<string> borderIds) =>
        RequiredBorderKeys.Length == 0 ||
        borderIds.Any(id => RequiredBorderKeys.Any(k => id.Contains(k, StringComparison.OrdinalIgnoreCase)));

    public double ScoreBoard(IReadOnlyCollection<MapPiece> allPieces, double effectiveMultSum, IEnumerable<string> borderIds)
    {
        var top9 = allPieces
            .Select(BoostPiece)
            .Select(p => p.OwnModifier + p.LocalModifier + p.GlobalModifier)
            .OrderByDescending(v => v)
            .Take(9)
            .ToList();
        if (top9.Count == 0)
        {
            return 0;
        }

        var score = top9.Average() * effectiveMultSum;
        if (!RequirementsMet(borderIds))
        {
            score *= MissingRequirementPenalty;
        }

        // Composição incompleta = estratégia "aguardando peças" (all-or-nothing).
        if (MissingPieces(allPieces).Count > 0)
        {
            score *= MissingRequirementPenalty;
        }

        return score;
    }
}
