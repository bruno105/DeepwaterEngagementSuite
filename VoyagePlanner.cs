using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using DeepwaterEngagementSuite.VoyagePlannerData;

namespace DeepwaterEngagementSuite;

public class VoyagePlanner
{
    private const int GridSize = 3;

    private static readonly (Direction Dir, int Dr, int Dc)[] Directions =
    [
        (Direction.Up, 1, 0),
        (Direction.Down, -1, 0),
        (Direction.Left, 0, -1),
        (Direction.Right, 0, 1)
    ];

    private MapPiecePlacement[,] _grid;
    private bool[] _pieceUsed;
    private double _bestScore;
    private List<VoyageSolution> _topSolutions;
    private long _nodesExplored;
    private long _nodesPruned;
    private Stopwatch _stopwatch;
    private VoyagePuzzle _puzzle;
    private bool _cancelled;
    private int _filledCount;

    // Decomposição exata do score (a mesma do VoyagePlannerFast): a peça i na célula c
    // contribui Own×M[c] + Local×ΣM[vizinhos de c] + Global×ΣM, independente das demais.
    // _w[i][cell] alimenta o upper bound por relaxação de atribuição.
    private double[][] _w;
    // Índice da peça em cada célula (r*3+c), -1 se vazia — espelho de _grid para o bound.
    private int[] _gridPieceIdx;
    // Buffers do bound adjacency-aware do scan MRV (Search é iterator, sem stackalloc).
    // Seguros entre níveis da recursão: consumidos antes de recursar.
    private double[] _scanGroupBest;
    private int[] _scanGroupUnused;
    private double[] _scanCandidates;
    private int[] _scanGroupCellMask;
    private int[] _scanEmptyCells;
    private double[] _dpPrev;
    private double[] _dpNext;

    // Precomputed: for each piece, all (rotation, connections) pairs.
    private record struct PieceOption(int PieceIdx, int Rotation, Direction Connections);
    private PieceOption[][] _pieceOptionsByGroup;
    private int[] _pieceToGroup;
    // Ordem de tentativa: mais conexões primeiro (acha o primeiro board conectado rápido,
    // habilitando a poda por upper bound), valor como desempate.
    private int[] _pieceOrder;

    public IEnumerable<VoyageSolutionResult> Solve(VoyagePuzzle puzzle, VoyagePlannerSettings settings = null)
    {
        settings ??= new VoyagePlannerSettings();
        _puzzle = puzzle;
        _grid = new MapPiecePlacement[GridSize, GridSize];
        _pieceUsed = new bool[puzzle.AvailablePieces.Count];
        // -inf, não 0: com pesos ruins o ótimo pode ser negativo e ainda assim é solução.
        _bestScore = double.NegativeInfinity;
        _topSolutions = new List<VoyageSolution>(settings.TopN);
        _nodesExplored = 0;
        _nodesPruned = 0;
        _filledCount = 0;
        _stopwatch = Stopwatch.StartNew();
        _cancelled = false;

        var mTotal = 0.0;
        var mNeighbours = new double[GridSize * GridSize];
        for (var r = 0; r < GridSize; r++)
        {
            for (var c = 0; c < GridSize; c++)
            {
                mTotal += puzzle.LocationModifiers[r, c];
                foreach (var (_, dr, dc) in Directions)
                {
                    var nr = r + dr;
                    var nc = c + dc;
                    if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                    mNeighbours[r * GridSize + c] += puzzle.LocationModifiers[nr, nc];
                }
            }
        }

        _w = new double[puzzle.AvailablePieces.Count][];
        for (var i = 0; i < puzzle.AvailablePieces.Count; i++)
        {
            var p = puzzle.AvailablePieces[i];
            _w[i] = new double[GridSize * GridSize];
            for (var cell = 0; cell < GridSize * GridSize; cell++)
            {
                _w[i][cell] = p.OwnModifier * puzzle.LocationModifiers[cell / GridSize, cell % GridSize] +
                              p.LocalModifier * mNeighbours[cell] +
                              p.GlobalModifier * mTotal;
            }
        }

        _gridPieceIdx = new int[GridSize * GridSize];
        Array.Fill(_gridPieceIdx, -1);
        _scanCandidates = new double[puzzle.AvailablePieces.Count];

        // Group pieces by (Type, BaseConnections, GlobalWeight, LocalWeight, OwnWeight) — pieces
        // in the same group are interchangeable for both connectivity and scoring.
        var groupMap = new Dictionary<(PieceType, Direction, double, double, double), int>();
        var groups = new List<List<int>>();
        _pieceToGroup = new int[puzzle.AvailablePieces.Count];

        for (var i = 0; i < puzzle.AvailablePieces.Count; i++)
        {
            var p = puzzle.AvailablePieces[i];
            var globalWeight = p.GlobalModifier;
            var localWeight = p.LocalModifier;
            var key = (p.Type, p.BaseConnections, globalWeight, localWeight, p.OwnModifier);
            if (!groupMap.TryGetValue(key, out var g))
            {
                g = groups.Count;
                groupMap[key] = g;
                groups.Add([]);
            }
            groups[g].Add(i);
            _pieceToGroup[i] = g;
        }

        // Precompute all (rotation, connections) options for each group.
        _pieceOptionsByGroup = new PieceOption[groups.Count][];
        for (var g = 0; g < groups.Count; g++)
        {
            var piece = puzzle.AvailablePieces[groups[g][0]];
            var opts = new List<PieceOption>();
            for (var rot = 0; rot < piece.DistinctRotations; rot++)
            {
                opts.Add(new PieceOption(groups[g][0], rot, piece.GetConnections(rot)));
            }
            _pieceOptionsByGroup[g] = opts.ToArray();
        }

        _scanGroupBest = new double[groups.Count];
        _scanGroupUnused = new int[groups.Count];
        _scanGroupCellMask = new int[groups.Count];
        _scanEmptyCells = new int[GridSize * GridSize];
        _dpPrev = new double[1 << (GridSize * GridSize)];
        _dpNext = new double[1 << (GridSize * GridSize)];

        _pieceOrder = Enumerable.Range(0, puzzle.AvailablePieces.Count)
            .OrderByDescending(i => CountConnections(puzzle.AvailablePieces[i].BaseConnections))
            .ThenByDescending(i =>
            {
                var p = puzzle.AvailablePieces[i];
                return p.OwnModifier + p.LocalModifier + p.GlobalModifier;
            })
            .ToArray();

        // Handle locked placements
        var lockedCells = puzzle.LockedPlacements
            .Select(lp => (lp.Row, lp.Col))
            .ToHashSet();
        var lockedAssignments = puzzle.LockedPlacements
            .ToDictionary(
                lp => (lp.Row, lp.Col),
                lp => (puzzle.AvailablePieces.IndexOf(puzzle.AvailablePieces.First(p => p.Id == lp.PieceId)), lp.Rotation));

        // Place locked cells first
        foreach (var (r, c) in lockedCells)
        {
            var (pieceIdx, rotation) = lockedAssignments[(r, c)];
            var piece = puzzle.AvailablePieces[pieceIdx];
            var connections = piece.GetConnections(rotation);
            _grid[r, c] = new MapPiecePlacement(piece, rotation, connections);
            _gridPieceIdx[r * GridSize + c] = pieceIdx;
            _pieceUsed[pieceIdx] = true;
            _filledCount++;
        }

        // Fase seed: ótimo do board por enumeração de topologias + DP de atribuição —
        // incumbente em milissegundos, ou prova de que o puzzle é infactível (aí não há
        // o que buscar). Sem isso, pools quase-infactíveis fazem o DFS vaguear o budget
        // inteiro sem achar (ou sem refutar) um board, e o score final fica refém da
        // sorte da ordem de exploração.
        var seed = SeedFromTopologies();
        if (seed == null)
        {
            yield return FinalResult();
            yield break;
        }

        _bestScore = seed.TotalScore;
        _topSolutions.Add(seed);
        if (settings.YieldIntermediate)
        {
            yield return new VoyageSolutionResult(
                new List<VoyageSolution>(_topSolutions),
                _nodesExplored,
                _nodesPruned);
        }

        // Fase de busca: B&B completo — certifica o seed, preenche o TopN com empates
        // e melhora o resultado se a fase seed tiver algum bug. Strong branching nas
        // camadas rasas (ordena e poda filhos pelo UB com a escolha fixada); no fundo,
        // ordem por conexões de GetValidOptions (mais leaves conexas por segundo).
        foreach (var result in Search(settings, lockedCells))
        {
            if (_cancelled) yield break;
            yield return result;
        }

        yield return FinalResult();
    }

    public void Cancel() => _cancelled = true;

    /// <summary>
    /// MRV-based backtracking search: at each step, pick the empty cell with the fewest valid
    /// piece options (Minimum Remaining Values). This dramatically reduces the search space
    /// because highly-constrained cells are resolved first, propagating adjacency constraints
    /// to the remaining cells.
    /// </summary>
    private IEnumerable<VoyageSolutionResult> Search(VoyagePlannerSettings settings, HashSet<(int, int)> lockedCells)
    {
        if (_cancelled) yield break;

        if (settings.TimeLimitSeconds.HasValue &&
            _stopwatch.Elapsed.TotalSeconds >= settings.TimeLimitSeconds.Value)
        {
            yield break;
        }

        if (_filledCount == GridSize * GridSize)
        {
            if (IsFullyConnected())
            {
                var score = CalculateScore();
                // Empates (score == _bestScore) podem ser o board do seed revisitado
                // pela busca — não duplicar no TopN.
                if (score >= _bestScore && !(score == _bestScore && IsDuplicateOfTop()))
                {
                    if (score > _bestScore)
                    {
                        _bestScore = score;
                        // New best score — clear previous solutions since they're worse
                        _topSolutions.Clear();
                    }

                    var solution = new VoyageSolution(CloneGrid(), score, true);
                    _topSolutions.Insert(0, solution);
                    if (_topSolutions.Count > settings.TopN)
                        _topSolutions.RemoveAt(_topSolutions.Count - 1);

                    if (settings.YieldIntermediate)
                    {
                        yield return new VoyageSolutionResult(
                            new List<VoyageSolution>(_topSolutions),
                            _nodesExplored,
                            _nodesPruned);
                    }
                }
            }

            yield break;
        }

        // Upper-bound prune: only prune if the upper bound is strictly worse than best.
        // Use < (not <=) so equal-scoring subtrees are still explored, allowing TopN to fill.
        if (CalculateUpperBoundScore() < _bestScore)
        {
            _nodesPruned++;
            yield break;
        }

        // Find the most-constrained empty cell (MRV). O scan é fundido com um segundo
        // upper bound, adjacency-aware: cada célula vazia só aceita as opções válidas
        // contra os vizinhos já colocados (e opções só encolhem conforme o board enche),
        // então max de w[peça][célula] sobre as opções é um bound admissível bem mais
        // justo que o de CalculateUpperBoundScore. Dual por peça idem, respeitando a
        // multiplicidade dos grupos intercambiáveis.
        var bestCell = (-1, -1);
        var bestOptions = new List<(int PieceIdx, int Rotation, Direction Connections)>();
        var bestOptionCount = int.MaxValue;
        var bestEmptyIdx = -1;
        var placedW = 0.0;
        var emptyCount = 0;
        var perCellSum = 0.0;

        for (var cell = 0; cell < GridSize * GridSize; cell++)
        {
            if (_gridPieceIdx[cell] >= 0) placedW += _w[_gridPieceIdx[cell]][cell];
        }

        Array.Fill(_scanGroupBest, double.NegativeInfinity);
        Array.Clear(_scanGroupUnused);
        Array.Clear(_scanGroupCellMask);
        for (var i = 0; i < _pieceUsed.Length; i++)
        {
            if (!_pieceUsed[i]) _scanGroupUnused[_pieceToGroup[i]]++;
        }

        for (var r = 0; r < GridSize; r++)
        {
            for (var c = 0; c < GridSize; c++)
            {
                if (_grid[r, c] != null) continue;

                var options = GetValidOptions(r, c);
                if (options.Count == 0)
                {
                    _nodesPruned++;
                    yield break;
                }

                if (options.Count < bestOptionCount)
                {
                    bestOptionCount = options.Count;
                    bestCell = (r, c);
                    bestOptions = options;
                    bestEmptyIdx = emptyCount;
                }

                var cellIdx = r * GridSize + c;
                var cellMax = double.NegativeInfinity;
                foreach (var (pieceIdx, _, _) in options)
                {
                    var wv = _w[pieceIdx][cellIdx];
                    if (wv > cellMax) cellMax = wv;
                    var g = _pieceToGroup[pieceIdx];
                    if (wv > _scanGroupBest[g]) _scanGroupBest[g] = wv;
                    _scanGroupCellMask[g] |= 1 << emptyCount;
                }

                perCellSum += cellMax;
                _scanEmptyCells[emptyCount] = cellIdx;
                emptyCount++;
            }
        }

        // Bound dual por peça: cada peça não usada de grupo colocável contribui no máximo
        // o melhor w do grupo; soma dos top-(vazias). Se nem há peças colocáveis
        // suficientes, o ramo é infactível.
        var candidateCount = 0;
        for (var g = 0; g < _scanGroupBest.Length; g++)
        {
            if (double.IsNegativeInfinity(_scanGroupBest[g])) continue;
            for (var k = 0; k < _scanGroupUnused[g]; k++) _scanCandidates[candidateCount++] = _scanGroupBest[g];
        }

        if (candidateCount < emptyCount)
        {
            _nodesPruned++;
            yield break;
        }

        Array.Sort(_scanCandidates, 0, candidateCount);
        var perPieceSum = 0.0;
        for (var k = candidateCount - emptyCount; k < candidateCount; k++) perPieceSum += _scanCandidates[k];

        if (placedW + Math.Min(perCellSum, perPieceSum) < _bestScore)
        {
            _nodesPruned++;
            yield break;
        }

        // O bound barato não podou: refina com o ótimo EXATO da relaxação de atribuição
        // (DP de bitmask, como no VoyagePlannerFast) — distinção de peças e validade
        // por célula respeitadas; só adjacência entre peças futuras e conectividade
        // ficam relaxadas. -inf = nenhuma atribuição consistente existe (poda de
        // factibilidade, vale mesmo sem incumbente).
        var assignmentUb = AssignmentUpperBound(emptyCount);
        if (double.IsNegativeInfinity(assignmentUb) || placedW + assignmentUb < _bestScore)
        {
            _nodesPruned++;
            yield break;
        }

        var (br, bc) = bestCell;
        _nodesExplored++;

        var bestCellIdx = br * GridSize + bc;

        // Strong branching nas camadas rasas: ordena (e pré-poda) os filhos pelo UB
        // exato da relaxação COM a escolha fixada. As máscaras do pai são superconjunto
        // das do filho, logo o valor é admissível para o filho.
        if (emptyCount >= 8 && !double.IsNegativeInfinity(_bestScore) && bestOptions.Count > 1)
        {
            var scored = new List<(double Ub, (int PieceIdx, int Rotation, Direction Connections) Opt)>(bestOptions.Count);
            foreach (var opt in bestOptions)
            {
                var childUb = placedW + _w[opt.PieceIdx][bestCellIdx] +
                              AssignmentUpperBound(emptyCount, 1 << bestEmptyIdx, opt.PieceIdx);
                if (childUb < _bestScore)
                {
                    _nodesPruned++;
                    continue;
                }
                scored.Add((childUb, opt));
            }

            scored.Sort((a, b) => b.Ub.CompareTo(a.Ub));
            bestOptions = scored.Select(s => s.Opt).ToList();
        }

        foreach (var (pieceIdx, rotation, connections) in bestOptions)
        {
            if (_cancelled) yield break;

            var piece = _puzzle.AvailablePieces[pieceIdx];
            _grid[br, bc] = new MapPiecePlacement(piece, rotation, connections);
            _gridPieceIdx[br * GridSize + bc] = pieceIdx;
            _pieceUsed[pieceIdx] = true;
            _filledCount++;

            if (IsConnectivityFeasible())
            {
                foreach (var result in Search(settings, lockedCells))
                {
                    yield return result;
                }
            }
            else
            {
                _nodesPruned++;
            }

            _pieceUsed[pieceIdx] = false;
            _gridPieceIdx[br * GridSize + bc] = -1;
            _grid[br, bc] = null;
            _filledCount--;
        }
    }

    /// <summary>
    /// Returns all valid (pieceIdx, rotation, connections) options for cell (r, c), considering
    /// adjacency constraints with already-placed neighbors. Only one piece per interchangeable
    /// group is included (symmetry breaking).
    /// </summary>
    private List<(int PieceIdx, int Rotation, Direction Connections)> GetValidOptions(int r, int c)
    {
        var result = new List<(int, int, Direction)>();
        var triedGroups = new HashSet<int>();

        foreach (var i in _pieceOrder)
        {
            if (_pieceUsed[i]) continue;
            var g = _pieceToGroup[i];
            if (!triedGroups.Add(g)) continue;

            foreach (var opt in _pieceOptionsByGroup[g])
            {
                if (CheckAdjacency(r, c, opt.Connections))
                {
                    result.Add((i, opt.Rotation, opt.Connections));
                }
            }
        }

        return result;
    }

    private bool CheckAdjacency(int r, int c, Direction? connections = null)
    {
        var conn = connections ?? _grid[r, c].Connections;

        foreach (var (dir, dr, dc) in Directions)
        {
            var nr = r + dr;
            var nc = c + dc;

            if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
            if (_grid[nr, nc] == null) continue;

            var neighborConn = _grid[nr, nc].Connections;
            var hasConnection = conn.HasFlag(dir);
            var neighborHasConnection = neighborConn.HasFlag(dir.Opposite());

            if (hasConnection != neighborHasConnection)
            {
                return false;
            }
        }

        return true;
    }

    private bool IsFullyConnected()
    {
        var visited = new bool[GridSize, GridSize];
        var stack = new Stack<(int R, int C)>();

        // Find first filled cell
        int sr = -1, sc = -1;
        for (var i = 0; i < GridSize && sr == -1; i++)
            for (var j = 0; j < GridSize && sr == -1; j++)
                if (_grid[i, j] != null) { sr = i; sc = j; }

        if (sr == -1) return true;

        stack.Push((sr, sc));
        visited[sr, sc] = true;
        var count = 1;

        while (stack.TryPop(out var pos))
        {
            var (cr, cc) = pos;
            var conn = _grid[cr, cc].Connections;

            foreach (var (dir, dr, dc) in Directions)
            {
                if (!conn.HasFlag(dir)) continue;

                var nr = cr + dr;
                var nc = cc + dc;

                if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                if (visited[nr, nc]) continue;
                if (_grid[nr, nc] == null) continue;

                var neighborConn = _grid[nr, nc].Connections;
                if (!neighborConn.HasFlag(dir.Opposite())) continue;

                visited[nr, nc] = true;
                count++;
                if (count == GridSize * GridSize) return true;
                stack.Push((nr, nc));
            }
        }

        return count == GridSize * GridSize;
    }

    private bool IsConnectivityFeasible()
    {
        if (_filledCount <= 1) return true;
        if (_filledCount == GridSize * GridSize) return IsFullyConnected();

        var components = CountConnectedComponents();
        if (components <= 1) return true;

        var emptyCells = GridSize * GridSize - _filledCount;

        // Each unused piece can reduce component count by at most (maxConn - 1).
        var mergeCapacities = new List<int>();
        for (var i = 0; i < _pieceUsed.Length; i++)
        {
            if (_pieceUsed[i]) continue;
            var maxConn = _pieceOptionsByGroup[_pieceToGroup[i]]
                .Max(o => CountConnections(o.Connections));
            mergeCapacities.Add(Math.Max(0, maxConn - 1));
        }

        var totalMergeCapacity = mergeCapacities
            .OrderByDescending(x => x)
            .Take(emptyCells)
            .Sum();

        if (totalMergeCapacity < components - 1) return false;

        return true;
    }

    private static int CountConnections(Direction conn)
    {
        var c = 0;
        if (conn.HasFlag(Direction.Up)) c++;
        if (conn.HasFlag(Direction.Down)) c++;
        if (conn.HasFlag(Direction.Left)) c++;
        if (conn.HasFlag(Direction.Right)) c++;
        return c;
    }

    private int CountConnectedComponents()
    {
        var visited = new bool[GridSize, GridSize];
        var components = 0;

        for (var sr = 0; sr < GridSize; sr++)
        {
            for (var sc = 0; sc < GridSize; sc++)
            {
                if (_grid[sr, sc] == null || visited[sr, sc]) continue;

                components++;
                visited[sr, sc] = true;
                var stack = new Stack<(int R, int C)>();
                stack.Push((sr, sc));

                while (stack.TryPop(out var pos))
                {
                    var (cr, cc) = pos;
                    var conn = _grid[cr, cc].Connections;

                    foreach (var (dir, dr, dc) in Directions)
                    {
                        if (!conn.HasFlag(dir)) continue;

                        var nr = cr + dr;
                        var nc = cc + dc;

                        if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                        if (visited[nr, nc] || _grid[nr, nc] == null) continue;

                        var neighborConn = _grid[nr, nc].Connections;
                        if (!neighborConn.HasFlag(dir.Opposite())) continue;

                        visited[nr, nc] = true;
                        stack.Push((nr, nc));
                    }
                }
            }
        }

        return components;
    }

    private double CalculateScore()
    {
        var score = 0.0;
        var globalSum = 0.0;

        for (var r = 0; r < GridSize; r++)
            for (var c = 0; c < GridSize; c++)
                if (_grid[r, c] != null)
                    globalSum += _grid[r, c].Piece.GlobalModifier;

        for (var r = 0; r < GridSize; r++)
        {
            for (var c = 0; c < GridSize; c++)
            {
                var cellScore = globalSum + _grid[r, c].Piece.OwnModifier;

                foreach (var (_, dr, dc) in Directions)
                {
                    var nr = r + dr;
                    var nc = c + dc;

                    if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;
                    if (_grid[nr, nc] == null) continue;

                    cellScore += _grid[nr, nc].Piece.LocalModifier;
                }

                score += cellScore * _puzzle.LocationModifiers[r, c];
            }
        }

        return score;
    }

    /// <summary>
    /// Fase seed: melhor board por enumeração de topologias (tabela estática do
    /// VoyagePlannerFast — combinatória pura do grid) + DP de atribuição com rastreio,
    /// em ordem de bound decrescente (Σ max w por célula) com corte quando o bound da
    /// próxima não supera o melhor seed. O corte é sem perda: o seed é o ÓTIMO do
    /// board completo (respeitando LockedPlacements, que o Fast ignora). A busca B&B
    /// fica com a certificação, o TopN e qualquer melhoria caso esta fase tenha bug.
    /// Retorna null se nenhuma topologia é factível — puzzle PROVADAMENTE infactível.
    /// </summary>
    private VoyageSolution SeedFromTopologies()
    {
        var n = _pieceUsed.Length;
        var cells = GridSize * GridSize;
        var groupCount = _pieceOptionsByGroup.Length;

        // Padrões de braços internos que cada grupo apresenta em cada célula, e uma
        // rotação por padrão (braços para fora do grid são livres).
        var patterns = new ushort[groupCount][];
        var rotFor = new byte[groupCount][];
        for (var g = 0; g < groupCount; g++)
        {
            patterns[g] = new ushort[cells];
            rotFor[g] = new byte[cells * 16];
            rotFor[g].AsSpan().Fill(byte.MaxValue);
            foreach (var opt in _pieceOptionsByGroup[g])
            {
                for (var cell = 0; cell < cells; cell++)
                {
                    var inGrid = (int)opt.Connections & VoyagePlannerFast.InGrid[cell];
                    var slot = cell * 16 + inGrid;
                    if (rotFor[g][slot] != byte.MaxValue) continue;
                    rotFor[g][slot] = (byte)opt.Rotation;
                    patterns[g][cell] |= (ushort)(1 << inGrid);
                }
            }
        }

        // Bound por topologia (e compatibilidade com células travadas).
        var topos = VoyagePlannerFast.Topologies;
        var bound = new double[topos.Length];
        for (var t = 0; t < topos.Length; t++)
        {
            var topo = topos[t];
            var total = 0.0;
            var feasible = true;

            for (var cell = 0; cell < cells && feasible; cell++)
            {
                var arms = topo[cell];
                var lockedIdx = _gridPieceIdx[cell];
                if (lockedIdx >= 0)
                {
                    var placedArms = (int)_grid[cell / GridSize, cell % GridSize].Connections &
                                     VoyagePlannerFast.InGrid[cell];
                    if (placedArms != arms) feasible = false;
                    else total += _w[lockedIdx][cell];
                    continue;
                }

                var best = double.NegativeInfinity;
                for (var i = 0; i < n; i++)
                {
                    if (_pieceUsed[i]) continue;
                    if ((patterns[_pieceToGroup[i]][cell] >> arms & 1) == 0) continue;
                    if (_w[i][cell] > best) best = _w[i][cell];
                }

                if (double.IsNegativeInfinity(best)) feasible = false;
                else total += best;
            }

            bound[t] = feasible ? total : double.NegativeInfinity;
        }

        var order = Enumerable.Range(0, topos.Length)
            .Where(t => !double.IsNegativeInfinity(bound[t]))
            .OrderByDescending(t => bound[t])
            .ToArray();

        var emptyCells = new List<int>(cells);
        var lockedW = 0.0;
        for (var cell = 0; cell < cells; cell++)
        {
            if (_gridPieceIdx[cell] < 0) emptyCells.Add(cell);
            else lockedW += _w[_gridPieceIdx[cell]][cell];
        }

        var k = emptyCells.Count;
        var states = 1 << k;
        var choice = new byte[n][];
        for (var i = 0; i < n; i++) choice[i] = new byte[states];
        var assignment = new int[cells];
        var bestAssignment = new int[cells];
        var bestRotations = new byte[cells];
        var bestVal = double.NegativeInfinity;
        var found = false;

        foreach (var t in order)
        {
            if (found && bound[t] <= bestVal) break;

            var topo = topos[t];

            // DP de atribuição com rastreio (mesma forma de AssignmentUpperBound).
            var dp = _dpPrev;
            var next = _dpNext;
            Array.Fill(dp, double.NegativeInfinity, 0, states);
            dp[0] = 0;

            for (var i = 0; i < n; i++)
            {
                var mine = choice[i];
                mine.AsSpan(0, states).Fill(byte.MaxValue);
                if (_pieceUsed[i]) { Array.Copy(dp, next, states); (dp, next) = (next, dp); continue; }

                var open = 0;
                for (var kk = 0; kk < k; kk++)
                {
                    if ((patterns[_pieceToGroup[i]][emptyCells[kk]] >> topo[emptyCells[kk]] & 1) != 0)
                        open |= 1 << kk;
                }

                Array.Copy(dp, next, states);
                var w = _w[i];

                if (open != 0)
                {
                    for (var mask = 0; mask < states; mask++)
                    {
                        var from = dp[mask];
                        if (double.IsNegativeInfinity(from)) continue;

                        var free = open & ~mask;
                        while (free != 0)
                        {
                            var bit = free & -free;
                            free ^= bit;
                            var kk = BitOperations.TrailingZeroCount(bit);
                            var value = from + w[emptyCells[kk]];
                            if (value <= next[mask | bit]) continue;
                            next[mask | bit] = value;
                            mine[mask | bit] = (byte)kk;
                        }
                    }
                }

                (dp, next) = (next, dp);
            }

            _dpPrev = dp;
            _dpNext = next;
            if (double.IsNegativeInfinity(dp[states - 1])) continue;

            var val = lockedW + dp[states - 1];
            if (val <= bestVal) continue;

            // Reconstrói a atribuição desta topologia (rotações capturadas agora,
            // pois topo muda a cada iteração).
            var state = states - 1;
            for (var i = n - 1; i >= 0; i--)
            {
                var kk = choice[i][state];
                if (kk == byte.MaxValue) continue;
                assignment[emptyCells[kk]] = i;
                state ^= 1 << kk;
            }

            foreach (var cell in emptyCells)
            {
                bestAssignment[cell] = assignment[cell];
                bestRotations[cell] = rotFor[_pieceToGroup[assignment[cell]]][cell * 16 + topo[cell]];
            }

            bestVal = val;
            found = true;
        }

        if (!found) return null;

        // Materializa o melhor seed via os mesmos campos da busca, para o score sair
        // bit a bit igual ao de CalculateScore.
        foreach (var cell in emptyCells)
        {
            var i = bestAssignment[cell];
            var piece = _puzzle.AvailablePieces[i];
            var rot = bestRotations[cell];
            _grid[cell / GridSize, cell % GridSize] = new MapPiecePlacement(piece, rot, piece.GetConnections(rot));
            _gridPieceIdx[cell] = i;
            _pieceUsed[i] = true;
            _filledCount++;
        }

        var solution = new VoyageSolution(CloneGrid(), CalculateScore(), true);

        foreach (var cell in emptyCells)
        {
            _pieceUsed[_gridPieceIdx[cell]] = false;
            _gridPieceIdx[cell] = -1;
            _grid[cell / GridSize, cell % GridSize] = null;
            _filledCount--;
        }

        return solution;
    }

    /// <summary>
    /// Ótimo exato da relaxação de atribuição: melhor Σ w[peça][célula] atribuindo peças
    /// não usadas às células vazias, respeitando distinção de peças e as opções válidas
    /// por célula (via _scanGroupCellMask, preenchido pelo scan MRV). DP de bitmask sobre
    /// as células vazias, mesma forma de VoyagePlannerFast.BestAssignment. Retorna -inf
    /// se não há atribuição viável.
    /// </summary>
    private double AssignmentUpperBound(int emptyCount, int startMask = 0, int excludePieceIdx = -1)
    {
        var states = 1 << emptyCount;
        var dp = _dpPrev;
        var next = _dpNext;
        Array.Fill(dp, double.NegativeInfinity, 0, states);
        dp[startMask] = 0;

        for (var i = 0; i < _pieceUsed.Length; i++)
        {
            if (_pieceUsed[i] || i == excludePieceIdx) continue;
            var open = _scanGroupCellMask[_pieceToGroup[i]];
            if (open == 0) continue;

            Array.Copy(dp, next, states);
            var w = _w[i];

            for (var mask = 0; mask < states; mask++)
            {
                var from = dp[mask];
                if (double.IsNegativeInfinity(from)) continue;

                var free = open & ~mask;
                while (free != 0)
                {
                    var bit = free & -free;
                    free ^= bit;
                    var k = BitOperations.TrailingZeroCount(bit);
                    var value = from + w[_scanEmptyCells[k]];
                    if (value > next[mask | bit]) next[mask | bit] = value;
                }
            }

            (dp, next) = (next, dp);
        }

        var result = dp[states - 1];
        _dpPrev = dp;
        _dpNext = next;
        return result;
    }

    /// <summary>
    /// Upper bound por relaxação de atribuição sobre a decomposição exata _w[peça][célula]:
    /// a parte já colocada é contabilizada exatamente; as células vazias recebem o melhor
    /// completamento possível ignorando apenas adjacência/conectividade. Dois bounds duais
    /// (admissíveis) são combinados pelo mínimo:
    ///   A) cada célula vazia com a melhor peça não usada (permite repetir peça);
    ///   B) soma dos top-K valores "melhor célula por peça não usada", K = vazias
    ///      (permite repetir célula).
    /// </summary>
    private double CalculateUpperBoundScore()
    {
        var score = 0.0;
        Span<int> emptyCells = stackalloc int[GridSize * GridSize];
        var emptyCount = 0;

        for (var cell = 0; cell < GridSize * GridSize; cell++)
        {
            var pieceIdx = _gridPieceIdx[cell];
            if (pieceIdx >= 0) score += _w[pieceIdx][cell];
            else emptyCells[emptyCount++] = cell;
        }

        if (emptyCount == 0) return score;

        Span<double> bestPerCell = stackalloc double[emptyCount];
        Span<double> bestPerPiece = stackalloc double[_pieceUsed.Length];
        bestPerCell.Fill(double.NegativeInfinity);
        var unusedCount = 0;

        for (var i = 0; i < _pieceUsed.Length; i++)
        {
            if (_pieceUsed[i]) continue;
            var w = _w[i];
            var pieceBest = double.NegativeInfinity;
            for (var k = 0; k < emptyCount; k++)
            {
                var v = w[emptyCells[k]];
                if (v > bestPerCell[k]) bestPerCell[k] = v;
                if (v > pieceBest) pieceBest = v;
            }
            bestPerPiece[unusedCount++] = pieceBest;
        }

        // Peças insuficientes para completar o grid: ramo sem solução.
        if (unusedCount < emptyCount) return double.NegativeInfinity;

        var ubPerCell = 0.0;
        for (var k = 0; k < emptyCount; k++) ubPerCell += bestPerCell[k];

        bestPerPiece[..unusedCount].Sort();
        var ubPerPiece = 0.0;
        for (var k = unusedCount - emptyCount; k < unusedCount; k++) ubPerPiece += bestPerPiece[k];

        return score + Math.Min(ubPerCell, ubPerPiece);
    }

    /// <summary>
    /// O board atual já está em _topSolutions? Usado só em empate exato de score, para
    /// a busca não duplicar o board que a fase seed registrou.
    /// </summary>
    private bool IsDuplicateOfTop()
    {
        foreach (var solution in _topSolutions)
        {
            var same = true;
            for (var r = 0; r < GridSize && same; r++)
            {
                for (var c = 0; c < GridSize && same; c++)
                {
                    var a = _grid[r, c];
                    var b = solution.Grid[r, c];
                    if (a.Piece.Id != b.Piece.Id || a.Connections != b.Connections) same = false;
                }
            }

            if (same) return true;
        }

        return false;
    }

    private MapPiecePlacement[,] CloneGrid()
    {
        var clone = new MapPiecePlacement[GridSize, GridSize];
        for (var i = 0; i < GridSize; i++)
            for (var j = 0; j < GridSize; j++)
                clone[i, j] = _grid[i, j];
        return clone;
    }

    private VoyageSolutionResult FinalResult()
    {
        return new VoyageSolutionResult(
            [.._topSolutions,],
            _nodesExplored,
            _nodesPruned);
    }
}