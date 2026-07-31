using System;
using System.Collections.Generic;
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
    // quantity scales strongboxes; charts can't be rolled after running").
    // VEREDITO (medido 31/07, teste etiquetado + 86 runs): os reward riders NÃO se
    // aplicam a runs SOLO (instância sempre 20/20/20) — craft só paga no board de
    // VOYAGE. Nunca gaste chaos em chart que vai rodar solo.
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
            if (OverlapsTooltip(rect, tooltipRect))
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

    // Salas-peça das estratégias (visíveis ANTES de chartar): valem manter/rolar
    // por si só. RoomBiomeOverride NÃO serve aqui — é tabela de bioma e inclui
    // salas comuns (Eldritch Depths etc.) que não são peça de nada.
    private static readonly HashSet<string> KeeperRooms = new(StringComparer.Ordinal)
    {
        "Sea Pillars",
        "Pelagic Abyss",
    };

    private (Color Color, string Tag) ClassifyChart(Entity entity, DeepwaterChart chart)
    {
        var mods = entity.GetComponent<Mods>();

        // Mecânica (validada via bridge invcharts, 25 charts): cada explicit carrega
        // um "reward rider" (values[0]) que cai em UMA das recompensas — quant,
        // rarity, gold, sulphur ou pack. Rolar = redistribuir riders. No inventário
        // o implícito real é OCULTO ("revealed once Charted" — todos genéricos,
        // peso 10), então keeper-por-implícito só fira em chart já chartado.
        var keeperWeight = 0f;
        foreach (var im in mods?.ImplicitMods ?? [])
        {
            var cfg = Settings.VoyageSettings.ChartModifiers.Content
                .FirstOrDefault(cm => cm.Id.Value.Equals(im.RawName, StringComparison.OrdinalIgnoreCase));
            keeperWeight = Math.Max(keeperWeight, cfg?.Weight.Value ?? 0);
        }

        var keeper = (chart.Room?.Name is { } roomName && KeeperRooms.Contains(roomName)) ||
                     keeperWeight >= Settings.VoyageSettings.KeeperWeightThreshold.Value;

        var quant = SumStatValues(mods, "quantity");
        var sulphur = SumStatValues(mods, "resource");

        // Distribuição real do pool: quant 0-120 (top ~20% ≥110 — a meta do Milky é
        // alcançável), sulphur 0-120 (~20% ≥105). FILTH = candidato a pin no border
        // do polvo (~4k sulphur); quant 110+ = pronto (maiores quants nas laterais).
        if (sulphur >= 100)
        {
            return (Color.LightGreen, "FILTH");
        }

        if (quant >= Settings.VoyageSettings.GreenQuantThreshold.Value)
        {
            return (Color.LightGreen, null);
        }

        if (keeper)
        {
            return quant >= 80 || sulphur >= 75
                ? (Color.LightGreen, null)
                : (Color.Yellow, "ROLL");
        }

        if (quant >= 80)
        {
            return (Color.Yellow, "ROLL"); // perto da meta: chaos até 110+
        }

        return (Color.OrangeRed, null); // Alc & Go / filler
    }

    /// <summary>
    /// Rect do tooltip do item em hover (vazio se nenhum) — para não desenhar por
    /// cima. Só vale se o tooltip está VISÍVEL e o rect é plausível: o elemento
    /// âncora às vezes reporta uma faixa gigante (apagava a fileira inteira do
    /// inventário mesmo sem tooltip sobre ela).
    /// </summary>
    private RectangleF GetHoverTooltipRect()
    {
        try
        {
            var tooltip = GameController.IngameState.UIHover?.AsObject<HoverItemIcon>()?.Tooltip;
            if (tooltip is not { IsVisible: true })
            {
                return new RectangleF(0, 0, 0, 0);
            }

            var rect = tooltip.GetClientRect();
            var win = GameController.Window.GetWindowRectangleTimeCache;
            if (rect.Width > win.Width * 0.6f || rect.Height > win.Height * 0.8f)
            {
                return new RectangleF(0, 0, 0, 0); // rect âncora, não a caixa visível
            }

            return rect;
        }
        catch
        {
            return new RectangleF(0, 0, 0, 0);
        }
    }

    /// <summary>Sobreposição REAL (>20% da área do item) — encostar na borda não apaga o frame.</summary>
    private static bool OverlapsTooltip(RectangleF item, RectangleF tooltip)
    {
        if (tooltip.Width <= 0 || tooltip.Height <= 0)
        {
            return false;
        }

        var ix = Math.Max(item.Left, tooltip.Left);
        var iy = Math.Max(item.Top, tooltip.Top);
        var ax = Math.Min(item.Right, tooltip.Right);
        var ay = Math.Min(item.Bottom, tooltip.Bottom);
        if (ax <= ix || ay <= iy)
        {
            return false;
        }

        return (ax - ix) * (ay - iy) > 0.2f * item.Width * item.Height;
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
