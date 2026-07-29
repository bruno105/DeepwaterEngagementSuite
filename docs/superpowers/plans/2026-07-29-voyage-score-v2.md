# Voyage Score v2 + Reroll Advisor — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fazer o otimizador do Plan Voyage pontuar valor próprio do chart (bioma + explicits + escopo Self), pesos por posição do board, e recomendar keep/reroll dos border mods.

**Architecture:** O solver (`VoyagePlanner`) é C# puro e ganha um termo de valor próprio por célula; o caller (`DeepwaterEngagementSuite.Voyage.cs`) passa `multBorda × P[r,c]` já multiplicados na matriz existente. Um harness de console no scratchpad compila os arquivos puros do solver e roda asserções (não é commitado). Partes dependentes de ExileCore são verificadas por `dotnet build` + validação in-game.

**Tech Stack:** .NET 10 (win-x64), ExileCore, ImGui.NET, Newtonsoft.Json.

## Global Constraints

- Build do plugin: `$env:exapiPackage = "C:\Users\bruno\Documents\Exile\PoEHelper"; dotnet build "C:\Users\bruno\Documents\Exile\PoEHelper\Plugins\Source\DeepwaterEngagementSuite\DeepwaterEngagementSuite.csproj"` — deve terminar sem erros a cada task.
- Git: commits **locais** no repo `Plugins\Source\DeepwaterEngagementSuite`. **NUNCA** fazer push nem abrir PR (ordem explícita do usuário).
- O harness fica em `C:\Users\bruno\AppData\Local\Temp\claude\C--Users-bruno-Documents-Exile-PoEHelper\e6a81c64-fb8b-44c5-bc3b-de5ca563fb42\scratchpad\SolverTests\` — nunca entra no repo.
- Não tocar em `config/DeepwaterEngagementSuite/profiles/Default.json` (perfil ativo do usuário); migração é feita em código.
- Orientação do grid interno: **row 0 = linha de BAIXO da tela** (ver `BuildAsciiGrid`, que desenha `grid[2 - r, c]`). Settings de posição são armazenados em orientação de TELA (linha 0 = topo) e convertidos.
- Recarga in-game é manual: preparar tudo, buildar, e pedir para o usuário apertar "Reload Plugins".

Caminho raiz do plugin (todas as referências abaixo): `C:\Users\bruno\Documents\Exile\PoEHelper\Plugins\Source\DeepwaterEngagementSuite\`

---

### Task 1: Harness de testes do solver (scratchpad)

**Files:**
- Create: `<scratchpad>\SolverTests\SolverTests.csproj`
- Create: `<scratchpad>\SolverTests\Program.cs`

**Interfaces:**
- Consumes: `VoyagePlanner.Solve(VoyagePuzzle, VoyagePlannerSettings)`, tipos de `VoyagePlannerData` (estado atual do repo).
- Produces: runner `Check(name, cond)` e helpers `MakeCross(id, mods)` / `SolveBest(pieces, mults)` usados por todos os testes das tasks seguintes. Exit code 0 = tudo passou, 1 = falha.

- [ ] **Step 1: Criar o csproj do harness**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>disable</Nullable>
    <PluginDir>C:\Users\bruno\Documents\Exile\PoEHelper\Plugins\Source\DeepwaterEngagementSuite</PluginDir>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$(PluginDir)\VoyagePlanner.cs" />
    <Compile Include="$(PluginDir)\VoyagePlannerData\*.cs" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Criar Program.cs com runner + teste de comportamento atual**

O teste "baseline_9_crosses" valida a fórmula ATUAL: 9 peças Cross com só o mod
`Default(1)` local, multiplicadores todos 1.0. Cada célula soma o Local dos
vizinhos → Σ neighborCount = cantos 2×4 + laterais 3×4 + centro 4 = **24.0**.

```csharp
using DeepwaterEngagementSuite;
using DeepwaterEngagementSuite.VoyagePlannerData;

var failed = 0;

void Check(string name, bool cond, string detail = "")
{
    Console.WriteLine($"{(cond ? "PASS" : "FAIL")}  {name} {detail}");
    if (!cond) failed++;
}

MapPiece MakeCross(int id, List<Modifier> mods = null) =>
    new(id, PieceType.Cross, Direction.All, mods ?? [new Modifier("Default", 1)]);

double[,] UniformMults(double v)
{
    var m = new double[3, 3];
    for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++)
            m[r, c] = v;
    return m;
}

VoyageSolution SolveBest(List<MapPiece> pieces, double[,] mults)
{
    var planner = new VoyagePlanner();
    VoyageSolutionResult last = null;
    foreach (var r in planner.Solve(new VoyagePuzzle(pieces, mults, []),
                 new VoyagePlannerSettings(TopN: 3, TimeLimitSeconds: 10)))
        last = r;
    return last?.Solutions.FirstOrDefault();
}

// --- baseline_9_crosses: comportamento atual ---
{
    var pieces = Enumerable.Range(0, 9).Select(i => MakeCross(i)).ToList();
    var best = SolveBest(pieces, UniformMults(1.0));
    Check("baseline_9_crosses_found", best != null);
    Check("baseline_9_crosses_score", best != null && Math.Abs(best.TotalScore - 24.0) < 1e-9,
        $"score={best?.TotalScore}");
}

Console.WriteLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
return failed == 0 ? 0 : 1;
```

- [ ] **Step 3: Rodar e verificar que passa (valida o harness contra o código atual)**

Run: `dotnet run --project "<scratchpad>\SolverTests" -c Release`
Expected: `PASS baseline_9_crosses_found`, `PASS baseline_9_crosses_score`, `ALL PASS`, exit 0.

- [ ] **Step 4: Sem commit** — nada no repo mudou nesta task.

---

### Task 2: ModScope no modelo (Modifier + MapPiece) e call-sites

**Files:**
- Modify: `VoyagePlannerData\Modifier.cs`
- Modify: `VoyagePlannerData\MapPiece.cs`
- Modify: `DeepwaterEngagementSuite.Voyage.cs` (dois call-sites que usam o bool `IsGlobal` do record)
- Test: harness Program.cs (novo bloco)

**Interfaces:**
- Produces: `enum ModScope { Adjacent, Voyage, Self }`; `record Modifier(string Name, double Weight, ModScope Scope = ModScope.Adjacent)`; `MapPiece.OwnModifier` (double, soma dos pesos com escopo Self). `MapPiece.LocalModifier` passa a somar só `Adjacent`; `GlobalModifier` soma só `Voyage`.
- Consumes: helpers do harness (Task 1).

- [ ] **Step 1: Teste que falha — OwnModifier/escopo no MapPiece**

Adicionar ao Program.cs, antes da linha final `Console.WriteLine(...)`:

```csharp
// --- mod_scope_sums: MapPiece separa Own/Local/Global por escopo ---
{
    var piece = MakeCross(0, [
        new Modifier("Default", 1),
        new Modifier("Adj", 5, ModScope.Adjacent),
        new Modifier("Glob", 7, ModScope.Voyage),
        new Modifier("Self", 11, ModScope.Self),
    ]);
    Check("mod_scope_local", Math.Abs(piece.LocalModifier - 6.0) < 1e-9, $"local={piece.LocalModifier}");
    Check("mod_scope_global", Math.Abs(piece.GlobalModifier - 7.0) < 1e-9, $"global={piece.GlobalModifier}");
    Check("mod_scope_own", Math.Abs(piece.OwnModifier - 11.0) < 1e-9, $"own={piece.OwnModifier}");
}
```

- [ ] **Step 2: Rodar e verificar que falha**

Run: `dotnet run --project "<scratchpad>\SolverTests" -c Release`
Expected: erro de compilação — `ModScope` não existe.

- [ ] **Step 3: Implementar ModScope no Modifier**

Substituir o conteúdo de `VoyagePlannerData\Modifier.cs` por:

```csharp
namespace DeepwaterEngagementSuite.VoyagePlannerData;

public enum ModScope
{
    Adjacent,
    Voyage,
    Self,
}

public record Modifier(string Name, double Weight, ModScope Scope = ModScope.Adjacent);
```

- [ ] **Step 4: Somas por escopo no MapPiece**

Em `VoyagePlannerData\MapPiece.cs`, substituir as duas linhas de `GlobalModifier`/`LocalModifier` por:

```csharp
    public readonly double GlobalModifier = Modifiers.Where(x => x.Scope == ModScope.Voyage).Sum(x => x.Weight);
    public readonly double LocalModifier = Modifiers.Where(x => x.Scope == ModScope.Adjacent).Sum(x => x.Weight);
    public readonly double OwnModifier = Modifiers.Where(x => x.Scope == ModScope.Self).Sum(x => x.Weight);
```

- [ ] **Step 5: Atualizar call-sites em `DeepwaterEngagementSuite.Voyage.cs`**

(a) Na construção das peças dentro do `Solve` (método `ShowVoyageOptimizerWindow`, bloco `new Modifier(im.RawName, ...)`), trocar:

```csharp
return new Modifier(im.RawName, configuredWeight ?? 0, chartMod?.IsGlobal.Value ?? false);
```

por:

```csharp
return new Modifier(im.RawName, configuredWeight ?? 0,
    chartMod?.IsGlobal.Value == true ? ModScope.Voyage : ModScope.Adjacent);
```

(b) Na tabela "ScoreBreakdown" (`m.IsGlobal` no `modText`), trocar:

```csharp
var prefix = m.IsGlobal ? "[Global] " : "";
```

por:

```csharp
var prefix = m.Scope switch
{
    ModScope.Voyage => "[Global] ",
    ModScope.Self => "[Self] ",
    _ => "",
};
```

(c) No topo do arquivo, garantir o alias já existente `Direction = ...` e adicionar se necessário: `using DeepwaterEngagementSuite.VoyagePlannerData;` já está presente — nada a fazer.

- [ ] **Step 6: Rodar harness e build do plugin**

Run: `dotnet run --project "<scratchpad>\SolverTests" -c Release`
Expected: `ALL PASS` (baseline continua 24.0 — "Default" é Adjacent por default).

Run: `$env:exapiPackage = "C:\Users\bruno\Documents\Exile\PoEHelper"; dotnet build "<plugin>\DeepwaterEngagementSuite.csproj"`
Expected: `Build succeeded`.

- [ ] **Step 7: Commit**

```bash
git add VoyagePlannerData/Modifier.cs VoyagePlannerData/MapPiece.cs DeepwaterEngagementSuite.Voyage.cs
git commit -m "feat: mod scope (Adjacent/Voyage/Self) no modelo do voyage planner"
```

---

### Task 3: Termo de valor próprio no solver (score, upper bound, agrupamento)

**Files:**
- Modify: `VoyagePlanner.cs`
- Test: harness Program.cs (dois blocos novos)

**Interfaces:**
- Consumes: `MapPiece.OwnModifier` (Task 2).
- Produces: `CalculateScore` = Σ células `(Own(célula) + Σ Local(vizinhos) + Σ Global) × mult(célula)`. Agrupamento de peças considera OwnModifier. Upper bound usa `_maxOwnPerPiece`.

- [ ] **Step 1: Testes que falham — own vai para a célula de maior multiplicador**

Adicionar ao Program.cs:

```csharp
// --- own_value_placement: peça com Self forte vai para a célula de maior mult ---
{
    var pieces = Enumerable.Range(0, 9).Select(i => MakeCross(i)).ToList();
    pieces[0] = MakeCross(0, [new Modifier("Default", 1), new Modifier("SelfBig", 10, ModScope.Self)]);
    var mults = UniformMults(1.0);
    mults[0, 0] = 5.0;
    var best = SolveBest(pieces, mults);
    // base: 24 + 2*(5-1) = 32 (vizinhos do canto (0,0) pesados por 5.0); own: 10*5 = 50
    Check("own_value_score", best != null && Math.Abs(best.TotalScore - 82.0) < 1e-9, $"score={best?.TotalScore}");
    Check("own_value_position", best != null && best.Grid[0, 0].Piece.Id == 0,
        $"piece@0,0={best?.Grid[0, 0].Piece.Id}");
}

// --- own_value_groups: peças com Own diferente não podem ser agrupadas como iguais ---
{
    var pieces = Enumerable.Range(0, 9).Select(i => MakeCross(i)).ToList();
    pieces[0] = MakeCross(0, [new Modifier("Default", 1), new Modifier("SelfBig", 10, ModScope.Self)]);
    pieces[1] = MakeCross(1, [new Modifier("Default", 1), new Modifier("SelfMid", 5, ModScope.Self)]);
    var mults = UniformMults(1.0);
    mults[0, 0] = 5.0;
    mults[2, 2] = 3.0;
    var best = SolveBest(pieces, mults);
    // base: 24 + 2*(5-1) + 2*(3-1) = 36; own: 10*5 + 5*3 = 65 → 101
    Check("own_groups_score", best != null && Math.Abs(best.TotalScore - 101.0) < 1e-9, $"score={best?.TotalScore}");
}
```

- [ ] **Step 2: Rodar e verificar que falha**

Run: `dotnet run --project "<scratchpad>\SolverTests" -c Release`
Expected: compila, mas `FAIL own_value_score` (score sai 32.0 — own ignorado) e `FAIL own_groups_score` (36.0).

- [ ] **Step 3: Implementar no VoyagePlanner**

(a) Novo campo ao lado de `_maxModifierPerPiece`:

```csharp
    private double _maxModifierPerPiece;
    private double _maxOwnPerPiece;
```

(b) No `Solve`, logo após o cálculo de `_maxModifierPerPiece`, adicionar:

```csharp
        _maxOwnPerPiece = puzzle.AvailablePieces
            .Select(p => p.OwnModifier)
            .DefaultIfEmpty(0)
            .Max();
```

(c) Chave de agrupamento — trocar:

```csharp
        var groupMap = new Dictionary<(PieceType, Direction, double, double), int>();
```

por:

```csharp
        var groupMap = new Dictionary<(PieceType, Direction, double, double, double), int>();
```

e a montagem da chave:

```csharp
            var key = (p.Type, p.BaseConnections, globalWeight, localWeight, p.OwnModifier);
```

(d) `CalculateScore` — trocar a linha `var cellScore = globalSum;` por:

```csharp
                var cellScore = globalSum + _grid[r, c].Piece.OwnModifier;
```

(e) `CalculateUpperBoundScore` — no ramo de célula preenchida, trocar `var cellScore = 0.0;` por:

```csharp
                    var cellScore = _grid[i, j].Piece.OwnModifier;
```

e no ramo de célula vazia, trocar:

```csharp
                    score += (neighborCount * _maxModifierPerPiece + ubGlobalSum) * _puzzle.LocationModifiers[i, j];
```

por:

```csharp
                    score += (neighborCount * _maxModifierPerPiece + _maxOwnPerPiece + ubGlobalSum) * _puzzle.LocationModifiers[i, j];
```

- [ ] **Step 4: Rodar harness (tudo verde) + build do plugin**

Run: `dotnet run --project "<scratchpad>\SolverTests" -c Release`
Expected: `ALL PASS` (inclui baseline 24.0 intacto — Own default é 0).

Run: build do plugin (comando dos Global Constraints). Expected: `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add VoyagePlanner.cs
git commit -m "feat: termo de valor proprio (Self) no score do voyage solver"
```

---

### Task 4: Pesos de posição P[r,c] (helper + settings + profile + wiring)

**Files:**
- Create: `VoyagePlannerData\PositionWeightMap.cs`
- Modify: `DeepwaterEngagementSuiteSettings.cs` (classe `VoyageSettings`)
- Modify: `VoyageProfile.cs`
- Modify: `DeepwaterEngagementSuite.Profiles.cs` (`ApplyProfile`, `SyncCurrentProfileToMemory`)
- Modify: `DeepwaterEngagementSuite.Voyage.cs` (montagem do `tileMultiplierArray`)
- Modify: `profiles\default.json` (template)
- Test: harness Program.cs

**Interfaces:**
- Produces: `PositionWeightMap.ScreenToGrid(float[][] screenRows) → double[,]` (tela linha 0 = topo → grid row 0 = baixo); `VoyageSettings.PositionWeights` (`float[][]`, orientação de tela); `VoyageSettings.DefaultPositionWeights()`.
- Consumes: nada das tasks anteriores (independente do Own).

- [ ] **Step 1: Teste que falha — conversão tela→grid**

Adicionar ao Program.cs:

```csharp
// --- position_map: tela (linha 0 = topo) para grid (row 0 = baixo) ---
{
    float[][] screen = [[1.00f, 0.15f, 1.00f], [1.10f, 0.90f, 1.00f], [1.15f, 1.05f, 1.00f]];
    var grid = PositionWeightMap.ScreenToGrid(screen);
    Check("position_map_topmid", Math.Abs(grid[2, 1] - 0.15) < 1e-6, $"grid[2,1]={grid[2, 1]}");
    Check("position_map_spawn", Math.Abs(grid[0, 0] - 1.15) < 1e-6, $"grid[0,0]={grid[0, 0]}");
    Check("position_map_midleft", Math.Abs(grid[1, 0] - 1.10) < 1e-6, $"grid[1,0]={grid[1, 0]}");
}
```

- [ ] **Step 2: Rodar e verificar que falha** (não compila — `PositionWeightMap` não existe).

- [ ] **Step 3: Criar `VoyagePlannerData\PositionWeightMap.cs`**

```csharp
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
```

- [ ] **Step 4: Rodar harness — verde.**

- [ ] **Step 5: Settings + UI (3×3 sliders) em `VoyageSettings`**

Em `DeepwaterEngagementSuiteSettings.cs`, dentro de `VoyageSettings`:

(a) Novas propriedades (colocar após `ProfileRenameNode`):

```csharp
    public float[][] PositionWeights { get; set; } = DefaultPositionWeights();

    [JsonIgnore]
    public CustomNode PositionWeightsNode { get; set; }

    public static float[][] DefaultPositionWeights() =>
    [
        [1.00f, 0.15f, 1.00f],
        [1.10f, 0.90f, 1.00f],
        [1.15f, 1.05f, 1.00f],
    ];
```

(b) No construtor de `VoyageSettings`, adicionar:

```csharp
        PositionWeightsNode = new CustomNode
        {
            DrawDelegate = () =>
            {
                ImGui.TextUnformatted("Position weights (top row = topo do board no jogo)");
                for (var row = 0; row < 3; row++)
                {
                    for (var col = 0; col < 3; col++)
                    {
                        if (col > 0) ImGui.SameLine();
                        ImGui.PushID(row * 3 + col);
                        ImGui.SetNextItemWidth(100);
                        var v = PositionWeights[row][col];
                        if (ImGui.SliderFloat("##pw", ref v, 0f, 2f, "%.2f"))
                            PositionWeights[row][col] = v;
                        ImGui.PopID();
                    }
                }

                if (ImGui.Button("Reset position defaults"))
                    PositionWeights = DefaultPositionWeights();
            },
        };
```

- [ ] **Step 6: Persistência no profile**

(a) `VoyageProfile.cs`:

```csharp
using System.Collections.Generic;

namespace DeepwaterEngagementSuite;

public class VoyageProfile
{
    public List<VoyageBorderModifier> BorderModifiers { get; set; } = [];
    public List<VoyageChartModifier> ChartModifiers { get; set; } = [];
    public float[][] PositionWeights { get; set; }
}
```

(b) `DeepwaterEngagementSuite.Profiles.cs` — em `ApplyProfile`, após o loop de `ChartModifiers`, adicionar:

```csharp
        vs.PositionWeights = entry.Profile.PositionWeights is { Length: 3 } pw
            ? pw.Select(r => r.ToArray()).ToArray()
            : VoyageSettings.DefaultPositionWeights();
```

(c) Em `SyncCurrentProfileToMemory`, após o loop de `ChartModifiers`, adicionar:

```csharp
        entry.Profile.PositionWeights = Settings.VoyageSettings.PositionWeights.Select(r => r.ToArray()).ToArray();
```

(d) `profiles\default.json` (template) — adicionar no topo do objeto raiz:

```json
  "PositionWeights": [
    [1.00, 0.15, 1.00],
    [1.10, 0.90, 1.00],
    [1.15, 1.05, 1.00]
  ],
```

- [ ] **Step 7: Wiring no Solve (`DeepwaterEngagementSuite.Voyage.cs`)**

Trocar:

```csharp
                var tileMultiplierArray = new double[3, 3];
                foreach (var boardMultiplier in boardMultipliers)
                {
                    tileMultiplierArray[boardMultiplier.Key / 3, boardMultiplier.Key % 3] = boardMultiplier.Item2;
                }
```

por:

```csharp
                var tileMultiplierArray = PositionWeightMap.ScreenToGrid(Settings.VoyageSettings.PositionWeights);
                foreach (var boardMultiplier in boardMultipliers)
                {
                    tileMultiplierArray[boardMultiplier.Key / 3, boardMultiplier.Key % 3] *= boardMultiplier.Item2;
                }
```

Nota: quando `BorderMods.Count < 12`, `boardMultipliers` fica vazio — antes a matriz
ficava toda 0 (todo score = 0); agora degrada para "só P", o que é melhoria intencional.

- [ ] **Step 8: Rodar harness + build do plugin** — ambos verdes.

- [ ] **Step 9: Commit**

```bash
git add VoyagePlannerData/PositionWeightMap.cs DeepwaterEngagementSuiteSettings.cs VoyageProfile.cs DeepwaterEngagementSuite.Profiles.cs DeepwaterEngagementSuite.Voyage.cs profiles/default.json
git commit -m "feat: pesos por posicao do board (P[r,c]) configuraveis por profile"
```

---

### Task 5: Seletor de escopo nos ChartModifiers + fallback IsGlobal

**Files:**
- Modify: `DeepwaterEngagementSuiteSettings.cs` (classe `VoyageChartModifier`)
- Modify: `DeepwaterEngagementSuite.Voyage.cs` (usar `EffectiveScope` nos dois pontos que hoje leem `IsGlobal.Value`)

**Interfaces:**
- Produces: `VoyageChartModifier.EffectiveScope` (`ModScope`; `Scope.Value` vazio → cai para `IsGlobal`); `VoyageChartModifier.Scope` (`TextNode`, valores "Adjacent"/"Voyage"/"Self", serializado no profile pelo `TextNodeConverter` existente).
- Consumes: `ModScope` (Task 2).

- [ ] **Step 1: Substituir a classe `VoyageChartModifier` inteira**

```csharp
[Submenu(CollapsedByDefault = true)]
public class VoyageChartModifier
{
    internal static readonly string[] ScopeValues = ["Adjacent", "Voyage", "Self"];

    public VoyageChartModifier()
    {
        ScopeSelector = new CustomNode
        {
            DrawDelegate = () =>
            {
                var current = EffectiveScope.ToString();
                if (ImGui.BeginCombo("Scope", current))
                {
                    foreach (var v in ScopeValues)
                    {
                        if (ImGui.Selectable(v, v == current))
                            Scope.Value = v;
                    }

                    ImGui.EndCombo();
                }
            },
        };
    }

    public TextNode Id { get; set; } = new TextNode("");
    public RangeNode<float> Weight { get; set; } = new RangeNode<float>(0, 0, 100);

    // Legado: mantido para migração de perfis antigos; escondido do menu.
    [IgnoreMenu]
    public ToggleNode IsGlobal { get; set; } = new ToggleNode(false);

    [IgnoreMenu]
    public TextNode Scope { get; set; } = new TextNode("");

    [JsonIgnore]
    public CustomNode ScopeSelector { get; set; }

    public ColorNode HighlightColor { get; set; } = Color.Violet;

    [JsonIgnore]
    public VoyagePlannerData.ModScope EffectiveScope => Scope.Value switch
    {
        "Voyage" => VoyagePlannerData.ModScope.Voyage,
        "Self" => VoyagePlannerData.ModScope.Self,
        "Adjacent" => VoyagePlannerData.ModScope.Adjacent,
        _ => IsGlobal.Value ? VoyagePlannerData.ModScope.Voyage : VoyagePlannerData.ModScope.Adjacent,
    };

    public override string ToString()
    {
        return $"{Id.Value} {Weight.Value} {EffectiveScope}###";
    }
}
```

Adicionar `using DeepwaterEngagementSuite.VoyagePlannerData;` NÃO — usar o nome
qualificado `VoyagePlannerData.ModScope` como acima (o arquivo de settings não tem
esse using e adicioná-lo cria ambiguidade com `Direction` do GameOffsets).

- [ ] **Step 2: Usar EffectiveScope em `DeepwaterEngagementSuite.Voyage.cs`**

(a) Na construção de peças (Task 2 deixou o mapeamento por IsGlobal), trocar:

```csharp
return new Modifier(im.RawName, configuredWeight ?? 0,
    chartMod?.IsGlobal.Value == true ? ModScope.Voyage : ModScope.Adjacent);
```

por:

```csharp
return new Modifier(im.RawName, configuredWeight ?? 0, chartMod?.EffectiveScope ?? ModScope.Adjacent);
```

(b) No desenho dos implicits por tile (`DrawVoyageHighlights`), trocar:

```csharp
var prefix = chartMod?.IsGlobal.Value == true ? "[G] " : "";
```

por:

```csharp
var prefix = chartMod?.EffectiveScope switch
{
    ModScope.Voyage => "[G] ",
    ModScope.Self => "[S] ",
    _ => "",
};
```

- [ ] **Step 3: Build do plugin** — `Build succeeded`.

- [ ] **Step 4: Commit**

```bash
git add DeepwaterEngagementSuiteSettings.cs DeepwaterEngagementSuite.Voyage.cs
git commit -m "feat: seletor de escopo (Adjacent/Voyage/Self) nos chart modifiers com fallback IsGlobal"
```

---

### Task 6: Biome weights + explicit mods alimentando o valor próprio

**Files:**
- Modify: `DeepwaterEngagementSuiteSettings.cs` (nova classe `BiomeWeightSetting` + ContentNode em `VoyageSettings`)
- Modify: `VoyageProfile.cs` (lista `BiomeWeights`)
- Modify: `DeepwaterEngagementSuite.Profiles.cs` (Apply/Sync)
- Modify: `DeepwaterEngagementSuite.Voyage.cs` (construção de peças; fila de biomas desconhecidos)
- Modify: `profiles\default.json` (template)

**Interfaces:**
- Produces: `BiomeWeightSetting { TextNode Id; RangeNode<float> Weight }`; `VoyageSettings.BiomeWeights` (`ContentNode<BiomeWeightSetting>`); peças passam a incluir `Modifier("Biome:<Id>", peso, ModScope.Self)` e explicits com lookup em `ChartModifiers` (default `Self`).
- Consumes: `ModScope`/`Modifier` (Task 2), `EffectiveScope` (Task 5).

- [ ] **Step 1: `BiomeWeightSetting` + ContentNode**

Em `DeepwaterEngagementSuiteSettings.cs`, adicionar após a classe `VoyageBorderModifier`:

```csharp
[Submenu(CollapsedByDefault = true)]
public class BiomeWeightSetting
{
    public TextNode Id { get; set; } = new TextNode("");
    public RangeNode<float> Weight { get; set; } = new RangeNode<float>(0, 0, 100);

    public override string ToString()
    {
        return $"{Id.Value} {Weight.Value}###";
    }
}
```

Em `VoyageSettings`, após o bloco de `ChartModifiers`, adicionar:

```csharp
    [Menu(null, CollapsedByDefault = true)]
    [JsonIgnore]
    public ContentNode<BiomeWeightSetting> BiomeWeights { get; set; } = new ContentNode<BiomeWeightSetting>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new BiomeWeightSetting(),
        ItemFilter = (o, s) => o.Id.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };
```

- [ ] **Step 2: Profile (VoyageProfile + Apply/Sync)**

(a) `VoyageProfile.cs` — adicionar:

```csharp
    public List<BiomeWeightSetting> BiomeWeights { get; set; } = [];
```

(b) `ApplyProfile` — adicionar após o bloco de PositionWeights (Task 4):

```csharp
        vs.BiomeWeights.Content.Clear();
        foreach (var bw in entry.Profile.BiomeWeights ?? [])
        {
            vs.BiomeWeights.Content.Add(bw);
        }
```

(c) `SyncCurrentProfileToMemory` — adicionar:

```csharp
        entry.Profile.BiomeWeights ??= [];
        entry.Profile.BiomeWeights.Clear();
        foreach (var bw in Settings.VoyageSettings.BiomeWeights.Content)
        {
            entry.Profile.BiomeWeights.Add(bw);
        }
```

(d) Template `profiles\default.json` — adicionar chave no objeto raiz (Ids são chute;
o auto-add do Step 3 descobre os reais em runtime):

```json
  "BiomeWeights": [
    { "Id": "SeafloorRidges", "Weight": 15.0 },
    { "Id": "AbyssalPlain", "Weight": 12.0 },
    { "Id": "UnderseaGroves", "Weight": 10.0 }
  ],
```

- [ ] **Step 3: Construção de peças com explicits + bioma + auto-add**

Em `DeepwaterEngagementSuite.Voyage.cs`:

(a) Campo novo no topo da classe parcial (junto de `_result` etc.):

```csharp
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _unknownBiomes = new();
```

(b) Substituir o bloco inteiro de construção do `MapPiece` (de `var rotation = ...` até `pieces.Add(mp);`) por:

```csharp
                        var rotation = ((Direction)c.Room.Path);
                        var itemMods = chart.Item.GetComponent<Mods>();
                        var modifiers = new List<Modifier> { new("Default", 1) };

                        void AddItemMods(IEnumerable<ItemMod> source, ModScope defaultScope)
                        {
                            foreach (var im in source ?? [])
                            {
                                var chartMod = Settings.VoyageSettings.ChartModifiers.Content
                                    .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
                                modifiers.Add(new Modifier(im.RawName, chartMod?.Weight.Value ?? 0,
                                    chartMod?.EffectiveScope ?? defaultScope));
                            }
                        }

                        AddItemMods(itemMods?.ImplicitMods, ModScope.Adjacent);
                        AddItemMods(itemMods?.ExplicitMods, ModScope.Self);

                        var biomeId = c.Room?.Biome?.Id;
                        if (!string.IsNullOrEmpty(biomeId))
                        {
                            var biome = Settings.VoyageSettings.BiomeWeights.Content
                                .FirstOrDefault(b => b.Id.Value.Equals(biomeId, StringComparison.OrdinalIgnoreCase));
                            if (biome == null)
                            {
                                _unknownBiomes.Enqueue(biomeId);
                            }

                            modifiers.Add(new Modifier($"Biome:{biomeId}", biome?.Weight.Value ?? 0, ModScope.Self));
                        }

                        var mp = new MapPiece(i,
                            int.PopCount((int)rotation) switch
                            {
                                4 => PieceType.Cross,
                                3 => PieceType.Tee,
                                1 => PieceType.Single,
                                2 => rotation.HasFlag(Direction.Left) == rotation.HasFlag(Direction.Right)
                                    ? PieceType.Straight
                                    : PieceType.Corner
                            }, rotation, modifiers);
                        pieces.Add(mp);
```

(c) Drenar a fila no render thread — no início de `DrawVoyageHighlights`, logo após o
`TaskUtils.RunOrRestart(...)`:

```csharp
        while (_unknownBiomes.TryDequeue(out var newBiomeId))
        {
            if (!Settings.VoyageSettings.BiomeWeights.Content
                    .Any(b => b.Id.Value.Equals(newBiomeId, StringComparison.OrdinalIgnoreCase)))
            {
                Settings.VoyageSettings.BiomeWeights.Content.Add(new BiomeWeightSetting
                {
                    Id = new TextNode(newBiomeId),
                });
            }
        }
```

- [ ] **Step 4: Build do plugin** — `Build succeeded`.

- [ ] **Step 5: Commit**

```bash
git add DeepwaterEngagementSuiteSettings.cs VoyageProfile.cs DeepwaterEngagementSuite.Profiles.cs DeepwaterEngagementSuite.Voyage.cs profiles/default.json
git commit -m "feat: peso de bioma + explicit mods no valor proprio do chart"
```

---

### Task 7: Reroll advisor

**Files:**
- Create: `RerollAdvisor.cs`
- Modify: `DeepwaterEngagementSuiteSettings.cs` (`VoyageSettings`: threshold + toggle)
- Modify: `DeepwaterEngagementSuite.Voyage.cs` (baseline solve + detecção + UI)
- Modify: `DeepwaterEngagementSuite.cs` (`AreaChange`: reset)
- Modify: `<scratchpad>\SolverTests\SolverTests.csproj` (+ `RerollAdvisor.cs`)
- Test: harness Program.cs

**Interfaces:**
- Produces: `RerollAdvisor.NextCost(int rerollsDone) → long` (3000 × 2ⁿ); `RerollAdvisor.ShouldKeep(double ratio, double keepThreshold) → bool`; `RerollAdvisor.BuildBaselineMultipliers(double avg, double[,] positionWeights, IReadOnlyList<int> borderModCountPerTile) → double[,]`; `RerollAdvisor.BorderModCountPerTile` (int[9] = `[2,1,2,1,0,1,2,1,2]`, mesmo layout de `GetTileMods`).
- Consumes: `PositionWeightMap.ScreenToGrid` (Task 4), `VoyagePlanner` (Task 3).

- [ ] **Step 1: Teste que falha**

Adicionar ao Program.cs:

```csharp
// --- reroll_advisor: custos, recomendacao e baseline ---
{
    Check("reroll_cost0", RerollAdvisor.NextCost(0) == 3000);
    Check("reroll_cost2", RerollAdvisor.NextCost(2) == 12000);
    Check("reroll_keep", RerollAdvisor.ShouldKeep(1.2, 1.0));
    Check("reroll_reroll", !RerollAdvisor.ShouldKeep(0.8, 1.0));

    var p = new double[3, 3];
    for (var r = 0; r < 3; r++)
        for (var c = 0; c < 3; c++)
            p[r, c] = 1.0;
    var baseline = RerollAdvisor.BuildBaselineMultipliers(2.0, p, RerollAdvisor.BorderModCountPerTile);
    Check("reroll_baseline_corner", Math.Abs(baseline[0, 0] - 4.0) < 1e-9, $"corner={baseline[0, 0]}");
    Check("reroll_baseline_edge", Math.Abs(baseline[0, 1] - 2.0) < 1e-9, $"edge={baseline[0, 1]}");
    Check("reroll_baseline_center", Math.Abs(baseline[1, 1] - 1.0) < 1e-9, $"center={baseline[1, 1]}");
}
```

E no `SolverTests.csproj`, adicionar ao ItemGroup:

```xml
    <Compile Include="$(PluginDir)\RerollAdvisor.cs" />
```

- [ ] **Step 2: Rodar e verificar que falha** (não compila — arquivo não existe).

- [ ] **Step 3: Criar `RerollAdvisor.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace DeepwaterEngagementSuite;

public static class RerollAdvisor
{
    public const int BaseCost = 3000;

    /// <summary>Contagem de border mods por tile index (0..8) — mesmo layout de GetTileMods.</summary>
    public static readonly int[] BorderModCountPerTile = [2, 1, 2, 1, 0, 1, 2, 1, 2];

    public static long NextCost(int rerollsDone) => BaseCost * (1L << rerollsDone);

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
```

- [ ] **Step 4: Rodar harness — verde.**

- [ ] **Step 5: Settings do advisor**

Em `VoyageSettings` (`DeepwaterEngagementSuiteSettings.cs`), após `ChartHighlightThreshold`:

```csharp
    public ToggleNode ShowRerollAdvisor { get; set; } = new ToggleNode(true);

    [Menu("Reroll keep threshold", "R = melhor score atual / score com borders medios. Abaixo disso o advisor recomenda reroll.")]
    public RangeNode<float> RerollKeepThreshold { get; set; } = new RangeNode<float>(1.0f, 0f, 3f);
```

- [ ] **Step 6: Estado + baseline solve + detecção + UI em `DeepwaterEngagementSuite.Voyage.cs`**

(a) Campos novos no topo da classe parcial:

```csharp
    private int _rerollCount;
    private string _lastBorderKey;
    private double? _baselineScore;
    private string _baselineKey;
```

(b) Dentro do `Task.Run` do botão Solve, após o `foreach (var r in _voyagePlanner.Solve(...))` e antes do bloco `if (_voyageStopwatch...)`, adicionar o baseline (cacheado por pool de peças + P):

```csharp
                var piecesKey = string.Join("|", pieces.Select(p =>
                    $"{p.Id}:{p.Type}:{(int)p.BaseConnections}:{p.OwnModifier:F2}:{p.LocalModifier:F2}:{p.GlobalModifier:F2}"));
                var positionWeights = PositionWeightMap.ScreenToGrid(Settings.VoyageSettings.PositionWeights);
                if (piecesKey != _baselineKey)
                {
                    var avg = Settings.VoyageSettings.BorderModifiers.Content
                        .Select(b => (double)b.ValueMultiplier.Value)
                        .DefaultIfEmpty(1)
                        .Average();
                    var baselineMults = RerollAdvisor.BuildBaselineMultipliers(avg, positionWeights, RerollAdvisor.BorderModCountPerTile);
                    var baselinePlanner = new VoyagePlanner();
                    double baselineScore = 0;
                    foreach (var br in baselinePlanner.Solve(new VoyagePuzzle(pieces, baselineMults, []),
                                 new VoyagePlannerSettings(TopN: 1, TimeLimitSeconds: timeLimitSetting)))
                    {
                        baselineScore = br.Solutions.FirstOrDefault()?.TotalScore ?? baselineScore;
                    }

                    _baselineScore = baselineScore;
                    _baselineKey = piecesKey;
                }
```

(c) Detecção de reroll — em `DrawVoyageHighlights`, logo após `var modsPerTileIndex = GetTileMods(tree);`:

```csharp
        var borderKey = string.Join("|", tree.Data.BorderMods.Select(m => m.RawName));
        if (!string.IsNullOrEmpty(borderKey) && _lastBorderKey != null && borderKey != _lastBorderKey)
        {
            _rerollCount++;
        }

        if (!string.IsNullOrEmpty(borderKey))
        {
            _lastBorderKey = borderKey;
        }
```

(d) UI — em `ShowVoyageOptimizerWindow`, logo após a linha `ImGui.Text($"Nodes: ...")`:

```csharp
        if (Settings.VoyageSettings.ShowRerollAdvisor.Value &&
            _result is { Solutions.Count: > 0 } &&
            _baselineScore is > 0)
        {
            var ratio = _result.Solutions[0].TotalScore / _baselineScore.Value;
            var keep = RerollAdvisor.ShouldKeep(ratio, Settings.VoyageSettings.RerollKeepThreshold.Value);
            ImGui.TextColored((keep ? Color.LightGreen : Color.OrangeRed).ToImguiVec4(),
                keep
                    ? $"Borders: R={ratio:F2} — KEEP"
                    : $"Borders: R={ratio:F2} — REROLL (próximo: {RerollAdvisor.NextCost(_rerollCount):N0} sulphur)");

            int? sulphur = null;
            try
            {
                sulphur = GameController.IngameState.ServerData.DeepwaterHandler?.Sulphur;
            }
            catch
            {
                // fora de contexto deepwater o handler pode não estar legível
            }

            if (sulphur != null)
            {
                ImGui.SameLine();
                ImGui.Text($"(sulphur: {sulphur:N0})");
            }

            ImGui.Text($"Rerolls nesta board: {_rerollCount}");
            ImGui.SameLine();
            if (ImGui.SmallButton("+")) _rerollCount++;
            ImGui.SameLine();
            if (ImGui.SmallButton("-")) _rerollCount = Math.Max(0, _rerollCount - 1);
            ImGui.SameLine();
            if (ImGui.SmallButton("reset")) _rerollCount = 0;
        }
```

(e) Reset — em `DeepwaterEngagementSuite.cs`, dentro de `AreaChange`, adicionar ao final:

```csharp
        _rerollCount = 0;
        _lastBorderKey = null;
        _baselineScore = null;
        _baselineKey = null;
```

- [ ] **Step 7: Rodar harness + build do plugin** — ambos verdes.

- [ ] **Step 8: Commit**

```bash
git add RerollAdvisor.cs DeepwaterEngagementSuiteSettings.cs DeepwaterEngagementSuite.Voyage.cs DeepwaterEngagementSuite.cs
git commit -m "feat: reroll advisor de border mods (baseline solve + custo 3000*2^n)"
```

---

### Task 8: Validação final (build completo, suite, in-game)

**Files:**
- Nenhum novo; correções pontuais se a validação achar problemas.

- [ ] **Step 1: Suite completa do harness**

Run: `dotnet run --project "<scratchpad>\SolverTests" -c Release`
Expected: `ALL PASS` (baseline 24.0, escopos, own placement 82/101, position map, reroll advisor).

- [ ] **Step 2: Build limpo do plugin**

Run: comando de build dos Global Constraints.
Expected: `Build succeeded`, 0 warnings novos relevantes.

- [ ] **Step 3: Pedir Reload Plugins ao usuário e validar in-game (checklist)**

1. **Orientação de P**: os tiles têm label "(r,c)" desenhado; o tile de spawn
   (bottom-left na tela) deve mostrar `(0,0)`. Se mostrar `(2,0)`, inverter a
   conversão em `PositionWeightMap.ScreenToGrid` (trocar `grid[2 - s, c]` por
   `grid[s, c]`) — é o único ponto de flip.
2. **Biomas**: abrir o Plan Voyage com charts no estoque, clicar Solve, e conferir
   no menu `VoyageSettings → BiomeWeights` que os Ids reais dos biomas foram
   auto-adicionados; preencher pesos (15/12/10 conforme spec) se os Ids do
   template não bateram.
3. **Score**: com os defaults, o Solve deve colocar o pior chart no top-middle
   (tile `(2,1)`), e charts de bioma forte/6-mod nos cantos com borders bons.
4. **Advisor**: linha "Borders: R=... KEEP/REROLL" aparece após Solve; usar o
   compass uma vez e conferir que o contador vai a 1 e o custo mostra 6.000.
5. **Perfis**: trocar de profile e voltar; PositionWeights/BiomeWeights/Scope
   persistem (conferir o JSON em `config/DeepwaterEngagementSuite/profiles/`).

- [ ] **Step 4: Commit final (se houve correções) e atualizar o spec se algo divergiu**

```bash
git add -A
git commit -m "fix: ajustes da validacao in-game do voyage score v2"
```

---

## Self-Review (executado na escrita do plano)

- **Spec coverage:** fórmula nova (T3), escopos (T2/T5), bioma+explicits (T6),
  P[r,c] + UI + profile (T4), reroll advisor com baseline/custo/contador/reset (T7),
  migração IsGlobal→Scope sem quebrar perfis antigos (T5, via EffectiveScope),
  tratamento de erros (biome nulo → peso 0 + auto-add; borders <12 → advisor oculto
  pois `_baselineScore is > 0` e board degrada para P-only), verificação (T1, T8). ✓
- **Placeholders:** nenhum TBD; todo step de código tem o código. ✓
- **Type consistency:** `OwnModifier` (T2) usado em T3/T7; `EffectiveScope` (T5)
  usado em T6; `PositionWeightMap.ScreenToGrid` (T4) usado em T7;
  `BorderModCountPerTile` definido e consumido em T7. ✓
