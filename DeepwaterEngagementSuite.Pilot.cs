using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // "Voyage Pilot": painel in-run com o comportamento da estratégia ativa + seta para
    // o próximo objetivo. Prioridades por tipo de marker, com overrides por estratégia.
    private static readonly Dictionary<string, int> PilotBasePriority = new()
    {
        ["Unrevealed"] = 80,
        ["CurrencyTreasureChest"] = 70,
        ["BottledItemChest"] = 70,
        ["ScarabChest"] = 65,
        ["StackedDecksChest"] = 65,
        ["MapsChest"] = 60,
        ["AllflameEmbersChest"] = 55,
        ["LanternReplenishEncounter"] = 55,
        ["CursedDucatDrop"] = 45,
        ["IzaroObject"] = 40,
        ["GoldTreasureChest"] = 35,
        ["AltarCrab"] = 35,
        ["AltarOctopus"] = 35,
        ["ClamTreasureChest"] = 30,
        ["UniqueWeaponChest"] = 30,
        ["UniqueArmourChest"] = 30,
    };

    private static readonly Dictionary<string, Dictionary<string, int>> PilotStrategyOverrides = new()
    {
        ["Speedrun"] = new Dictionary<string, int>
        {
            ["CurrencyTreasureChest"] = 100,
            ["BottledItemChest"] = 95,
            ["ScarabChest"] = 90,
            ["StackedDecksChest"] = 90,
            ["Unrevealed"] = 85,
            ["MapsChest"] = 80,
            ["GoldTreasureChest"] = 20,
            ["ClamTreasureChest"] = 15,
        },
        ["Meatfish"] = new Dictionary<string, int>
        {
            ["LanternReplenishEncounter"] = 100,
            ["AllflameEmbersChest"] = 70,
        },
        ["DivineBorder"] = new Dictionary<string, int>
        {
            ["Unrevealed"] = 50,
        },
    };

    private void DrawVoyagePilot()
    {
        var settings = Settings.PilotSettings;
        if (!settings.ShowPilotPanel)
        {
            return;
        }

        if (_cachedEntities.Count == 0 && _pointerEntities.Count == 0)
        {
            return;
        }

        var strategyName = _activeStrategy?.Name ?? "Base";

        // Barco e fundo do mar são a MESMA área: "fora do campo" = fora de todas as
        // bolhas de ar do handler (no barco o jogador fica longe de qualquer bolha).
        var inField = true;
        try
        {
            var bubbles = Bubbles;
            if (bubbles is { Count: > 0 })
            {
                inField = bubbles.Any(b =>
                    Vector2.Distance(_playerGridPos, new Vector2(b.Position.X, b.Position.Y)) <= b.Radius * 1.5f);
            }
        }
        catch
        {
            // sem dados de bolhas, não esconder nada
        }

        if (!inField)
        {
            if (ImGui.Begin("Voyage Pilot", ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextColored(Color.Gray.ToImguiVec4(),
                    $"{strategyName} - fora das bolhas (no barco?). Sem objetivos ativos.");
            }

            ImGui.End();
            return;
        }

        var overrides = PilotStrategyOverrides.GetValueOrDefault(strategyName);

        int Priority(string kind) =>
            overrides?.GetValueOrDefault(kind, PilotBasePriority.GetValueOrDefault(kind, 10))
            ?? PilotBasePriority.GetValueOrDefault(kind, 10);

        // Objetivos: markers conhecidos (não abertos), alvos unrevealed e (fase de kill) rares vivos.
        var objectives = new List<(string Label, Vector2 Pos, int Prio)>();
        var kindCounts = new Dictionary<string, int>();
        foreach (var cached in _cachedEntities.Values)
        {
            var kind = GetChestType(cached.Path).ToString();
            kindCounts[kind] = kindCounts.GetValueOrDefault(kind) + 1;
            objectives.Add((kind, cached.GridPos, Priority(kind)));
        }

        var seenTargets = new HashSet<(int, int)>();
        foreach (var e in _pointerEntities.Values.ToList())
        {
            if (e is not { IsValid: true })
            {
                continue;
            }

            foreach (var target in ReadPointerTargets(e, out _))
            {
                if (!seenTargets.Add(((int)(target.X / 20), (int)(target.Y / 20))) ||
                    ResolveTargetLabel(target) != null)
                {
                    continue;
                }

                kindCounts["Unrevealed"] = kindCounts.GetValueOrDefault("Unrevealed") + 1;
                objectives.Add(("Unrevealed", target, Priority("Unrevealed")));
            }
        }

        var lanternsLeft = kindCounts.GetValueOrDefault("LanternReplenishEncounter");
        var meatfishKillPhase = strategyName == "Meatfish" && lanternsLeft == 0;
        if (strategyName == "DivineBorder" || meatfishKillPhase)
        {
            try
            {
                foreach (var monster in Handler?.Monsters ?? [])
                {
                    if (monster is { IsValid: true, IsAlive: true } &&
                        monster.Rarity is MonsterRarity.Rare or MonsterRarity.Unique)
                    {
                        objectives.Add(($"{monster.Rarity}", monster.GridPosNum,
                            strategyName == "DivineBorder" ? 95 : 75));
                    }
                }
            }
            catch
            {
                // lista de monstros pode falhar em transição de área
            }
        }

        var next = objectives
            .OrderByDescending(o => o.Prio)
            .ThenBy(o => Vector2.Distance(_playerGridPos, o.Pos))
            .Cast<(string Label, Vector2 Pos, int Prio)?>()
            .FirstOrDefault();

        // Seta para o próximo objetivo (mundo + mapa).
        if (settings.ShowObjectiveArrow && next is { } obj)
        {
            var color = settings.ObjectiveColor.Value;
            var playerScreen = GetWorldScreenPosition(_playerGridPos);
            var targetScreen = GetWorldScreenPosition(obj.Pos);
            if (IsRoughlyOnScreen(playerScreen) || IsRoughlyOnScreen(targetScreen))
            {
                Graphics.DrawLine(playerScreen, targetScreen, 3, color);
                Graphics.DrawTextWithBackground(
                    $"> {obj.Label} ({Vector2.Distance(_playerGridPos, obj.Pos):F0})",
                    targetScreen + new Vector2(0, -18), color, Color.Black);
            }

            if (_largeMapOpen)
            {
                Graphics.DrawLine(Graphics.GridToMap(_playerGridPos, _playerGridPos),
                    Graphics.GridToMap(obj.Pos, _playerGridPos), 2, color);
            }
        }

        // Painel.
        if (!ImGui.Begin("Voyage Pilot", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var elapsed = DateTime.UtcNow - _statsAreaStart;
        var extractLimit = strategyName switch
        {
            "Speedrun" => settings.SpeedrunExtractMinutes.Value,
            "Meatfish" => settings.MeatfishExtractMinutes.Value,
            _ => 0,
        };
        var overTime = extractLimit > 0 && elapsed.TotalMinutes > extractLimit;
        ImGui.TextColored((overTime ? Color.OrangeRed : Color.LightGreen).ToImguiVec4(),
            $"{strategyName}  {(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}" +
            (extractLimit > 0 ? $" / {extractLimit}min{(overTime ? "  EXTRAIA!" : "")}" : ""));

        var behavior = strategyName switch
        {
            "Speedrun" => "Abra boxes/eventos, ignore trash. Extraia no limite.",
            "Meatfish" when !meatfishKillPhase => $"FASE 1: colete os lanterns ({lanternsLeft} restantes)",
            "Meatfish" => "FASE 2: full clear de rares/uniques. Extraia por ultimo.",
            "DivineBorder" => "Full-clear de rares na regiao do border Divine.",
            _ => "Siga os marcadores; abra o que tiver valor.",
        };
        ImGui.TextUnformatted(behavior);

        int? sulphur = null;
        try
        {
            sulphur = Handler?.Sulphur;
        }
        catch
        {
            // fora de contexto deepwater
        }

        var goldenLanternStacks = 0;
        try
        {
            goldenLanternStacks = GameController.Player?.GetComponent<Buffs>()?.BuffsList?
                .Where(b => b?.Name == "deepwater_golden_lantern")
                .Sum(b => b.Charges) ?? 0;
        }
        catch
        {
            // buffs ilegíveis em transição
        }

        var rares = _statsMonsters.GetValueOrDefault(MonsterRarity.Rare);
        var uniques = _statsMonsters.GetValueOrDefault(MonsterRarity.Unique);
        ImGui.TextUnformatted(
            $"Kills R/U: {rares}/{uniques}   Lanterns: {PlacedLanternCount}/{Handler?.MaxLanternCount ?? 0}" +
            (goldenLanternStacks > 0 ? $"   GL buff: {goldenLanternStacks}" : "") +
            (sulphur != null ? $"   Sulphur: {sulphur:N0}" : ""));

        if (kindCounts.Count > 0)
        {
            var summary = string.Join("  ", kindCounts
                .OrderByDescending(kv => Priority(kv.Key))
                .Take(4)
                .Select(kv => $"{kv.Key}:{kv.Value}"));
            ImGui.TextUnformatted($"Restam: {summary}");
        }

        if (next is { } n)
        {
            ImGui.TextColored(settings.ObjectiveColor.Value.ToImguiVec4(),
                $"Proximo: {n.Label} ({Vector2.Distance(_playerGridPos, n.Pos):F0})");
        }

        ImGui.End();
    }
}
