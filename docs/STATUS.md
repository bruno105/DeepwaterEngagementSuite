# DeepwaterEngagementSuite — estado do projeto (sessão 29–30/07/2026)

Âncora de contexto para sessões futuras. Detalhes: specs/plans em `docs/superpowers/`,
`docs/upstream-integration-notes.md`, `docs/community-strategies-reference.md`.

## O que está construído e validado in-game

- **Score v2**: `(Próprio + Σ Adjacente(vizinhos) + Voyage) × multBorda × P[r,c]`.
  Escopos Adjacent/Voyage/Self; bioma+explicits no valor próprio; PositionWeights por
  profile (tela: linha 0 = topo; solver: row 0 = baixo — spawn (0,0) bottom-left).
- **Solver**: pool cap top-K (SolverMaxCharts=24). Fast (topologias+DP) é o default; o
  fallback MRV (toggle "Use fast solver" off) foi corrigido em 31/07/2026: seed exato por
  topologias (reusa tabelas do Fast; suporta LockedPlacements) + B&B com bounds de
  relaxação de atribuição admissíveis e strong branching; _bestScore inicia em -inf
  (scores negativos valem). Empata com o Fast em 480 pools aleatórios do harness
  (subótimo/sem-solução do relatório original era timeout por bound frouxo, não
  inadmissibilidade — o bound antigo era admissível, só inútil).
- **Estratégias** (VoyageStrategy.DocStrategies): as 6 do site one-more-map, pesos
  verificados contra o bundle 2026-07-30 — Speedrun/Meatfish/DivineBorder/DivineBoxes
  (cutedog)/Ethereal (⚠ deprecado)/AlcGo (refugo, sem requisitos) — boosts por
  substring, gates de border, requisitos de peças (FALTAM no painel), layout hints;
  seletor Auto/manual/Base dinâmico. Stats rolados (quant/6, sulphur/8, pack/8) viram
  pseudo-mods Self ("Stat:*") no BuildMapPiece — craft conta no valor próprio.
  RESERVA implementada: Speedrun/AlcGo têm ReserveKeys; Solve filtra do pool (backfill
  até 12 se faltar) e ScoreBoard exclui do top-9 (Auto honesto); UI mostra protegidos.
  Posicionais (pins/centro/laterais): integração upstream.
- **Reroll advisor**: R = Σ borders efetivos / baseline médio, pré-solve, por frame;
  custo 3000×2^n; sulphur-aware; contador com reset em AreaChange.
- **Voyage Pilot**: fase/comportamento por estratégia, decay temporal de buffs, célula
  atual, aviso de retorno (lanterns apagam em ordem reversa), objetivos priorizados
  (chests, unrevealed, rares em fase kill, TILES do plano não visitados com snap para
  terreno andável), rota real via Radar.LookForRoute (retry a cada 2s).
- **Hints (Pointer nativo)**: `Pointer.Targets` + direção raw em +0x58 (gridDir=(Y,-X));
  marcadores dedupados só de alvos unrevealed; linhas opt-in das N chamas próximas.
- **Grid Tracker**: janela-minimapa; região = maior componente conexo do terreno
  (pathfinding, flood fill em background — exclui o barco); grade 3×3 + heat do plano
  só em voyage (maxLanterns>7) ou debug; trilha in-field only; Y flip (norte = cima).
- **ZoneStats (zone_stats.jsonl)**: kind (chart≤7/voyage), biome (preloads + override
  por sala do dat), room, dims, mapStats, drops de chão (dedup, loot-filter-visible),
  rewards (melhor snapshot), sulphurMax, células (seconds/order/sulphur/chests/monsters),
  planned{strategy,score,mults} no Place, regionSource, flush no OnClose/hot-reload.
- **ChartCraft (semáforo inventário)**: verde=pronto / amarelo ROLL (keeper, quant<110)
  ou SIDE (quant≥120) / vermelho=Alc&Go; quant/sulphur somados dos STATS
  (ModRecord.StatNames, keys "quantity"/"resource"); não desenha sobre tooltips.

## Convenções críticas (não redescobrir)

- Barco e fundo = MESMA instância; sair do chart = teleporte. AreaChange só ao entrar
  em nova instância. In-field = dentro de bolha do Handler (×1.5).
- Chart runs rodam na WorldArea genérica "DeepwaterEncounter"; bioma vem dos PRELOADS.
- Golden lantern: `.../Objects/DeepwaterGoldenLantern`; Pointer: `.../Objects/Pointer`.
- Sulphur ≈ uniforme por célula → NÃO é proxy de valor de tile; usar cellChests/drops.
- Reload no meio de voyage fragmenta registros (flush preserva mas células degradam).

## Dados coletados até aqui (zone_stats.jsonl, ~107 linhas)

- ~20 chart runs etiquetados (bioma CoralForest/CoralReef, Kishara's Rest detectada);
  média ~1.786 sulphur/run; todos SEM craft (quant 20% = Vesper) — falta variância.
- 3 voyages Meatfish completas; A/B rota livre vs seta: 9/9 células, +48% sulphur/min,
  50 uniques, 38% do tempo nos top-2 tiles (vs 10%). Rewards capturadas (40 tipos).

## Pendências (em ordem)

1. **Leva de calibração**: +19 charts individuais (em andamento), ~5 deles 6-modados
   (variância de mapStats) → calibrar BiomeWeights + veredito do 6-mod + P por dados.
2. **Integração upstream** (decisão: adiada): scorer por tags + correções de geometria
   (AffectsPlacedChart/PerConnection/flat-neutro) — ver upstream-integration-notes.md.
   Portar Self/P/estratégias-como-tag-boosts por cima; sistema de reserva entre
   estratégias e regras posicionais (site one-more-map) entram aí.
3. Próxima voyage com rota da seta completa → segundo ponto do A/B.

## Operacional

- Commits locais no repo do plugin (`Plugins/Source/DeepwaterEngagementSuite`), NUNCA
  push/PR sem aprovação. Reload Plugins é manual (pedir ao usuário). Bridge:
  `.claude/bridge-query.ps1 -Query "voyage|deepwaterdat|..."`. Perfil ativo: DocTuned.
