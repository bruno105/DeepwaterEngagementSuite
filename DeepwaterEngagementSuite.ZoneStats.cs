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
    private readonly HashSet<uint> _statsSeenMonsters = new();
    private readonly HashSet<uint> _statsSeenChests = new();
    private readonly Dictionary<MonsterRarity, int> _statsMonsters = new();
    private readonly Dictionary<string, int> _statsChests = new();

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

        try
        {
            var sulphur = Handler?.Sulphur;
            if (sulphur is > 0)
            {
                _statsSulphurStart ??= sulphur;
                _statsSulphurLast = sulphur;
            }
        }
        catch
        {
            // handler ilegível fora de contexto deepwater
        }
    }

    private void FinalizeZoneStats()
    {
        try
        {
            // Só grava zonas onde apareceu conteúdo deepwater (marker de chest/evento).
            if (Settings.CollectZoneStats && _statsChests.Count > 0 && !string.IsNullOrEmpty(_statsAreaName))
            {
                var sb = new StringBuilder(512);
                sb.Append('{');
                sb.Append($"\"time\":\"{_statsAreaStart:O}\",");
                sb.Append($"\"area\":\"{_statsAreaName.Replace("\"", "")}\",");
                sb.Append($"\"level\":{_statsAreaLevel},");
                sb.Append($"\"durationSec\":{(int)(DateTime.UtcNow - _statsAreaStart).TotalSeconds},");
                sb.Append($"\"sulphurStart\":{_statsSulphurStart?.ToString() ?? "null"},");
                sb.Append($"\"sulphurEnd\":{_statsSulphurLast?.ToString() ?? "null"},");
                sb.Append("\"monsters\":{");
                sb.Append(string.Join(",", _statsMonsters.Select(kv => $"\"{kv.Key}\":{kv.Value}")));
                sb.Append("},\"chests\":{");
                sb.Append(string.Join(",", _statsChests.Select(kv => $"\"{kv.Key}\":{kv.Value}")));
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
        _statsSulphurStart = null;
        _statsSulphurLast = null;
        var area = GameController.IngameState.Data.CurrentArea;
        _statsAreaName = area?.Name;
        _statsAreaLevel = area?.AreaLevel ?? 0;
        _statsAreaStart = DateTime.UtcNow;
    }
}
