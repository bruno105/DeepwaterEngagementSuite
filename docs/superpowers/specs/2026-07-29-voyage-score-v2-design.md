# Voyage Score v2 + Reroll Advisor — Design

Data: 2026-07-29
Status: aprovado pelo usuário (brainstorming em sessão)

## Contexto e problema

O otimizador do Plan Voyage (`VoyagePlanner`) pontua uma disposição de 9 charts no board 3×3 com:

```
score = Σ células [ (somaGlobal + Σ pesos locais dos vizinhos) × multBorda(célula) ]
```

Gaps identificados contra a estratégia de referência (playbook anotado do usuário +
conversa do Discord + mecânica confirmada da liga 3.29):

1. **Sem valor por posição.** O spawn é sempre o tile bottom-left; os Allflame
   Lanterns têm 7 usos e começam a apagar quando o último é colocado, então tiles
   distantes da rota (top-middle em particular) têm probabilidade de visita
   baixíssima. Investimento (allflames, charts bons) nesses tiles é desperdiçado.
   Isso é otimização de comunidade, não penalidade oficial — por isso deve ser
   um peso configurável, não uma regra fixa.
2. **O chart não vale nada para a própria célula.** Bioma (Seafloor Ridges /
   Abyssal Plain / Undersea Groves têm densidades de monstros/chests distintas),
   explicit mods de 6-mod (iiq/iir/pack size) e nível não entram no score. Só
   implicits contam, e apenas como adjacência ou global.
3. **Sem apoio à decisão de reroll** dos border mods (compass, 3000 sulphur
   dobrando a cada uso, reset ao rodar a voyage).

Escopo desta sessão (decidido pelo usuário): remodelar o score + reroll advisor.
Fora de escopo: overhaul de highlights do board, coletor de dados por zona,
fator de nível do chart (futuro).

## Fórmula nova

```
score = Σ células [ (Próprio(chart) + Σ Adjacente(vizinhos) + somaVoyage) × multEfetivo(célula) ]
multEfetivo(célula) = multBorda(célula) × P[r,c]
```

- `Próprio(chart)` = peso do bioma + Σ pesos de mods com escopo Self.
- `Adjacente` / `somaVoyage` = como hoje (local/global renomeados em escopo).
- `P[r,c]` = peso de posição configurável (grid 3×3).

## Mudanças por componente

### 1. Modelo (`VoyagePlannerData`)

- `Modifier`: `bool IsGlobal` → `enum ModScope { Adjacent, Voyage, Self }`.
- `MapPiece`: novo `OwnModifier` (soma Self + bioma); `LocalModifier`/`GlobalModifier`
  passam a filtrar por escopo.
- `VoyagePuzzle`: sem mudança de forma — `LocationModifiers` já recebe o produto
  `multBorda × P` calculado pelo caller.

### 2. Solver (`VoyagePlanner`)

- `CalculateScore`: soma o termo `Próprio(chart na célula) × multEfetivo`.
- Chave de agrupamento de peças interchangeáveis inclui `OwnModifier`.
- `CalculateUpperBoundScore`: para células vazias usa o maior `OwnModifier` não
  colocado (análogo ao `_maxModifierPerPiece` de locais).
- Poda MRV e conectividade intactas.

### 3. Valor próprio do chart (`DeepwaterEngagementSuite.Voyage.cs`)

- Bioma lido de `DeepwaterChart.Room.Biome.Id`. Nova lista configurável
  `BiomeWeights` (Id → peso). Biomas não cadastrados aparecem na lista com peso 0
  (auto-add) para o usuário preencher.
- Defaults iniciais (chute educado a partir do pivot de dados do Discord, usuário
  ajusta; escala comparável aos pesos de mods de chart 0–100):
  `SeafloorRidges` 15, `AbyssalPlain` 12, `UnderseaGroves` 10. Os Ids reais dos
  biomas devem ser confirmados em runtime no primeiro uso (auto-add cobre isso).
- `Mods.ExplicitMods` do item do chart avaliados contra a mesma lista
  `ChartModifiers` (matching por `RawName`), tipicamente cadastrados com escopo
  `Self`.

### 4. Settings e migração de perfil

- `VoyageChartModifier` ganha seletor `Scope` (Adjacent/Voyage/Self).
- Migração: perfil antigo com `IsGlobal=true` → `Voyage`; `false` → `Adjacent`.
  `Scope` explícito tem precedência; perfis antigos carregam sem quebrar.
- `PositionWeights`: 3×3 de `RangeNode<float>` (0–2), salvo no profile (é
  estratégia). Renderizado no menu na orientação da tela (linha de cima = topo
  do board; internamente row 0 = bottom, cuidado com a conversão).
- Defaults de `P` (orientação de tela):

  | topo  | 1.00 | 0.15 | 1.00 |
  |-------|------|------|------|
  | meio  | 1.10 | 0.90 | 1.00 |
  | baixo | 1.15 | 1.05 | 1.00 |

  Racional: bottom-left = spawn; mid-left/bottom-center = rota inicial de golden
  lanterns; top-middle ≈ nunca visitado.

### 5. Reroll advisor

- Após o solve normal, um solve baseline com os mesmos charts e
  `multEfetivo_baseline = P[r,c] × B_médio(célula)`, onde `B_médio` usa a média
  dos `ValueMultiplier` do profile: cantos = média², laterais = média, centro = 1.
  Cacheado por hash do pool de charts.
- `R = melhorScoreAtual / scoreBaseline`.
  `R ≥ KeepThreshold` (default 1.0, configurável) → "KEEP";
  senão → "REROLL — próximo custo: 3000×2ⁿ sulphur".
- Contador de rerolls `n`: auto-incrementa ao detectar mudança no conjunto dos 12
  border mods com a janela aberta; botões +/− no optimizer window para correção
  manual; auto-reset em `AreaChange` (rodar a voyage reseta o custo no jogo) e
  botão de reset manual.
- Sulphur atual exibido se `DeepwaterHandler.Sulphur` for legível no contexto;
  caso contrário, mostrar apenas o custo.

## Tratamento de erros

- Bioma/Room nulos (chart inválido): peso 0 e log em Debug, sem crash.
- Perfil sem `Scope`: migração silenciosa via `IsGlobal`.
- Baseline sem borders (lista < 12): advisor oculto (mesmo caso em que o board
  hoje fica sem multiplicadores).

## Verificação

1. `dotnet build` do plugin (`exapiPackage` apontando para a raiz do PoEHelper).
2. Harness de sanidade temporário no scratchpad (não commitado): puzzle sintético
   verificando que (a) o pior chart cai no top-middle com os defaults de P,
   (b) chart de bioma forte vai para canto com borda boa, (c) upper bound nunca
   fica abaixo do score real de uma solução (poda correta).
3. In-game: usuário roda Solve/Place no Plan Voyage após Reload Plugins.

## Riscos e trade-offs

- Chave de agrupamento maior → menos simetria → solver mais lento; mitigado pelo
  time limit existente e poda com Own no upper bound.
- Pesos são heurísticos por decisão explícita (chute educado + ajuste manual);
  um modelo de EV real fica como evolução futura se o usuário coletar dados.
