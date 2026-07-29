using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    private double _maxModifierPerPiece;
    private double _maxOwnPerPiece;
    private bool _cancelled;
    private int _filledCount;

    // Precomputed: for each piece, all (rotation, connections) pairs.
    private record struct PieceOption(int PieceIdx, int Rotation, Direction Connections, double LocalWeight, double GlobalWeight);
    private PieceOption[][] _pieceOptionsByGroup;
    private int[] _pieceToGroup;

    public IEnumerable<VoyageSolutionResult> Solve(VoyagePuzzle puzzle, VoyagePlannerSettings settings = null)
    {
        settings ??= new VoyagePlannerSettings();
        _puzzle = puzzle;
        _grid = new MapPiecePlacement[GridSize, GridSize];
        _pieceUsed = new bool[puzzle.AvailablePieces.Count];
        _bestScore = 0;
        _topSolutions = new List<VoyageSolution>(settings.TopN);
        _nodesExplored = 0;
        _nodesPruned = 0;
        _filledCount = 0;
        _stopwatch = Stopwatch.StartNew();
        _cancelled = false;

        _maxModifierPerPiece = puzzle.AvailablePieces
            .Select(p => p.LocalModifier)
            .DefaultIfEmpty(0)
            .Max();

        _maxOwnPerPiece = puzzle.AvailablePieces
            .Select(p => p.OwnModifier)
            .DefaultIfEmpty(0)
            .Max();

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
            var globalWeight = piece.GlobalModifier;
            var localWeight = piece.LocalModifier;
            var opts = new List<PieceOption>();
            for (var rot = 0; rot < piece.DistinctRotations; rot++)
            {
                opts.Add(new PieceOption(groups[g][0], rot, piece.GetConnections(rot), localWeight, globalWeight));
            }
            _pieceOptionsByGroup[g] = opts.ToArray();
        }

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
            _pieceUsed[pieceIdx] = true;
            _filledCount++;
        }

        var results = Search(settings, lockedCells);

        foreach (var result in results)
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
                if (score >= _bestScore)
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

        // Find the most-constrained empty cell (MRV)
        var bestCell = (-1, -1);
        var bestOptions = new List<(int PieceIdx, int Rotation, Direction Connections)>();
        var bestOptionCount = int.MaxValue;

        for (var r = 0; r < GridSize; r++)
        {
            for (var c = 0; c < GridSize; c++)
            {
                if (_grid[r, c] != null) continue;

                var options = GetValidOptions(r, c);
                if (options.Count < bestOptionCount)
                {
                    bestOptionCount = options.Count;
                    bestCell = (r, c);
                    bestOptions = options;
                    if (bestOptionCount == 0) break;
                    if (bestOptionCount == 1) break;
                }
            }

            if (bestOptionCount == 0) break;
        }

        if (bestOptionCount == 0)
        {
            _nodesPruned++;
            yield break;
        }

        var (br, bc) = bestCell;
        _nodesExplored++;

        foreach (var (pieceIdx, rotation, connections) in bestOptions)
        {
            if (_cancelled) yield break;

            var piece = _puzzle.AvailablePieces[pieceIdx];
            _grid[br, bc] = new MapPiecePlacement(piece, rotation, connections);
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

        for (var i = 0; i < _pieceUsed.Length; i++)
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

    private double CalculateUpperBoundScore()
    {
        var score = 0.0;
        var emptyCount = 0;
        var actualGlobalSum = 0.0;

        for (var i = 0; i < GridSize; i++)
            for (var j = 0; j < GridSize; j++)
                if (_grid[i, j] != null)
                    actualGlobalSum += _grid[i, j].Piece.GlobalModifier;

        // Upper bound on global sum: take only the top (9 - filled) unplaced global weights
        var unplacedGlobal = new List<double>();
        for (var i = 0; i < _pieceUsed.Length; i++)
        {
            if (_pieceUsed[i]) continue;
            unplacedGlobal.Add(_pieceOptionsByGroup[_pieceToGroup[i]][0].GlobalWeight);
        }
        unplacedGlobal.Sort((a, b) => b.CompareTo(a));
        var ubGlobalSum = actualGlobalSum + unplacedGlobal.Take(GridSize * GridSize - _filledCount).Sum();

        for (var i = 0; i < GridSize; i++)
        {
            for (var j = 0; j < GridSize; j++)
            {
                if (_grid[i, j] != null)
                {
                    var cellScore = _grid[i, j].Piece.OwnModifier;
                    foreach (var (_, dr, dc) in Directions)
                    {
                        var nr = i + dr;
                        var nc = j + dc;
                        if (nr < 0 || nr >= GridSize || nc < 0 || nc >= GridSize) continue;

                        cellScore += _grid[nr, nc] != null
                            ? _grid[nr, nc].Piece.LocalModifier
                            : _maxModifierPerPiece;
                    }
                    score += (cellScore + ubGlobalSum) * _puzzle.LocationModifiers[i, j];
                }
                else
                {
                    var neighborCount = 0;
                    foreach (var (_, dr, dc) in Directions)
                    {
                        var nr = i + dr;
                        var nc = j + dc;
                        if (nr >= 0 && nr < GridSize && nc >= 0 && nc < GridSize)
                            neighborCount++;
                    }
                    score += (neighborCount * _maxModifierPerPiece + _maxOwnPerPiece + ubGlobalSum) * _puzzle.LocationModifiers[i, j];
                    emptyCount++;
                }
            }
        }

        return score;
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