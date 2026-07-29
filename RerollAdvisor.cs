using System;
using System.Collections.Generic;

namespace DeepwaterEngagementSuite;

public static class RerollAdvisor
{
    public const int BaseCost = 3000;

    /// <summary>Contagem de border mods por tile index (0..8) — mesmo layout de GetTileMods.</summary>
    public static readonly int[] BorderModCountPerTile = [2, 1, 2, 1, 0, 1, 2, 1, 2];

    public static long NextCost(int rerollsDone) => BaseCost * (1L << rerollsDone);

    /// <summary>
    /// Razão entre a soma dos multiplicadores efetivos do board atual e a do board
    /// baseline "médio". Não depende do solve — é a qualidade dos borders em si.
    /// </summary>
    public static double BorderRatio(double[,] effectiveMults, double[,] baselineMults)
    {
        double actual = 0, baseline = 0;
        for (var r = 0; r < 3; r++)
        {
            for (var c = 0; c < 3; c++)
            {
                actual += effectiveMults[r, c];
                baseline += baselineMults[r, c];
            }
        }

        return baseline > 0 ? actual / baseline : 0;
    }

    public static bool ShouldKeep(double ratio, double keepThreshold) => ratio >= keepThreshold;

    /// <summary>
    /// Board hipotético "médio": cada tile recebe média^numBorderMods × peso de posição.
    /// Serve de denominador para o ratio R = melhor score atual / score baseline.
    /// </summary>
    public static double[,] BuildBaselineMultipliers(
        double averageBorderMultiplier,
        double[,] positionWeights,
        IReadOnlyList<int> borderModCountPerTile)
    {
        var result = new double[3, 3];
        for (var i = 0; i < 9; i++)
        {
            var r = i / 3;
            var c = i % 3;
            result[r, c] = Math.Pow(averageBorderMultiplier, borderModCountPerTile[i]) * positionWeights[r, c];
        }

        return result;
    }
}
