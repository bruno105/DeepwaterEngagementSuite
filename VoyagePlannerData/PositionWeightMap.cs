namespace DeepwaterEngagementSuite.VoyagePlannerData;

public static class PositionWeightMap
{
    /// <summary>
    /// Converte pesos em orientação de tela (linha 0 = topo do board no jogo) para o
    /// grid interno do solver (row 0 = linha de baixo — mesma convenção de BuildAsciiGrid).
    /// </summary>
    public static double[,] ScreenToGrid(float[][] screenRows)
    {
        var grid = new double[3, 3];
        for (var s = 0; s < 3; s++)
        {
            for (var c = 0; c < 3; c++)
            {
                grid[2 - s, c] = screenRows[s][c];
            }
        }

        return grid;
    }
}
