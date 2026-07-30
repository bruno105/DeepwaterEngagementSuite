using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // Coletor de dados por zona deepwater: monstros por raridade, chests por tipo e
    // sulphur inicial/final. Um registro JSON por linha em config/.../zone_stats.jsonl,
    // gravado ao sair da zona. Serve para calibrar os pesos de bioma/mods com dados reais.
    private string _statsAreaName;
    private int _statsAreaLevel;
    private DateTime _statsAreaStart;
    private int? _statsSulphurStart;
    private int? _statsSulphurLast;
    private int? _statsSulphurMax;
    private readonly HashSet<uint> _statsSeenMonsters = new();
    private readonly HashSet<uint> _statsSeenChests = new();
    private readonly Dictionary<MonsterRarity, int> _statsMonsters = new();
    private readonly Dictionary<string, int> _statsChests = new();
    private Dictionary<string, int> _statsRewards = new();
    private DateTime _statsLastRewardScan = DateTime.MinValue;
    // Atribuição de valor por célula do grid 3x3 (convenção do board, linha 0 embaixo).
    private readonly int[] _cellSulphurGain = new int[9];
    private readonly int[] _cellChestsOpened = new int[9];
    private readonly int[] _cellMonstersSeen = new int[9];
    private int? _statsSulphurPrev;
    // Identidade do run: bioma (via WorldArea match no .dat), dimensões da sala,
    // lanterns (7 = chart solo, ~69 = voyage) e os stats ativos da área (mods do chart).
    private string _statsBiome;
    private GameOffsets.Native.Vector2i _statsDims;
    private int _statsMaxLanterns;
    private int _statsPlacedLanterns;
    private Dictionary<string, int> _statsMapStats;
    private DateTime _statsMapStatsAt = DateTime.MinValue;
    private DateTime _statsBiomeAt = DateTime.MinValue;
    private string _statsRoom;
    private readonly HashSet<string> _statsPathSample = new(StringComparer.Ordinal);

    // Tokens de bioma/sala nos paths das entidades (chart runs rodam na área genérica
    // DeepwaterEncounter; a identidade real está nos assets da sala).
    private static readonly (string Token, string Biome)[] BiomePathTokens =
    [
        ("Sandy", "Sandy"),
        ("CoralForest", "CoralForest"),
        ("CoralReef", "CoralReef"),
        ("ThermalVent", "ThermalVents"),
    ];

    private static readonly (string Token, string Room)[] RoomPathTokens =
    [
        ("StarfishPillar", "Sea Pillars"),
        ("AnchorField", "Anchorfield"),
        ("Bathyspheres", "Infested Bathyspheres"),
        ("HazardBoat", "Hazardous Depths"),
        ("LostShipment", "Lost Shipment"),
        ("StoneCircle", "Runes of the Deep"),
        ("CrashedShip", "Kishara's Rest"),
        ("AbyssalPit", "Pelagic Abyss"),
        ("EldritchHorrors", "Eldritch Depths"),
        ("VaalRuins", "Lost Ruins"),
    ];

    private void ZoneStatsSniffPath(string path)
    {
        if (!Settings.CollectZoneStats || string.IsNullOrEmpty(path))
        {
            return;
        }

        if (string.IsNullOrEmpty(_statsBiome))
        {
            foreach (var (token, biome) in BiomePathTokens)
            {
                if (path.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    _statsBiome = biome;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(_statsRoom))
        {
            foreach (var (token, room) in RoomPathTokens)
            {
                if (path.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    _statsRoom = room;
                    break;
                }
            }
        }

        // Auto-diagnóstico: enquanto o bioma não resolve, amostra paths deepwater para
        // extrair novos tokens offline. Removível quando os tokens estabilizarem.
        if (string.IsNullOrEmpty(_statsBiome) && _statsPathSample.Count < 12 &&
            path.Contains("Deepwater", StringComparison.Ordinal))
        {
            _statsPathSample.Add(path);
        }
    }
    // Drops no chão (chart runs não têm reward window): labels visíveis = o que o
    // loot filter do jogador considera relevante. Dedup por entidade.
    private readonly HashSet<uint> _statsSeenDrops = new();
    private readonly Dictionary<string, int> _statsDrops = new();
    private DateTime _statsDropScanAt = DateTime.MinValue;
    // Contexto do plano (setado no Place): previsto vs realizado por tile.
    private double[,] _plannedMults;
    private string _plannedStrategy;
    private double _plannedScore;

    private void ZoneStatsOnMonsterAdded(Entity entity)
    {
        if (!Settings.CollectZoneStats || entity.Type != EntityType.Monster)
        {
            return;
        }

        if (!_statsSeenMonsters.Add(entity.Id))
        {
            return;
        }

        var rarity = entity.Rarity;
        _statsMonsters[rarity] = _statsMonsters.GetValueOrDefault(rarity) + 1;

        var cell = GridCellIndex(entity.PosNum.WorldToGrid());
        if (cell >= 0)
        {
            _cellMonstersSeen[cell]++;
        }
    }

    private void ZoneStatsOnMarkerAdded(Entity entity)
    {
        if (!Settings.CollectZoneStats || !_statsSeenChests.Add(entity.Id))
        {
            return;
        }

        var kind = GetChestType(entity.Path).ToString();
        _statsChests[kind] = _statsChests.GetValueOrDefault(kind) + 1;
    }

    private void ZoneStatsTick()
    {
        if (!Settings.CollectZoneStats)
        {
            return;
        }

        // Init preguiçoso: cobre o primeiro load/reload no meio de uma zona, quando
        // AreaChange (que normalmente inicializa a identidade da zona) não disparou.
        if (string.IsNullOrEmpty(_statsAreaName))
        {
            var area = GameController.Area.CurrentArea;
            _statsAreaName = area?.Name;
            _statsAreaLevel = area?.RealLevel ?? 0;
            _statsAreaStart = DateTime.UtcNow;
        }

        try
        {
            var sulphur = Handler?.Sulphur;
            if (sulphur is > 0)
            {
                _statsSulphurStart ??= sulphur;
                if (_statsSulphurPrev is { } prev && sulphur > prev && _lastCellIndex >= 0)
                {
                    _cellSulphurGain[_lastCellIndex] += sulphur.Value - prev;
                }

                _statsSulphurPrev = sulphur;
                _statsSulphurLast = sulphur;
                _statsSulphurMax = Math.Max(_statsSulphurMax ?? 0, sulphur.Value);
            }
        }
        catch
        {
            // handler ilegível fora de contexto deepwater
        }

        try
        {
            var max = Handler?.MaxLanternCount ?? 0;
            if (max > 0)
            {
                _statsMaxLanterns = max;
                // Máximo, não último: o contador zera na extração/fim do run.
                _statsPlacedLanterns = Math.Max(_statsPlacedLanterns, Handler.PlacedLanternCount);
            }
        }
        catch
        {
            // handler ilegível
        }

        if (_statsDims == default)
        {
            try
            {
                _statsDims = GameController.IngameState.Data.AreaDimensions;
            }
            catch
            {
                // dimensões ainda não carregadas
            }
        }

        // Retry com throttle: o .dat pode não estar carregado nos primeiros ticks.
        if (string.IsNullOrEmpty(_statsBiome) && (DateTime.UtcNow - _statsBiomeAt).TotalSeconds >= 2)
        {
            _statsBiomeAt = DateTime.UtcNow;
            try
            {
                var worldArea = GameController.Area.CurrentArea?.Area;
                if (worldArea != null)
                {
                    _statsBiome = GameController.Files.DeepwaterBiomes.EntriesList
                        .FirstOrDefault(b => b.WorldArea?.Address == worldArea.Address)?.Id;
                }
            }
            catch
            {
                // tenta de novo no próximo ciclo
            }
        }

        if ((DateTime.UtcNow - _statsMapStatsAt).TotalSeconds >= 2)
        {
            _statsMapStatsAt = DateTime.UtcNow;
            try
            {
                var stats = GameController.IngameState.Data.MapStats;
                if (stats is { Count: > 0 })
                {
                    var filtered = new Dictionary<string, int>();
                    foreach (var kv in stats)
                    {
                        if (kv.Value == 0)
                        {
                            continue;
                        }

                        var name = kv.Key.ToString();
                        if (name.Contains("Deepwater", StringComparison.OrdinalIgnoreCase) ||
                            name is "MapItemDropQuantityPct" or "MapItemDropRarityPct" or "MapPackSizePct")
                        {
                            filtered[name] = kv.Value;
                        }
                    }

                    if (filtered.Count >= (_statsMapStats?.Count ?? 0))
                    {
                        _statsMapStats = filtered;
                    }
                }
            }
            catch
            {
                // stats da área ilegíveis em transição
            }
        }

        ZoneStatsScanGroundLoot();
        ZoneStatsScanRewards();
    }

    private void ZoneStatsScanGroundLoot()
    {
        if ((DateTime.UtcNow - _statsDropScanAt).TotalMilliseconds < 1000)
        {
            return;
        }

        _statsDropScanAt = DateTime.UtcNow;
        try
        {
            var labels = GameController.IngameState.IngameUi.ItemsOnGroundLabelElement.VisibleGroundItemLabels;
            if (labels == null)
            {
                return;
            }

            foreach (var label in labels)
            {
                var ground = label.Entity;
                if (ground == null || !_statsSeenDrops.Add(ground.Id))
                {
                    continue;
                }

                var item = ground.GetComponent<WorldItem>()?.ItemEntity;
                if (item == null)
                {
                    continue;
                }

                var baseType = GameController.Files.BaseItemTypes.Translate(item.Path);
                // Só classes que definem lucro: currency, scarabs (MapFragment), cards e maps.
                if (baseType?.ClassName is not ("StackableCurrency" or "DivinationCard" or "Map" or "MapFragment"))
                {
                    continue;
                }

                var stack = item.GetComponent<Stack>()?.Size ?? 1;
                var name = baseType.BaseName;
                _statsDrops[name] = _statsDrops.GetValueOrDefault(name) + stack;
            }
        }
        catch
        {
            // labels em transição; tenta no próximo scan
        }
    }

    private void ZoneStatsScanRewards()
    {
        if ((DateTime.UtcNow - _statsLastRewardScan).TotalMilliseconds < 500)
        {
            return;
        }

        _statsLastRewardScan = DateTime.UtcNow;
        try
        {
            if (GameController.IngameState.IngameUi.VoyageRewardWindow is not { IsValid: true, IsVisible: true } rewardWindow)
            {
                return;
            }

            var snapshot = new Dictionary<string, int>();
            foreach (var tab in rewardWindow.ItemContainer?.Inventories ?? [])
            {
                foreach (var invItem in tab.Inventory?.VisibleInventoryItems ?? [])
                {
                    var entity = invItem.Item;
                    if (entity == null)
                    {
                        continue;
                    }

                    var name = GameController.Files.BaseItemTypes.Translate(entity.Path)?.BaseName ?? entity.Path;
                    var stack = entity.GetComponent<ExileCore.PoEMemory.Components.Stack>()?.Size ?? 1;
                    snapshot[name] = snapshot.GetValueOrDefault(name) + stack;
                }
            }

            // Guarda o MELHOR snapshot (maior total): conforme o jogador saqueia a
            // janela, os snapshots encolhem — o último seria só a sobra.
            if (snapshot.Count > 0 && snapshot.Values.Sum() > _statsRewards.Values.Sum())
            {
                _statsRewards = snapshot;
            }
        }
        catch
        {
            // janela em transição; tenta no próximo scan
        }
    }

    private void FinalizeZoneStats()
    {
        try
        {
            // Só grava zonas com conteúdo deepwater de verdade: um marker além de
            // "OtherChests" (o chest de Lost Chart em mapas normais é só ruído) ou rewards.
            var hasDeepwaterContent =
                _statsChests.Keys.Any(k => k != "OtherChests") || _statsRewards.Count > 0;
            if (Settings.CollectZoneStats && hasDeepwaterContent && !string.IsNullOrEmpty(_statsAreaName))
            {
                var sb = new StringBuilder(512);
                sb.Append('{');
                sb.Append($"\"time\":\"{_statsAreaStart:O}\",");
                sb.Append($"\"area\":\"{_statsAreaName.Replace("\"", "")}\",");
                sb.Append($"\"level\":{_statsAreaLevel},");
                var kind = _statsMaxLanterns switch
                {
                    <= 0 => "unknown",
                    <= 7 => "chart",
                    _ => "voyage",
                };
                sb.Append($"\"kind\":\"{kind}\",");
                sb.Append($"\"biome\":\"{(_statsBiome ?? "").Replace("\"", "")}\",");
                sb.Append($"\"room\":\"{(_statsRoom ?? "").Replace("\"", "")}\",");
                if (string.IsNullOrEmpty(_statsBiome) && _statsPathSample.Count > 0)
                {
                    sb.Append("\"pathSample\":[");
                    sb.Append(string.Join(",", _statsPathSample.Select(p => $"\"{p.Replace("\"", "")}\"")));
                    sb.Append("],");
                }
                sb.Append($"\"dims\":[{_statsDims.X},{_statsDims.Y}],");
                sb.Append($"\"maxLanterns\":{_statsMaxLanterns},\"placedLanterns\":{_statsPlacedLanterns},");
                sb.Append("\"mapStats\":{");
                sb.Append(string.Join(",",
                    (_statsMapStats ?? new Dictionary<string, int>()).Select(kv => $"\"{kv.Key}\":{kv.Value}")));
                sb.Append("},");
                sb.Append($"\"durationSec\":{(int)(DateTime.UtcNow - _statsAreaStart).TotalSeconds},");
                sb.Append($"\"sulphurStart\":{_statsSulphurStart?.ToString() ?? "null"},");
                sb.Append($"\"sulphurEnd\":{_statsSulphurLast?.ToString() ?? "null"},");
                sb.Append($"\"sulphurMax\":{_statsSulphurMax?.ToString() ?? "null"},");
                sb.Append("\"monsters\":{");
                sb.Append(string.Join(",", _statsMonsters.Select(kv => $"\"{kv.Key}\":{kv.Value}")));
                sb.Append("},\"chests\":{");
                sb.Append(string.Join(",", _statsChests.Select(kv => $"\"{kv.Key}\":{kv.Value}")));
                sb.Append("},\"rewards\":{");
                sb.Append(string.Join(",", _statsRewards.Select(kv => $"\"{kv.Key.Replace("\"", "")}\":{kv.Value}")));
                sb.Append("},\"drops\":{");
                sb.Append(string.Join(",", _statsDrops.Select(kv => $"\"{kv.Key.Replace("\"", "")}\":{kv.Value}")));
                sb.Append("},\"cellSeconds\":[");
                sb.Append(string.Join(",", _cellSeconds.Select(s => ((int)s).ToString())));
                sb.Append("],\"cellOrder\":[");
                sb.Append(string.Join(",", _cellFirstOrder));
                sb.Append("],\"cellSulphur\":[");
                sb.Append(string.Join(",", _cellSulphurGain));
                sb.Append("],\"cellChests\":[");
                sb.Append(string.Join(",", _cellChestsOpened));
                sb.Append("],\"cellMonsters\":[");
                sb.Append(string.Join(",", _cellMonstersSeen));
                sb.Append(']');
                if (_plannedMults != null)
                {
                    var mults = new List<string>();
                    for (var r = 0; r < 3; r++)
                    {
                        for (var c = 0; c < 3; c++)
                        {
                            mults.Add(_plannedMults[r, c].ToString("F2", CultureInfo.InvariantCulture));
                        }
                    }

                    sb.Append($",\"planned\":{{\"strategy\":\"{_plannedStrategy}\",");
                    sb.Append($"\"score\":{_plannedScore.ToString("F1", CultureInfo.InvariantCulture)},");
                    sb.Append($"\"mults\":[{string.Join(",", mults)}]}}");
                }

                sb.Append('}');
                File.AppendAllText(Path.Combine(ConfigDirectory, "zone_stats.jsonl"), sb + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            LogError($"ZoneStats: {ex.Message}");
        }

        _statsSeenMonsters.Clear();
        _statsSeenChests.Clear();
        _statsMonsters.Clear();
        _statsChests.Clear();
        _statsRewards = new Dictionary<string, int>();
        _statsSulphurStart = null;
        _statsSulphurLast = null;
        _statsSulphurMax = null;
        _statsSulphurPrev = null;
        _statsBiome = null;
        _statsDims = default;
        _statsMaxLanterns = 0;
        _statsPlacedLanterns = 0;
        _statsMapStats = null;
        _statsMapStatsAt = DateTime.MinValue;
        _statsBiomeAt = DateTime.MinValue;
        _statsRoom = null;
        _statsPathSample.Clear();
        _statsSeenDrops.Clear();
        _statsDrops.Clear();
        Array.Clear(_cellSulphurGain);
        Array.Clear(_cellChestsOpened);
        Array.Clear(_cellMonstersSeen);
        var area = GameController.Area.CurrentArea;
        _statsAreaName = area?.Name;
        _statsAreaLevel = area?.RealLevel ?? 0;
        _statsAreaStart = DateTime.UtcNow;
    }
}
