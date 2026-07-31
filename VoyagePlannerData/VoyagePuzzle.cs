using System.Collections.Generic;

namespace DeepwaterEngagementSuite.VoyagePlannerData;

public record VoyagePuzzle(
    List<MapPiece> AvailablePieces,
    double[,] LocationModifiers,
    List<LockedPlacement> LockedPlacements,
    // Modelo de borders v2 (semânticas do site one-more-map, 31/07) — opcionais;
    // null = modelo legado (só LocationModifiers). Consumidos pelo Fast; o MRV
    // de fallback segue no legado.
    double[] CellMagnitude = null,     // fator por célula (1 + Σmag/100): ChartEffect amplifica a PEÇA ocupante
    double[][] CellMultByConn = null,  // [célula][conexões 0..4]: M com borders ±/conexão no nº real de braços casados
    double[][] PieceCellBonus = null); // [peça][célula]: bônus posicionais da estratégia (PositionRules)
