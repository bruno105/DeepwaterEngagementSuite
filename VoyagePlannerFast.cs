using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using DeepwaterEngagementSuite.VoyagePlannerData;

namespace DeepwaterEngagementSuite;

/// <summary>
/// Solver EXATO por enumeração de topologias + DP de atribuição (port do upstream
/// PR#6/syrairc, adaptado ao nosso score). Funciona porque nosso score é separável
/// por (peça, célula): peça i em c contribui Own×M[c] + Local×ΣM[vizinhos de c] +
/// Global×ΣM — e os multiplicadores M (borders × P, boostados pela estratégia) são
/// constantes por célula. Sem cap de pool e sem timeout: ótimo certificado sobre
/// TODOS os charts. Limitação: ignora LockedPlacements (call site passa []).
/// </summary>
public class VoyagePlannerFast
{
    private const int GridSize = 3;
    private const int Cells = GridSize * GridSize;
    private const int States = 1 << Cells;

    private static readonly (Direction Dir, int Dr, int Dc)[] Dirs =
    [
        (Direction.Up, 1, 0),
        (Direction.Down, -1, 0),
        (Direction.Left, 0, -1),
        (Direction.Right, 0, 1),
    ];

    private static readonly int[] InGrid = BuildInGrid();
    private static readonly int[][] Topologies = BuildTopologies();

    private static int[] BuildInGrid()
    {
        var mask = new int[Cells];
        for (var r = 0; r < GridSize; r++)
        for (var c = 0; c < GridSize; c++)
        foreach (var (dir, dr, dc) in Dirs)
        {
            var nr = r + dr;
            var nc = c + dc;
            if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
            mask[r * GridSize + c] |= (int)dir;
        }

        return mask;
    }

    // Todo subconjunto das 12 arestas internas cujas arestas abertas conectam as 9 células.
    private static int[][] BuildTopologies()
    {
        var edges = new List<(int A, int B, Direction Dir)>();
        for (var r = 0; r < GridSize; r++)
        for (var c = 0; c < GridSize; c++)
        {
            var i = r * GridSize + c;
            if (c < GridSize - 1) edges.Add((i, i + 1, Direction.Right));
            if (r < GridSize - 1) edges.Add((i, i + GridSize, Direction.Up));
        }

        var found = new List<int[]>();
        var neighbours = new List<int>[Cells];

        for (var subset = 0; subset < 1 << 12; subset++)
        {
            var cellMask = new int[Cells];
            for (var i = 0; i < Cells; i++) neighbours[i] = [];

            for (var e = 0; e < edges.Count; e++)
            {
                if ((subset >> e & 1) == 0) continue;
                var (a, b, dir) = edges[e];
                cellMask[a] |= (int)dir;
                cellMask[b] |= (int)dir.Opposite();
                neighbours[a].Add(b);
                neighbours[b].Add(a);
            }

            if (Reaches(neighbours)) found.Add(cellMask);
        }

        return found.ToArray();
    }

    private static bool Reaches(List<int>[] neighbours)
    {
        var seen = 1;
        var stack = new Stack<int>();
        stack.Push(0);
        while (stack.TryPop(out var cell))
        {
            foreach (var next in neighbours[cell])
            {
                if ((seen >> next & 1) != 0) continue;
                seen |= 1 << next;
                stack.Push(next);
            }
        }

        return seen == States - 1;
    }

    public IEnumerable<VoyageSolutionResult> Solve(VoyagePuzzle puzzle, VoyagePlannerSettings settings = null)
    {
        settings ??= new VoyagePlannerSettings();
        var pieces = puzzle.AvailablePieces;
        var n = pieces.Count;
        var topN = Math.Max(1, settings.TopN);

        if (n < Cells)
        {
            yield return new VoyageSolutionResult([], 0, 0);
            yield break;
        }

        // Multiplicadores por célula do NOSSO modelo (borders × P, já boostados).
        var m = new double[Cells];
        for (var cell = 0; cell < Cells; cell++)
        {
            m[cell] = puzzle.LocationModifiers[cell / GridSize, cell % GridSize];
        }

        var mTotal = m.Sum();
        var mNeighbours = new double[Cells];
        for (var cell = 0; cell < Cells; cell++)
        {
            var r = cell / GridSize;
            var c = cell % GridSize;
            foreach (var (_, dr, dc) in Dirs)
            {
                var nr = r + dr;
                var nc = c + dc;
                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                mNeighbours[cell] += m[nr * GridSize + nc];
            }
        }

        // weight[i][cell] = contribuição da peça i na célula: valor próprio na célula,
        // mods adjacentes entregues aos vizinhos, mods de voyage ao board inteiro.
        var weight = new double[n][];
        var eligible = new int[n][];
        var rotation = new byte[n][];

        for (var i = 0; i < n; i++)
        {
            var piece = pieces[i];
            weight[i] = new double[Cells];
            eligible[i] = new int[Cells];
            rotation[i] = new byte[Cells * 16];
            rotation[i].AsSpan().Fill(byte.MaxValue);

            for (var cell = 0; cell < Cells; cell++)
            {
                weight[i][cell] = piece.OwnModifier * m[cell] +
                                  piece.LocalModifier * mNeighbours[cell] +
                                  piece.GlobalModifier * mTotal;
            }

            // Quais conjuntos de braços internos a peça apresenta em cada célula,
            // e uma rotação por conjunto (braços para fora do grid são livres).
            for (var rot = 0; rot < piece.DistinctRotations; rot++)
            {
                var conn = (int)piece.GetConnections(rot);
                for (var cell = 0; cell < Cells; cell++)
                {
                    var inGrid = conn & InGrid[cell];
                    var slot = cell * 16 + inGrid;
                    if (rotation[i][slot] != byte.MaxValue) continue;
                    eligible[i][cell] |= 1 << inGrid;
                    rotation[i][slot] = (byte)rot;
                }
            }
        }

        var reachable = new int[Topologies.Length][];
        var bound = new double[Topologies.Length];

        for (var t = 0; t < Topologies.Length; t++)
        {
            var topo = Topologies[t];
            var allow = new int[n];
            var total = 0.0;
            var feasible = true;

            for (var cell = 0; cell < Cells && feasible; cell++)
            {
                var best = double.NegativeInfinity;
                for (var i = 0; i < n; i++)
                {
                    if ((eligible[i][cell] >> topo[cell] & 1) == 0) continue;
                    allow[i] |= 1 << cell;
                    if (weight[i][cell] > best) best = weight[i][cell];
                }

                if (double.IsNegativeInfinity(best)) feasible = false;
                else total += best;
            }

            reachable[t] = allow;
            bound[t] = feasible ? total : double.NegativeInfinity;
        }

        var order = Enumerable.Range(0, Topologies.Length).OrderByDescending(t => bound[t]).ToArray();

        var dpPrev = new double[States];
        var dpNext = new double[States];
        var choice = new byte[n][];
        for (var i = 0; i < n; i++) choice[i] = new byte[States];

        var top = new List<VoyageSolution>(topN);
        var explored = 0L;
        var pruned = 0L;
        var assignment = new int[Cells];

        for (var o = 0; o < order.Length; o++)
        {
            var t = order[o];

            if (double.IsNegativeInfinity(bound[t]) || (top.Count >= topN && bound[t] <= top[^1].TotalScore))
            {
                pruned += order.Length - o;
                break;
            }

            explored++;
            var score = BestAssignment(n, weight, reachable[t], dpPrev, dpNext, choice, assignment);
            if (double.IsNegativeInfinity(score)) continue;
            if (top.Count >= topN && score <= top[^1].TotalScore) continue;

            var topo = Topologies[t];
            var grid = new MapPiecePlacement[GridSize, GridSize];
            for (var cell = 0; cell < Cells; cell++)
            {
                var piece = pieces[assignment[cell]];
                var rot = rotation[assignment[cell]][cell * 16 + topo[cell]];
                grid[cell / GridSize, cell % GridSize] = new MapPiecePlacement(piece, rot, piece.GetConnections(rot));
            }

            Insert(top, topN, new VoyageSolution(grid, score, true));
        }

        yield return new VoyageSolutionResult(top, explored, pruned);
    }

    private static double BestAssignment(
        int n, double[][] weight, int[] allow, double[] dpPrev, double[] dpNext, byte[][] choice, int[] assignment)
    {
        dpPrev.AsSpan().Fill(double.NegativeInfinity);
        dpPrev[0] = 0;

        for (var i = 0; i < n; i++)
        {
            var mine = choice[i];
            var open = allow[i];
            var w = weight[i];

            Array.Copy(dpPrev, dpNext, States);
            mine.AsSpan().Fill(byte.MaxValue);

            if (open != 0)
            {
                for (var mask = 0; mask < States; mask++)
                {
                    var from = dpPrev[mask];
                    if (double.IsNegativeInfinity(from)) continue;

                    var free = open & ~mask;
                    while (free != 0)
                    {
                        var bit = free & -free;
                        free ^= bit;
                        var cell = BitOperations.TrailingZeroCount(bit);
                        var value = from + w[cell];
                        if (value <= dpNext[mask | bit]) continue;
                        dpNext[mask | bit] = value;
                        mine[mask | bit] = (byte)cell;
                    }
                }
            }

            (dpPrev, dpNext) = (dpNext, dpPrev);
        }

        var full = States - 1;
        if (double.IsNegativeInfinity(dpPrev[full])) return double.NegativeInfinity;

        var state = full;
        for (var i = n - 1; i >= 0; i--)
        {
            var cell = choice[i][state];
            if (cell == byte.MaxValue) continue;
            assignment[cell] = i;
            state ^= 1 << cell;
        }

        return dpPrev[full];
    }

    private static void Insert(List<VoyageSolution> top, int topN, VoyageSolution solution)
    {
        var at = top.Count;
        for (var i = 0; i < top.Count; i++)
        {
            if (solution.TotalScore <= top[i].TotalScore) continue;
            at = i;
            break;
        }

        top.Insert(at, solution);
        if (top.Count > topN) top.RemoveAt(top.Count - 1);
    }
}
