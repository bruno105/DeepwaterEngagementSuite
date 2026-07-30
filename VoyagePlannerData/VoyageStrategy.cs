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

    public static readonly VoyageStrategy[] DocStrategies =
    [
        // Regex do doc: "bottle|divine|oper" — âncora no centro, speedrun de boxes/eventos.
        new("Speedrun",
            [
                new StrategyBoost(["LostMessage", "OperativeBox", "DivinerBox"], 2.5),
                new StrategyBoost(["Strongboxes", "ArcanistBox"], 1.5),
            ],
            [new StrategyBoost(["RareMonsterDivine"], 2.0)],
            []),
        // Regex do doc: "cannot|poss|lantern|pantheon" — juice máximo, coleta de lanterns.
        // Composição do Milky: 2× Starfish, 1× Pantheon (ou 4k Wisps), 2× Sea Pillars,
        // 2× Golden Lanterns, 1× Possessed, 1× No-Equipment (fallback Rares Fracture).
        // All-or-nothing: sem as peças, evitar a voyage e continuar farmando boxes.
        new("Meatfish",
            [
                new StrategyBoost(["NoEquipmentDrops", "MonstersPossessed", "Pantheon", "GoldenLanterns", "Starfish", "Wisps2"], 2.5),
            ],
            [new StrategyBoost(["GoldenLanterns", "PackSize", "IncreasedRareMonsters", "MonstersAtLeastMagic"], 1.5)],
            [],
            [
                new PieceRequirement("2x Starfish", 2, ["Starfish"]),
                new PieceRequirement("1x Pantheon/4k Wisps", 1, ["Pantheon", "Wisps2"]),
                new PieceRequirement("2x Sea Pillars", 2, ["Room:Sea Pillars"]),
                new PieceRequirement("2x Golden Lanterns", 2, ["GoldenLanterns"]),
                new PieceRequirement("1x Possessed", 1, ["MonstersPossessed"]),
                new PieceRequirement("1x No-Equipment/Fracture", 1, ["NoEquipmentDrops", "RareFracture"]),
            ],
            "Layout Milky: Starfish topo/baixo-meio | Pantheon dir-meio | GL centro | Pillars cantos"),
        // Regex do doc: "rare monsters in all voy|strongbox" — só compensa com o border de Divine rolado.
        new("DivineBorder",
            [
                new StrategyBoost(["IncreasedRareMonsters", "Strongboxes"], 2.5),
                new StrategyBoost(["ArcanistBox", "OperativeBox", "DivinerBox"], 1.5),
            ],
            [
                new StrategyBoost(["RareMonsterDivine"], 3.0),
                new StrategyBoost(["IncreasedRareMonsters", "RareMonstersPerConnection"], 2.0),
            ],
            ["RareMonsterDivine"]),
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
