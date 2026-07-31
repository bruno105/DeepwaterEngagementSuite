# DeepwaterEngagementSuite — estado do projeto (sessões 29–31/07/2026)

## Modelo de borders v2 + regras posicionais (31/07, fim do dia)

Portado do código aberto do site one-more-map (github.com/one-more-map/
one-more-map.github.io — scoring.ts/solver.ts/strategies.ts/mods.ts):

- **ChartEffect (magnitude)** NÃO multiplica o tile: amplifica os mods da PEÇA
  ocupante (40/60/80% por tier). No solver Fast: fator `CellMagnitude` sobre a
  contribuição inteira da peça; no painel/advisor: aproximado como 1.4-1.8×.
- **QuantityPerConnection** = base 120/180% de quant MENOS 50% por conexão
  CASADA da peça no tile (3-4 conexões = border NEGATIVO, piso 0.2);
  **RareMonstersPerConnection** = +50/75% POR conexão (0 conexões = nada,
  âncora do cfg em 2). No Fast é exato por topologia (`CellMultByConn[cell][conn]`,
  popcount dos braços casados); display ancorado em 2 conexões.
- **PositionRules** por estratégia (bônus suaves por peça×célula, escala do
  nosso score): Speedrun quant nas 4 laterais (contínuo) + sulphur no tile do
  Filthscrabble; Meatfish = board do Milky (Starfish ±400 topo/fundo-meio,
  Pantheon SÓ meio-direita, GL centro, Pillars cantos); Divine* = peça-âncora
  no tile do border Divine rolado + feeders adjacentes. Resolução NearBorderKey
  usa o border ROLADO (GetTileMods).
- **Enforcement Speedrun**: UMA peça de box (Operative > Diviner > Bottle)
  TRAVADA no centro via LockedPlacements (Fast honra travas posicionais,
  rotação livre por topologia).
- **Fallback MRV segue no modelo legado** (flat) — documentado no call site;
  harness MRV×Fast continua válido só para puzzles legados.
- Números dos bônus/âncoras são chutes calibráveis — validar com runs reais.
- NÃO portado (decisão): layouts de conectores por estratégia (highway AlcGo),
  modo filler (pior board), mods de chart com scaling por conexão (sem textos
  reais conhecidos), relaxamento de conectividade (site diz que o jogo NÃO
  exige tudo-conectado — nossas topologias exigem; TESTAR in-game antes).

## Validações fechadas em 31/07

- **Pipeline de estratégias AUDITADO dígito a dígito**: harness (scratchpad
  StrategyAudit) roda o código real (ScoreBoard/BoostPiece/BoostBorderMultiplier/
  ScreenToGrid) sobre board+pool do bridge (`voyage` agora expõe room+stats) e
  reproduziu o painel exato (905/1053/206/232/734/3484 com biomas 0; 986/1156/...
  com biomas calibrados). Boosts de border, gates, reserva e penalidades conferidos
  à mão numa board real (QuantPerConn 2.8→4.6, GL 5→7, IncRares3 2→3).
- **Solver DP validado in-game**: 47 topologias exploradas/2.487 podadas, ótimo
  certificado; ~5s eram releitura de memória (corrigido: peças via cache na thread
  de render, stats em passada única, Id refeito p/ índice atual do pool).
- **Protocolo de edição de profile** (aprendido a caro): editar o JSON em disco e
  o usuário clicar "Reload profiles" pela UI (relê disco + ApplyProfile limpa o
  _chartValueCache). Edits NÃO sobrevivem a Reload Plugins (o destroy sincroniza o
  profile ATIVO da memória por cima do arquivo).
- Chart craft: cada explicit carrega um reward rider (values[0]) que cai em UMA
  recompensa (quant/rarity/gold/sulphur/pack); implícito real oculto até chartar
  na Valerie (semáforo do inventário recalibrado com 25 charts reais).
  **MECÂNICA + VEREDITO do craft (31/07)**: TODO chart obrigatoriamente RODA SOLO
  antes de virar peça de plan (pipeline da liga), e o run trava o craft ("can't
  roll after running"). Riders ficam DORMENTES no solo (87 runs + teste etiquetado,
  instância sempre 20/20/20) e pagam na VOYAGE. Fluxo: decidir keeper → rolar/
  exaltar ANTES do solo → rodar solo → board. Rider budget ≈ 40-50 por mod
  (6-mod ≈ 225-300 vs ~150 do 4-mod) → Exalt paga p/ peça de board.
- BiomeWeights medidos: CoralReef=CoralForest=13 (n~40; diferença era ruído),
  Sandy 6, ThermalVent 10 placeholder. Registro da voyage Speedrun de 31/07 de
  manhã PERDEU (HUD fechado sem flush) — flush periódico ainda pendente.

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
  inadmissibilidade — o bound antigo era admissível, só inútil). Harness de regressão
  MRV×Fast (fora do repo, HUD não compila): `PoEHelper\tools\VoyageHarness`
  (`VoyageHarness.exe <pools> <baseSeed> [--limit=s] [--bound-only]`).
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
