using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

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
                _statsSulphurLast = sulphur;
                _statsSulphurMax = Math.Max(_statsSulphurMax ?? 0, sulphur.Value);
            }
        }
        catch
        {
            // handler ilegível fora de contexto deepwater
        }

        ZoneStatsScanRewards();
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
                sb.Append("}}");
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
        var area = GameController.Area.CurrentArea;
        _statsAreaName = area?.Name;
        _statsAreaLevel = area?.RealLevel ?? 0;
        _statsAreaStart = DateTime.UtcNow;
    }
}
