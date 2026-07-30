# Referência: estratégias da comunidade (one-more-map.github.io/allflame-voyage-solver)

Data: 2026-07-30. Extraído do bundle JS do solver comunitário (dados numéricos exatos).
Fontes citadas por eles: Milkybk_ (YouTube "Allflame Buffs and My Strategy") e cutedog_ (Twitch).

## Validações do nosso modelo (defaults do solver deles)

- `adjacencyMode: "physical"` — mods adjacentes alcançam QUALQUER vizinho ortogonal
  (não exige conexão de caminho). Igual ao nosso.
- `adjacentAffectsSelf: false` — mod adjacente NÃO afeta o próprio tile. Igual ao nosso.
- Conectores: default "strict" (devem casar — regra real), com modo experimental ignorando.
- Vocabulário de pesos deles: `adjacent:X`, `voyage:X`, `self:X`, `border:X` — **eles têm
  escopo self** (quant/pack/rarity/sulph próprios do chart) igual ao nosso Self.
- Board deles: indexação row-major com topo-esquerda = 0 (⚓ start no canto inferior).

## As 6 estratégias (pesos e regras posicionais exatos)

### 1. Alc & Go (lixo útil)
- Queima os charts que NENHUMA outra estratégia quer (sistema de reserva!): forma
  "one-lane highways" (3 pistas ligadas por baixo). Sulphur + encontros aleatórios.
- Pesos: self:quant 2, self:sulph 2, voyage:sulph 2, voyage:quant 2. layoutPenalty 15.
- Reserva (não queimar): todos os juice pieces + boxes de centro + "pillar"/"pelagic".

### 2. Speedrun Strongboxes (Milky)
- **UM Operative no CENTRO** (regra: centro opbox +55; boxes FORA do centro −40!);
  Diviner/Message fallback. "A few divines of scarabs per run".
- **Rolar charts para 110%+ quant ANTES de rodar — não dá para rolar depois**; charts de
  maior quant nas 4 LATERAIS; cantos = lixo só para conectar.
- Levar Alchemy/Scouring/Exalted para juicar as boxes antes de abrir.
- **Border "Filthscrabble" = boss de ~4.000 sulphur → pinar o chart de mais sulphur nele.**
- Reserva: nunca queima Starfish/Pantheon/Lantern/Possessed/Fracture/Rares/No-Equip/
  Wisp/Magic/Strongbox/Sea-Pillar (peças das outras estratégias).
- Pesos: adjacent:opbox 10, divbox 7, msg 7, self:quant 8, voyage:quant 5, sulph 3/3,
  border:quantconn 6, border:divine 4, exalt 3, ancient 3.
- Requisito: 1× box/msg chart (centro). Regex: `"bottle|divine|oper"`.

### 3. Meatfish (Milky — o principal)
- Composição: 2× Starfish, 1× Pantheon (ou 4k Wisps), 2× Sea-Pillars, 2× Golden
  Lanterns, 1× Possessed, 1× No-Equipment (fallback Rares Fracture).
- Posições (regras ±80): Starfish SÓ topo-meio/baixo-meio; Pantheon SÓ meio-direita;
  GL centro (+40); Pillars cantos (+40, fora deles −40).
- Coletar TODOS os lanterns: ≈280% quant, 840 rarity → uniques (MB/HH).
- Pesos: adjacent:star 10, pantheon 10, lantern 10, voyage:possess 10, fracture 8,
  rare 8, adjacent:rare 6, border:rare 9, self:quant 4, self:rarity 3.
- All-or-nothing; sem peças → Speedrun. Regex: `"cannot|poss|lantern|pantheon"`.

### 4. Magic Ethereal (Milky — deprecado)
- ⚠ Reportes fracos (~5 div, Palsteron); Milky migrou para Meatfish. Referência.
- Layout exato: 3 Corners, 4 T, 1 Cross, 1 Straight = 11 conexões; Wisps nas laterais,
  GL nos cantos, Cross no centro; "at least Magic" + increased Magic; usa Infested
  Bathysphere (mais monstros para converter); No-Equipment pesa MAIS que no Meatfish.

### 5. Divine Border Rares (Milky)
- Reroll até o border "+1 Divine Orb per Rare" (um dos DOIS jackpots reais da mecânica).
- **Sea-Pillar PINADO no tile do border Divine** (+100) — os pilares chovem rares ali.
- Feeders: charts "+5 Strongboxes" adjacentes (+35/+22); rolar as boxes spawnadas para
  "Stream of Monsters" (+4 rares) e "of Rarity" (+3) → **7 rares/box = 7 divines/box;
  um chart +5 ≈ 35 div; três em volta ≈ 105 div**. Starfish como feeder reserva.
- 5× Increased Rares no resto. Requisitos: 1× Pillar, 3× Starfish/Box, 5× IncRares +
  border Divine rolado. Regex: `"rare monsters in all voy|strongbox"`.

### 6. Divine Strongboxes (cutedog_)
- Variante: **Pelagic Abyss com pack size ALTO no tile do Divine** (+80; bonus por
  packsize per 8), 3× charts de strongbox de QUALQUER tipo adjacentes (+25).
- Prefixos das boxes: "3 additional Rares" = 3 div, "Stream of Monsters" = 4; ambos = 7.
- Resto do board: voyage-wide Increased Rares. Comprar charts baratos no trade
  (whisper "fastge"), regex de 120%+ quant: `"m q.*(1[2-9].|[2-9]..)%"`.
- Trade search deles: pathofexile.com/trade/search/Allflame/9zRn7YLRHK

## Mecanismos do solver deles que valem adotar na integração

- **Sistema de reserva entre estratégias**: cada estratégia declara quais mods/salas NÃO
  pode queimar (pertencem a outras) — o Alc&Go/Speedrun só usam o refugo.
- **Regras posicionais** (cell-indexed bonuses ±N) e **pins por border**
  (`nearBorderId: b-divine` + nameMatch/modIds) — além dos pesos por categoria.
- **rewardStat rules**: bônus proporcionais a stat do chart (quantity per 6, packsize
  per 8, sulphur per 8 no tile do Filthscrabble/octoboss).
- **requiresBorderId** (gate de border por estratégia) + requirements com counts +
  waitHint ("Speedrun Strongboxes in the meantime") + searchRegex por estratégia.
- Gerador de regex de busca in-game a partir dos pesos ("Best-Charts Regex").
