# Upstream (exApiTools/DeepwaterEngagementSuite) — notas de integração e erros a não repetir

Data: 2026-07-30. Upstream analisado em `FETCH_HEAD = 82bce3d` (10 commits à frente do
nosso ponto de fork). **Integração ainda NÃO feita** — decisão deliberada; este doc é o
mapa para quando for feita.

## Erros de modelagem NOSSOS que o upstream corrige (não re-cometer)

1. **`ChartEffect*` e `ChanceToNotConsumeChart*` NÃO são multiplicadores de tile.**
   São `AffectsPlacedChart`: multiplicam os mods **do chart colocado naquele tile**,
   onde quer que o valor aterrisse (adjacências entregam nos vizinhos, globais no board
   todo). Nosso modelo atual trata como multiplicador do tile — placement errado: o
   correto é colocar ali o chart de mods mais fortes, não tratar o tile como "canto juicy".

2. **Borders `*PerConnection` escalam com as conexões da peça colocada.**
   Multiplicador efetivo = `1 + (mult − 1) × conexões`. Um Cross em
   QuantityPerConnection1 (1.4 base per-connection) vale ×2.6. Nosso modelo trata flat.

3. **Borders de valor fixo não guiam placement.** Com os 9 tiles sempre preenchidos,
   "Additional Crabs/Ducat chest/Treasure Anchors" somam o mesmo em qualquer solução —
   no SOLVE devem ser neutros (upstream usa tag `None`). NUANCE NOSSA: no **reroll
   advisor** eles ainda valem (R compara boards entre rerolls, não placements dentro de
   um board) — na integração, o advisor precisa de uma visão diferente da do solver.

4. **Border só deveria multiplicar recompensa compatível (tags).** Um border de rares
   não multiplica uma essence adjacency que caiu no mesmo tile. Upstream:
   `ModifierTag` flags (Monsters, MagicMonsters, RareMonsters, Essences, Strongboxes,
   Uniques, Currency, Scarabs, Gold, Equipment, Experience, Resources, Lanterns, Rarity)
   em mods e borders; match por interseção; `All` pega tudo, `None` inerte. Perfis
   antigos sem `Tags` caem em `All` (compat).

5. **Agrupamento de peças por SOMA de pesos perde informação.** Nosso group key usa
   (Own, Local, Global) somados — composições diferentes com mesma soma agrupam juntas.
   Inócuo no scorer por somas, ERRADO com tags. Upstream agrupa pela assinatura
   completa de mods.

## O que o upstream tem e como casa com o nosso fork

- `VoyagePlannerData/VoyageScorer.cs` (493 linhas): todo o score (inclusive upper bound
  admissível) extraído do planner; precompute por (tile, tag-mask, conexões); peças
  ordenadas por valor (nosso ordering é conexões-primeiro — atende pools pobres em
  conectividade; avaliar combinação dos dois).
- Optimizer com **score por tile + árvore "Score details"** (atribuição por peça/border)
  — é o "previsto" do nosso previsto-vs-realizado; casa com `cellSulphur/cellChests`
  do zone_stats.
- `profiles/default.json` re-tagueado e re-ponderado (referência de pesos fresca;
  starfish weights trocados no 82bce3d).
- `Label` em VoyageChartModifier (nomes de exibição).
- Lanterns/pointers (0299363): confirma path exato
  `Metadata/Terrain/Leagues/Deepwater/Objects/DeepwaterGoldenLantern` (nosso match por
  Contains("GoldenLantern") cobre) e `.../Objects/Pointer`. Ícone deles:
  `MapIconsIndex.LabyrinthGoldKey`. Nossos hints (cache por EntityAdded, alvos exatos
  dedupados, alvo ativo via +0x58) são mais avançados que os deles (varredura por frame,
  linhas simples).

## Plano de integração (quando for a hora)

1. Adotar o núcleo upstream: VoyageScorer + ModifierTag + BorderEffect + correções acima.
2. Portar NOSSAS extensões por cima:
   - Escopo `Self` (bioma + explicits como valor da própria célula) — upstream não tem;
     vira um terceiro modo de entrega no scorer.
   - `P[r,c]` (position weights) — multiplicador de célula pós-scorer.
   - **Estratégias viram boosts de TAG** (Speedrun → Strongboxes|Currency; Meatfish →
     Lanterns|Uniques|Monsters; DivineBorder → RareMonsters + gate no border de Divine)
     — substitui nosso match por substring.
   - Reroll advisor: manter R por soma de borders, mas com as semânticas corrigidas
     (AffectsPlacedChart/PerConnection) e SEM zerar borders flat (ver nuance no item 3).
3. Manter intactos (não conflitam com o scorer): Pilot, Hints, Grid Tracker, ZoneStats.
4. Conflitos esperados: Modifier.cs (ModScope nosso + Tags deles — mesclar),
   VoyagePlanner.cs (reescrito lá), Voyage.cs (nosso arquivo divergiu muito),
   default.json (regerar mesclando tags deles + escopos/pesos nossos).

## Lições do NOSSO coletor de dados (sessões 29–30/07, já corrigidas em commits)

- Reward window: snapshot "último vence" captura sobras pós-saque → guardar o MAIOR.
- Registro de zona vivia em memória até o AreaChange → flush no OnClose e no
  OnPluginDestroyForHotReload (reload/fechar não perde mais o run em andamento).
- Chart runs rodam na área genérica `DeepwaterEncounter` → bioma NÃO vem de WorldArea
  nem de paths de entidades (todos genéricos: Metadata/Monsters/DeepwaterLeague/...);
  vem dos PRELOADS da área (técnica PreloadAlert, ChangeCount da área) — validação
  pendente no próximo chart run.
- `Handler.Sulphur` tem semântica ambígua (carteira vs instância); o sinal bom é o
  drop de chão "Dead Man's Sulphur" + sulphurMax.
- `placedLanterns` zera na extração → gravar o máximo do run.
