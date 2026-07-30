using System;
using System.Linq;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using SharpDX;
using Vector2 = System.Numerics.Vector2;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // Semáforo de craft nos charts do inventário (regras do Milky: "roll first,
    // quantity scales strongboxes; charts can't be rolled after running"):
    //   VERDE   = pronto para rodar (keeper com quant/sulphur bons, ou sala especial rolada)
    //   AMARELO = vale re-craftar (keeper/sala especial com quant baixo -> "ROLL";
    //             não-keeper com quant 120%+ ainda serve de lateral no Speedrun -> "SIDE")
    //   VERMELHO= lixo: Alc & Go / fundir
    private void DrawInventoryChartHighlights()
    {
        if (!Settings.VoyageSettings.HighlightInventoryCharts.Value)
        {
            return;
        }

        var panel = GameController.IngameState.IngameUi.InventoryPanel;
        if (panel is not { IsVisible: true })
        {
            return;
        }

        var items = panel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems;
        if (items == null)
        {
            return;
        }

        var tooltipRect = GetHoverTooltipRect();

        foreach (var invItem in items)
        {
            var entity = invItem.Item;
            if (entity == null || !entity.TryGetComponent(out DeepwaterChart chart))
            {
                continue;
            }

            var (color, tag) = ClassifyChart(entity, chart);
            var rect = invItem.GetClientRectCache;
            if (tooltipRect.Intersects(rect))
            {
                continue; // não desenhar por cima do tooltip (padrão NinjaPrice/Beasts)
            }
            Graphics.DrawFrame(rect.TopLeft.ToVector2Num(), rect.BottomRight.ToVector2Num(), color, 2);
            if (!string.IsNullOrEmpty(tag))
            {
                Graphics.DrawTextWithBackground(tag, rect.TopLeft.ToVector2Num() + new Vector2(2, 2), color, Color.Black);
            }
        }
    }

    private (Color Color, string Tag) ClassifyChart(Entity entity, DeepwaterChart chart)
    {
        var mods = entity.GetComponent<Mods>();

        var keeperWeight = 0f;
        foreach (var im in mods?.ImplicitMods ?? [])
        {
            var cfg = Settings.VoyageSettings.ChartModifiers.Content
                .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
            keeperWeight = Math.Max(keeperWeight, cfg?.Weight.Value ?? 0);
        }

        var specialRoom = chart.Room?.Name is { } roomName && RoomBiomeOverride.ContainsKey(roomName);
        var keeper = specialRoom || keeperWeight >= Settings.VoyageSettings.KeeperWeightThreshold.Value;

        // Quant/sulphur são propriedades COMPUTADAS (soma dos stats de todos os mods),
        // não mods individuais — igual à linha "Item Quantity" do tooltip de maps.
        var quant = SumStatValues(mods, "quantity");
        var sulphur = SumStatValues(mods, "resource");

        var greenQuant = Settings.VoyageSettings.GreenQuantThreshold.Value;
        if (keeper && (quant >= greenQuant || sulphur >= 75))
        {
            return (Color.LightGreen, null);
        }

        if (keeper)
        {
            return (Color.Yellow, "ROLL");
        }

        if (quant >= 120)
        {
            return (Color.Yellow, "SIDE");
        }

        return (Color.OrangeRed, null);
    }

    /// <summary>Rect do tooltip do item em hover (vazio se nenhum) — para não desenhar por cima.</summary>
    private RectangleF GetHoverTooltipRect()
    {
        try
        {
            return GameController.IngameState.UIHover?.AsObject<HoverItemIcon>()?.Tooltip?.GetClientRect()
                   ?? new RectangleF(0, 0, 0, 0);
        }
        catch
        {
            return new RectangleF(0, 0, 0, 0);
        }
    }

    /// <summary>Soma os valores de stat cujo Key contenha o termo, em TODOS os mods do item.</summary>
    private static int SumStatValues(Mods mods, string statKeySubstring)
    {
        var total = 0;
        foreach (var mod in mods?.ItemMods ?? [])
        {
            var statNames = mod.ModRecord?.StatNames;
            if (statNames == null)
            {
                continue;
            }

            for (var i = 0; i < statNames.Length && i < mod.Values.Count; i++)
            {
                if (statNames[i]?.Key?.Contains(statKeySubstring, StringComparison.OrdinalIgnoreCase) == true)
                {
                    total += mod.Values[i];
                }
            }
        }

        return total;
    }
}
