using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using GameOffsets.Native;
using ImGuiNET;
using ItemFilterLibrary;
using Newtonsoft.Json;
using SharpDX;

namespace DeepwaterEngagementSuite;

public class DeepwaterEngagementSuiteSettings : ISettings
{
    public const MapIconsIndex DefaultOtherChestIcon = MapIconsIndex.HeistSpottedMiniBoss;
    public const MapIconsIndex DefaultBottledItemChestIcon = MapIconsIndex.QuestItem;
    public const MapIconsIndex DefaultGoldTreasureChestIcon = MapIconsIndex.LootFilterSmallYellowCircle;
    public const MapIconsIndex DefaultClamTreasureChestIcon = MapIconsIndex.LootFilterLargeYellowStar;
    public const MapIconsIndex DefaultCurrencyTreasureChestIcon = MapIconsIndex.RewardCurrency;
    public const MapIconsIndex DefaultCurrencyTreasureChestOpulentIcon = MapIconsIndex.LootFilterLargeYellowStar;
    public const MapIconsIndex DefaultCurrencyGemcuttersChestIcon = MapIconsIndex.RewardChestGems;
    public const MapIconsIndex DefaultUniqueWeaponChestIcon = MapIconsIndex.RewardWeapons;
    public const MapIconsIndex DefaultUniqueArmourChestIcon = MapIconsIndex.RewardArmour;
    public static readonly Color UniqueItemTint = new Color(175, 96, 37); // PoE unique orange
    public const MapIconsIndex DefaultScarabChestIcon = MapIconsIndex.RewardScarabs;
    public const MapIconsIndex DefaultStackedDecksChestIcon = MapIconsIndex.RewardDivinationCards;
    public const MapIconsIndex DefaultMapsChestIcon = MapIconsIndex.RewardMaps;
    // Small glowing orb (Allflame-style); not Essence.
    public const MapIconsIndex DefaultAllflameEmbersChestIcon = MapIconsIndex.SanctumGoldConvert;
    public const MapIconsIndex DefaultCursedDucatDropIcon = MapIconsIndex.RewardPerandus;
    public const MapIconsIndex DefaultIzaroObjectIcon = MapIconsIndex.RewardLabyrinth;
    public const MapIconsIndex DefaultAltarCrabIcon = MapIconsIndex.RewardBestiary;
    public const MapIconsIndex DefaultAltarOctopusIcon = MapIconsIndex.RewardBreach;
    public const MapIconsIndex DefaultTormentedSpiritEncounterIcon = MapIconsIndex.LootFilterSmallGreenCircle;
    // DeepwaterLantern is blank in Icons.png; BlightPortalFire is a visible fire-style stand-in.
    public const MapIconsIndex DefaultLanternReplenishEncounterIcon = MapIconsIndex.BlightPortalFire;
    public const MapIconsIndex DefaultGoldenLanternIcon = MapIconsIndex.LootFilterLargeYellowCircle;

    public Dictionary<IconPickerIndex, IconDisplaySettings> IconMapping = new();

    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    public RangeNode<int> WorldIconSize { get; set; } = new RangeNode<int>(50, 25, 200);
    public RangeNode<int> MapIconSize { get; set; } = new RangeNode<int>(30, 15, 200);

    [Menu("Zone analytics")]
    public ZoneAnalyticsSettings ZoneAnalytics { get; set; } = new ZoneAnalyticsSettings();

    public CurrencyReminderSettings CurrencyReminderSettings { get; set; } = new CurrencyReminderSettings();
    public BubbleSettings BubbleSettings { get; set; } = new BubbleSettings();

    public HintSettings HintSettings { get; set; } = new HintSettings();

    public PilotSettings PilotSettings { get; set; } = new PilotSettings();

    [Menu("Collect zone stats", "Grava monstros/chests/sulphur por zona deepwater em config/DeepwaterEngagementSuite/zone_stats.jsonl")]
    public ToggleNode CollectZoneStats { get; set; } = new ToggleNode(true);

    [Menu("Bubble planner settings")]
    public PlannerSettings PlannerSettings { get; set; } = new PlannerSettings();
    public VoyageSettings VoyageSettings { get; set; } = new VoyageSettings();

    public static MapIconsIndex GetDefaultIcon(IconPickerIndex index) => index switch
    {
        IconPickerIndex.BottledItemChest => DefaultBottledItemChestIcon,
        IconPickerIndex.GoldTreasureChest => DefaultGoldTreasureChestIcon,
        IconPickerIndex.ClamTreasureChest => DefaultClamTreasureChestIcon,
        IconPickerIndex.CurrencyTreasureChest => DefaultCurrencyTreasureChestIcon,
        IconPickerIndex.CurrencyTreasureChestOpulent => DefaultCurrencyTreasureChestOpulentIcon,
        IconPickerIndex.CurrencyGemcuttersChest => DefaultCurrencyGemcuttersChestIcon,
        IconPickerIndex.UniqueWeaponChest => DefaultUniqueWeaponChestIcon,
        IconPickerIndex.UniqueArmourChest => DefaultUniqueArmourChestIcon,
        IconPickerIndex.ScarabChest => DefaultScarabChestIcon,
        IconPickerIndex.StackedDecksChest => DefaultStackedDecksChestIcon,
        IconPickerIndex.MapsChest => DefaultMapsChestIcon,
        IconPickerIndex.AllflameEmbersChest => DefaultAllflameEmbersChestIcon,
        IconPickerIndex.CursedDucatDrop => DefaultCursedDucatDropIcon,
        IconPickerIndex.RandomDucatChest => DefaultCursedDucatDropIcon,
        IconPickerIndex.IzaroObject => DefaultIzaroObjectIcon,
        IconPickerIndex.AltarCrab => DefaultAltarCrabIcon,
        IconPickerIndex.AltarOctopus => DefaultAltarOctopusIcon,
        IconPickerIndex.TormentedSpiritEncounter => DefaultTormentedSpiritEncounterIcon,
        IconPickerIndex.LanternReplenishEncounter => DefaultLanternReplenishEncounterIcon,
        IconPickerIndex.GoldenLantern => DefaultGoldenLanternIcon,
        _ => DefaultOtherChestIcon,
    };

    public static Color? GetDefaultTint(IconPickerIndex index) => index switch
    {
        IconPickerIndex.UniqueWeaponChest or IconPickerIndex.UniqueArmourChest => UniqueItemTint,
        _ => null,
    };

    public static float GetDefaultIconSizeScale(IconPickerIndex index) => index switch
    {
        IconPickerIndex.CurrencyTreasureChestOpulent => 2.0f,
        _ => 1f,
    };
}

[Submenu(CollapsedByDefault = true)]
public class ZoneAnalyticsSettings
{
    [JsonIgnore] public CustomNode Node { get; set; } = new CustomNode();
}

[Submenu(CollapsedByDefault = true)]
public class CurrencyReminderSettings
{
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);
    public RangeNode<int> RequiredExaltedOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> RequiredAlchemyOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> RequiredChaosOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> RequiredScouringOrbs { get; set; } = new RangeNode<int>(20, 0, 20);
    public RangeNode<int> MaxInventoryItems { get; set; } = new RangeNode<int>(30, 0, 60);
}

[Submenu(CollapsedByDefault = true)]
public class PlannerSettings
{
    // Planner weights (default 1 via ChestSettings). High-value targets are weighted higher.
    public Dictionary<IconPickerIndex, ChestSettings> ChestSettingsMap = new()
    {
        [IconPickerIndex.BottledItemChest] = new ChestSettings { Weight = 30 },
        [IconPickerIndex.ClamTreasureChest] = new ChestSettings { Weight = 2 },
        [IconPickerIndex.LanternReplenishEncounter] = new ChestSettings { Weight = 30 },
        [IconPickerIndex.CurrencyTreasureChestOpulent] = new ChestSettings { Weight = 50 },
        // Sem lucro na prática — abaixo do peso neutro (1) p/ o planner de bolhas
        // preferir qualquer outro alvo no desempate.
        [IconPickerIndex.MapsChest] = new ChestSettings { Weight = 0.3f },
        [IconPickerIndex.GoldTreasureChest] = new ChestSettings { Weight = 0.2f },
    };

    public HotkeyNodeV2 StartSearchHotkey { get; set; } = new HotkeyNodeV2(Keys.None);
    public HotkeyNodeV2 StopSearchHotkey { get; set; } = new HotkeyNodeV2(Keys.None);
    public HotkeyNodeV2 ClearSearchHotkey { get; set; } = new HotkeyNodeV2(Keys.None);
    public HotkeyNodeV2 ConfirmEditorPlacementHotkey { get; set; } = new HotkeyNodeV2(Keys.None);

    [JsonIgnore]
    [ConditionalDisplay(nameof(IsSearchRunning), false)]
    public ButtonNode StartSearch { get; set; } = new ButtonNode();

    [JsonIgnore]
    [ConditionalDisplay(nameof(IsSearchRunning))]
    public ButtonNode StopSearch { get; set; } = new ButtonNode();

    [JsonIgnore]
    [ConditionalDisplay(nameof(HasSearchResult))]
    public ButtonNode ClearSearch { get; set; } = new ButtonNode();
    public ToggleNode PlaySoundOnFinish { get; set; } = new ToggleNode(false);
    public ToggleNode DrawPlannedBubblesOnMap { get; set; } = new ToggleNode(true);
    public ToggleNode DrawLinesToLanternsInWorld { get; set; } = new ToggleNode(true);
    public RangeNode<int> ClosestNLanterns { get; set; } = new RangeNode<int>(2, 0, 10);
    public ToggleNode MergePlannedBubbles { get; set; } = new ToggleNode(true);

    [Menu("Color for suggested bubble radius")]
    public ColorNode BubbleColor { get; set; } = new ColorNode(Color.Purple);

    public ColorNode MapLineColor { get; set; } = new ColorNode(Color.Red);
    public ColorNode WorldLineColor { get; set; } = new ColorNode(Color.Orange);

    [Menu("Color for captured entities in world")]
    public ColorNode CapturedEntityWorldFrameColor { get; set; } = new ColorNode(Color.Purple);

    [Menu("Color for captured entities on map")]
    public ColorNode CapturedEntityMapFrameColor { get; set; } = new ColorNode(Color.Purple);

    [Menu(null, "Do not show lines/circles for plan segments where a real bubble has already been placed")]
    public ToggleNode RemoveGraphicsForPlacedBubbles { get; set; } = new ToggleNode(false);

    public RangeNode<float> TextMarkerScale { get; set; } = new RangeNode<float>(2, 0, 5);

    public RangeNode<float> MaximumGenerationTimeSeconds { get; set; } = new RangeNode<float>(5, 0, 60);
    public RangeNode<int> SearchThreads { get; set; } = new RangeNode<int>(5, 1, 10);
    public RangeNode<float> NewRandomPathInjectionRate { get; set; } = new RangeNode<float>(1f, 0, 2);
    public RangeNode<int> PathGenerationSize { get; set; } = new RangeNode<int>(100, 1, 1000);
    public RangeNode<int> ValidatedIntermediatePoints { get; set; } = new RangeNode<int>(1, 0, 5);


    public ToggleNode ShowScoreHistory { get; set; } = new ToggleNode(false);
    public ToggleNode ShowScoreHistoryAfterSearchEnds { get; set; } = new ToggleNode(false);

    internal bool HasSearchResult => SearchState != SearchState.Empty;
    internal bool IsSearchRunning => SearchState == SearchState.Searching;

    internal SearchState SearchState = SearchState.Empty;
}

[Submenu(CollapsedByDefault = true)]
public class PilotSettings
{
    [Menu("Show pilot panel", "Painel in-run com fase/comportamento da estrategia ativa")]
    public ToggleNode ShowPilotPanel { get; set; } = new ToggleNode(true);

    [Menu("Show objective arrow", "Linha do jogador ate o proximo objetivo priorizado")]
    public ToggleNode ShowObjectiveArrow { get; set; } = new ToggleNode(true);

    [Menu("Use Radar route", "Rota real por terreno andavel via Radar (PluginBridge); fallback em linha reta")]
    public ToggleNode UseRadarRoute { get; set; } = new ToggleNode(true);

    [Menu("Route only on large map", "While the large map is open, the pilot route/line draws ONLY on the map (no world lines). Closing the map brings the world drawing back.")]
    public ToggleNode RouteOnlyOnLargeMap { get; set; } = new ToggleNode(true);

    [Menu("Show 3x3 grid tracker", "Grid 3x3 da area + trilha do personagem no mapa aberto")]
    public ToggleNode ShowGridTracker { get; set; } = new ToggleNode(true);

    [Menu("Grid debug mode", "Ativa o grid tracker em QUALQUER mapa - teste sem gastar voyage")]
    public ToggleNode GridDebugMode { get; set; } = new ToggleNode(false);

    [Menu("Grid window size", "Largura do canvas da janela do grid tracker (px)")]
    public RangeNode<int> GridWindowSize { get; set; } = new RangeNode<int>(260, 150, 600);

    public ColorNode GridColor { get; set; } = new ColorNode(Color.Cyan);
    public ColorNode PathColor { get; set; } = new ColorNode(Color.Magenta);

    [Menu("Speedrun extract (min)", "Aviso de extracao no Speedrun")]
    public RangeNode<int> SpeedrunExtractMinutes { get; set; } = new RangeNode<int>(15, 5, 60);

    [Menu("Meatfish extract (min)", "Aviso de extracao no Meatfish")]
    public RangeNode<int> MeatfishExtractMinutes { get; set; } = new RangeNode<int>(30, 10, 90);

    // Dados 31/07: AlcGo de 8,9 min rendeu 2.768 sulphur/min; a de 14,1 min caiu
    // para 1.601 — a doutrina "leave fast" precisa do mesmo relogio das outras.
    [Menu("Alch & Go extract (min)", "Aviso de extracao no Alch & Go (runs longas rendem menos)")]
    public RangeNode<int> AlcGoExtractMinutes { get; set; } = new RangeNode<int>(10, 3, 30);

    public ColorNode ObjectiveColor { get; set; } = new ColorNode(Color.LightGreen);
}

[Submenu(CollapsedByDefault = true)]
public class HintSettings
{
    public ToggleNode ShowPointerHints { get; set; } = new ToggleNode(true);
    public ToggleNode ShowHintsInWorld { get; set; } = new ToggleNode(true);
    public ToggleNode ShowHintsOnMap { get; set; } = new ToggleNode(true);

    [Menu("Show target markers", "Um marcador por alvo NAO revelado (dedup entre chamas) - o modo limpo")]
    public ToggleNode ShowTargetMarkers { get; set; } = new ToggleNode(true);

    [Menu("Show ray lines", "Linhas chama->alvo; limitadas as chamas mais proximas")]
    public ToggleNode ShowRayLines { get; set; } = new ToggleNode(false);

    [Menu("Max ray flames", "Numero maximo de chamas com linhas desenhadas")]
    public RangeNode<int> MaxRayFlames { get; set; } = new RangeNode<int>(2, 1, 10);

    [Menu("Max pointer range", "So desenha linhas de chamas a ate esta distancia do jogador (grid units); 0 = todas")]
    public RangeNode<int> MaxPointerRangeGridUnits { get; set; } = new RangeNode<int>(250, 0, 1000);

    [Menu("Ray length (grid units)", "Comprimento do raio no fallback por rotacao, quando o componente Pointer nao esta legivel")]
    public RangeNode<int> RayLengthGridUnits { get; set; } = new RangeNode<int>(250, 50, 600);

    [Menu("Hide resolved rays", "Esconde linhas para alvos que ja resolvem para um bau/evento conhecido")]
    public ToggleNode HideResolvedRays { get; set; } = new ToggleNode(false);

    public ColorNode RayColor { get; set; } = new ColorNode(Color.Gold);

    [Menu("Unrevealed color", "Cor das linhas para alvos ainda nao revelados")]
    public ColorNode UnrevealedColor { get; set; } = new ColorNode(Color.White);

    [Menu("Show debug window", "Tabela com os alvos lidos do componente Pointer")]
    public ToggleNode ShowHintsDebugWindow { get; set; } = new ToggleNode(false);
}

[Submenu(CollapsedByDefault = true)]
public class BubbleSettings
{
    public ToggleNode ShowBubblesOnMap { get; set; } = new ToggleNode(true);
    public ToggleNode ShowBubblesInWorld { get; set; } = new ToggleNode(false);

    [Menu("Color for bubble radius")]
    public ColorNode BubbleColor { get; set; } = new ColorNode(Color.Red);

    public RangeNode<int> BubbleRadiusOverride { get; set; } = new RangeNode<int>(0, 0, 1000);

    [Menu("Merge bubble circles for planned bubbles")]
    public ToggleNode EnableBubbleRadiusMerging { get; set; } = new ToggleNode(true);

    [Menu("Hide icons of entities captured by bubbles in world")]
    public ToggleNode HideCapturedEntitiesInWorld { get; set; } = new ToggleNode(false);

    [Menu("Hide icons of entities captured by bubbles on map")]
    public ToggleNode HideCapturedEntitiesOnMap { get; set; } = new ToggleNode(false);

    [Menu("Rectangle Thickness for captured entities in world")]
    public RangeNode<int> CapturedEntityWorldFrameThickness { get; set; } = new RangeNode<int>(2, 1, 20);

    [Menu("Rectangle Thickness for captured entities on map")]
    public RangeNode<int> CapturedEntityMapFrameThickness { get; set; } = new RangeNode<int>(2, 1, 20);

    public ToggleNode MarkStartingBubble { get; set; } = new ToggleNode(true);
}

[Submenu(CollapsedByDefault = true)]
public class VoyageSettings
{
    public VoyageSettings()
    {
        ClearBorderModifiers = new ButtonNode() { OnPressed = () => { BorderModifiers.Content.Clear(); } };
        ClearChartModifiers = new ButtonNode() { OnPressed = () => { ChartModifiers.Content.Clear(); } };
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
    }

    [JsonIgnore] [IgnoreMenu] public List<VoyageProfileEntry> Profiles { get; set; } = new();

    public ToggleNode EnableVoyageHandling { get; set; } = new ToggleNode(true);

    [Menu(null, CollapsedByDefault = true)]
    public ContentNode<VoyageExcludedChartSettings> IgnoredCharts { get; set; } = new ContentNode<VoyageExcludedChartSettings>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new VoyageExcludedChartSettings(),
        ItemFilter = (o, s) => o.IFL.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };

    [Menu("Show optimizer window")]
    public ToggleNode ShowOptimizerWindow { get; set; } = new ToggleNode(true);

    [Menu("Solver time limit (seconds)", "Max time the solver runs before returning the best solution found so far. 0 = no limit.")]
    public RangeNode<int> SolverTimeLimitSeconds { get; set; } = new RangeNode<int>(5, 1, 120);

    [Menu("Solver max charts", "Only the N best charts (by weight) are considered by the solver. 0 = no limit.")]
    public RangeNode<int> SolverMaxCharts { get; set; } = new RangeNode<int>(24, 0, 200);

    [Menu("Use fast solver (exact)", "Exact topology+DP solver (upstream port, harness-validated: 26/26 pools, never worse than the old MRV — which was suboptimal in 17/26). Ignores the time limit; you can raise max charts (0 = whole pool). Turning it off falls back to the old MRV.")]
    public ToggleNode UseFastSolver { get; set; } = new ToggleNode(true);

    [Menu("Allow burning reserves", "Lets the solver use pieces reserved for other strategies when free charts run short (backfill). Off = hard reserve: fewer than 9 free pieces means no valid solution.")]
    public ToggleNode AllowBurningReserves { get; set; } = new ToggleNode(false);
    public RangeNode<float> BorderHighlightThreshold { get; set; } = new RangeNode<float>(1.01f, 0, 10);
    public RangeNode<float> ChartHighlightThreshold { get; set; } = new RangeNode<float>(1.0f, 0, 10);

    public ToggleNode ShowRerollAdvisor { get; set; } = new ToggleNode(true);

    [Menu("Show tile priority", "Numera os tiles do board por multiplicador efetivo (onde investir allflames/rota)")]
    public ToggleNode ShowTilePriority { get; set; } = new ToggleNode(true);

    [Menu("Show chart values", "Mostra o valor calculado de cada chart do estoque e marca os que entram no solve")]
    public ToggleNode ShowChartValues { get; set; } = new ToggleNode(true);

    [Menu("Highlight inventory charts", "Semaforo verde/amarelo/vermelho nos charts do inventario (craft manual)")]
    public ToggleNode HighlightInventoryCharts { get; set; } = new ToggleNode(true);

    [Menu("Keeper weight threshold", "Peso minimo do implicit para o chart ser keeper (verde/amarelo)")]
    public RangeNode<int> KeeperWeightThreshold { get; set; } = new RangeNode<int>(40, 0, 100);

    [Menu("Green quant threshold", "Quant minima para keeper ficar verde (Milky: 110%+ antes de rodar)")]
    public RangeNode<int> GreenQuantThreshold { get; set; } = new RangeNode<int>(110, 0, 200);

    [Menu("Reroll keep threshold", "R = melhor score atual / score com borders medios. Abaixo disso o advisor recomenda reroll.")]
    public RangeNode<float> RerollKeepThreshold { get; set; } = new RangeNode<float>(1.0f, 0f, 3f);

    [IgnoreMenu]
    public TextNode SelectedStrategy { get; set; } = new TextNode("Auto");
    public ListNode ProfileSelector { get; set; } = new ListNode();
    [JsonIgnore] public ButtonNode AddProfile { get; set; } = new ButtonNode();
    [JsonIgnore] public ButtonNode ReloadProfiles { get; set; } = new ButtonNode();
    [JsonIgnore][Menu("Delete current profile (hold shift)")] public ButtonNode DeleteCurrentProfile { get; set; } = new ButtonNode();
    [JsonIgnore] public CustomNode ProfileRenameNode { get; set; } = new CustomNode();

    public float[][] PositionWeights { get; set; } = DefaultPositionWeights();

    [JsonIgnore]
    public CustomNode PositionWeightsNode { get; set; }

    public static float[][] DefaultPositionWeights() =>
    [
        [1.00f, 0.15f, 1.00f],
        [1.10f, 0.90f, 1.00f],
        [1.15f, 1.05f, 1.00f],
    ];

    [JsonIgnore]
    public ButtonNode ClearBorderModifiers { get; set; }

    [Menu(null, CollapsedByDefault = true)]
    [JsonIgnore]
    public ContentNode<VoyageBorderModifier> BorderModifiers { get; set; } = new ContentNode<VoyageBorderModifier>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new VoyageBorderModifier(),
        ItemFilter = (o, s) => o.Id.Value.Contains(s, StringComparison.OrdinalIgnoreCase) ||
                               o.Abbreviation.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };

    [JsonIgnore]
    public ButtonNode ClearChartModifiers { get; set; }

    [Menu(null, CollapsedByDefault = true)]
    [JsonIgnore]
    public ContentNode<VoyageChartModifier> ChartModifiers { get; set; } = new ContentNode<VoyageChartModifier>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new VoyageChartModifier(),
        ItemFilter = (o, s) => o.Id.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };

    [Menu(null, CollapsedByDefault = true)]
    [JsonIgnore]
    public ContentNode<BiomeWeightSetting> BiomeWeights { get; set; } = new ContentNode<BiomeWeightSetting>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new BiomeWeightSetting(),
        ItemFilter = (o, s) => o.Id.Value.Contains(s, StringComparison.OrdinalIgnoreCase),
    };
}

[Submenu(CollapsedByDefault = true)]
public class VoyageExcludedChartSettings
{
    private static readonly ConcurrentDictionary<string, ItemQuery<ChartData>> FilterCache = [];

    public VoyageExcludedChartSettings()
    {
        Status.DrawDelegate = () =>
        {
            if (Query.FailedToCompile)
            {
                ImGui.Text($"Compilation failed: {Query.Error}");
            }
        };
    }

    [JsonIgnore]
    public CustomNode Status { get; set; } = new CustomNode();

    [Menu("IFL")]
    public TextNode IFL { get; set; } = new TextNode("false");
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [IgnoreMenu]
    [JsonIgnore]
    public ItemQuery<ChartData> Query => FilterCache.GetOrAdd(IFL.Value, ItemQuery.Load<ChartData>);

    public override string ToString()
    {
        return $"{(Enabled ? "" : "[Disabled]")}{IFL.Value}###";
    }
}

public class ChartData : ItemData
{
    public Vector2i Pos { get; }

    public ChartData(Entity queriedItem, GameController gc, Vector2i pos) 
        : base(queriedItem, gc)
    {
        Pos = pos;
    }

    public ChartData(Entity queriedItem, Entity groundItem, GameController gameController, Vector2i pos) 
        : base(queriedItem, groundItem, gameController)
    {
        Pos = pos;
    }
}

public class VoyageProfileEntry
{
    public string Name;
    public VoyageProfile Profile;
}

[Submenu(CollapsedByDefault = true)]
public class VoyageBorderModifier
{
    public TextNode Id { get; set; } = new TextNode("");
    public TextNode Abbreviation { get; set; } = new TextNode("");
    public RangeNode<float> ValueMultiplier { get; set; } = new RangeNode<float>(1, 0, 10);
    public ColorNode HighlightColor { get; set; } = Color.Cyan;

    public override string ToString()
    {
        return $"{Id.Value} {ValueMultiplier.Value}###";
    }
}

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

public enum SearchState
{
    Empty,
    Searching,
    Stopped,
}