using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared.Helpers;
using ImGuiNET;
using Newtonsoft.Json.Linq;
using SharpDX;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    // Aba de analytics in-game sobre o zone_stats.jsonl: últimas voyages com as
    // métricas que usamos nas análises (sul/min, células, div/ex/chaos/scarabs) +
    // botão de emergência que finaliza o registro atual na hora (cache/instância
    // presa — o flush normal só acontece na troca de instância).
    private sealed record ZoneAnalyticsRow(
        DateTime Time, string Strategy, double Minutes, int SulGain, double SulPerMin,
        int Cells, int Chests, int Bottles, int Rares, int Uniques, int Div, int Ex, int Chaos, int Scarabs, int Decks);

    private sealed record ChartAnalyticsRow(
        DateTime Time, string Biome, string Room, int DurSec, int SulGain,
        int Chests, int Bottles, int Rares, int Uniques, int Scarabs, int Chaos, int Div, int Ex);

    private const string AnalyticsTimeFormat = "dd/MM/yy HH:mm:ss";

    private sealed class BiomeAggregate
    {
        public int Runs;
        public long Sul;
        public long Dur;
        public long Chests;
        public long Scarabs;
        public long Chaos;
    }

    private List<ZoneAnalyticsRow> _analyticsVoyages;
    private List<ChartAnalyticsRow> _analyticsCharts;
    private List<(string Biome, BiomeAggregate Agg)> _analyticsBiomes;
    private string _analyticsChartsSummary;
    private string _analyticsStatus;

    // Ordenação por clique no header: aplica o sort spec do ImGui à lista viva.
    private static void ApplyTableSort<T>(List<T> rows, Func<int, Comparison<T>> comparerFor)
    {
        try
        {
            var specs = ImGui.TableGetSortSpecs();
            if (!specs.SpecsDirty || specs.SpecsCount <= 0)
            {
                return;
            }

            var spec = specs.Specs;
            var cmp = comparerFor(spec.ColumnIndex);
            if (cmp != null)
            {
                rows.Sort(cmp);
                if (spec.SortDirection == ImGuiSortDirection.Descending)
                {
                    rows.Reverse();
                }
            }

            specs.SpecsDirty = false;
        }
        catch
        {
            // sort specs indisponíveis (tabela sem foco/versão do ImGui)
        }
    }

    private void LoadZoneAnalytics()
    {
        try
        {
            var path = Path.Combine(ConfigDirectory, "zone_stats.jsonl");
            var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            var voyages = new List<ZoneAnalyticsRow>();
            var charts = new List<ChartAnalyticsRow>();
            var biomes = new Dictionary<string, BiomeAggregate>(StringComparer.Ordinal);
            var chartCount = 0;
            var chartSul = 0L;
            var chartSec = 0L;

            foreach (var line in lines.Skip(Math.Max(0, lines.Length - 400)))
            {
                JObject record;
                try
                {
                    record = JObject.Parse(line);
                }
                catch
                {
                    continue;
                }

                var kind = (string)record["kind"] ?? "";
                var sulStart = (int?)record["sulphurStart"] ?? 0;
                var sulMax = (int?)record["sulphurMax"] ?? 0;
                var gain = Math.Max(0, sulMax - sulStart);
                var dur = (int?)record["durationSec"] ?? 0;

                if (kind == "chart")
                {
                    chartCount++;
                    chartSul += gain;
                    chartSec += dur;

                    var cBiome = (string)record["biome"];
                    if (string.IsNullOrEmpty(cBiome))
                    {
                        cBiome = "?";
                    }

                    var cChests = 0;
                    var cBottles = 0;
                    if (record["chests"] is JObject cChestsObj)
                    {
                        foreach (var p in cChestsObj.Properties())
                        {
                            var val = (int?)p.Value ?? 0;
                            cChests += val;
                            if (p.Name == "BottledItemChest")
                            {
                                cBottles += val;
                            }
                        }
                    }

                    int cScar = 0, cChaos = 0, cDiv = 0, cEx = 0;
                    if (record["drops"] is JObject cDropsObj)
                    {
                        foreach (var p in cDropsObj.Properties())
                        {
                            var val = (int?)p.Value ?? 0;
                            if (p.Name == "Divine Orb") cDiv += val;
                            else if (p.Name == "Exalted Orb") cEx += val;
                            else if (p.Name == "Chaos Orb") cChaos += val;
                            else if (p.Name.Contains("Scarab", StringComparison.Ordinal)) cScar += val;
                        }
                    }

                    var cTime = DateTime.TryParse((string)record["time"], CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var cdt)
                        ? cdt.ToLocalTime()
                        : DateTime.MinValue;
                    charts.Add(new ChartAnalyticsRow(cTime, cBiome, (string)record["room"] ?? "", dur, gain,
                        cChests, cBottles, (int?)record["monsters"]?["Rare"] ?? 0, (int?)record["monsters"]?["Unique"] ?? 0,
                        cScar, cChaos, cDiv, cEx));

                    if (!biomes.TryGetValue(cBiome, out var agg))
                    {
                        biomes[cBiome] = agg = new BiomeAggregate();
                    }

                    agg.Runs++;
                    agg.Sul += gain;
                    agg.Dur += dur;
                    agg.Chests += cChests;
                    agg.Scarabs += cScar;
                    agg.Chaos += cChaos;
                    continue;
                }

                if (kind != "voyage")
                {
                    continue;
                }

                var cellSecondsSum = (record["cellSeconds"] as JArray)?.Sum(v => (double?)v ?? 0) ?? 0;
                if (cellSecondsSum < 120)
                {
                    continue; // fragmento (reload/tempo de barco)
                }

                var cells = (record["cellOrder"] as JArray)?.Count(v => ((int?)v ?? 0) > 0) ?? 0;
                var chests = 0;
                var bottles = 0;
                if (record["chests"] is JObject chestsObj)
                {
                    foreach (var p in chestsObj.Properties())
                    {
                        var val = (int?)p.Value ?? 0;
                        chests += val;
                        if (p.Name == "BottledItemChest")
                        {
                            bottles += val;
                        }
                    }
                }

                int div = 0, ex = 0, chaos = 0, scarabs = 0, decks = 0;
                if (record["drops"] is JObject dropsObj)
                {
                    foreach (var p in dropsObj.Properties())
                    {
                        var val = (int?)p.Value ?? 0;
                        if (p.Name == "Divine Orb") div += val;
                        else if (p.Name == "Exalted Orb") ex += val;
                        else if (p.Name == "Chaos Orb") chaos += val;
                        else if (p.Name == "Stacked Deck") decks += val;
                        else if (p.Name.Contains("Scarab", StringComparison.Ordinal)) scarabs += val;
                    }
                }

                var time = DateTime.TryParse((string)record["time"], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt)
                    ? dt.ToLocalTime()
                    : DateTime.MinValue;
                var strat = (string)record["planned"]?["strategy"];
                var minutes = dur / 60.0;
                voyages.Add(new ZoneAnalyticsRow(time, string.IsNullOrEmpty(strat) ? "?" : strat,
                    minutes, gain, minutes > 0.1 ? gain / minutes : 0,
                    cells, chests, bottles,
                    (int?)record["monsters"]?["Rare"] ?? 0, (int?)record["monsters"]?["Unique"] ?? 0,
                    div, ex, chaos, scarabs, decks));
            }

            voyages.Reverse();
            _analyticsVoyages = voyages.Take(15).ToList();
            charts.Reverse();
            _analyticsCharts = charts.Take(30).ToList();
            _analyticsBiomes = biomes.Select(kv => (kv.Key, kv.Value))
                .OrderByDescending(x => x.Value.Runs).ToList();
            _analyticsChartsSummary = chartCount > 0
                ? $"solo charts: {chartCount} runs | avg {chartSul / Math.Max(1, chartCount):N0} sulphur | avg {chartSec / Math.Max(1, chartCount)}s"
                : "solo charts: none";
            _analyticsStatus = $"loaded {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception e)
        {
            _analyticsStatus = $"load error: {e.Message}";
        }
    }

    // Desenhado DENTRO do settings do plugin, na seção [Submenu] "Zone analytics"
    // (colapso nativo do menu — sem janela própria e sem header manual).
    private void DrawZoneAnalyticsInline()
    {
        if (_analyticsVoyages == null)
        {
            LoadZoneAnalytics();
        }

        if (ImGui.Button("Refresh"))
        {
            LoadZoneAnalytics();
        }

        if (_analyticsStatus != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(Color.Gray.ToImguiVec4(), _analyticsStatus);
        }

        ImGui.TextUnformatted(_analyticsChartsSummary ?? "");

        if (_analyticsVoyages is { Count: > 0 } &&
            ImGui.BeginTable("zoneAnalytics", 15,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Sortable |
                ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit))
        {
            foreach (var header in new[]
                     {
                         "time", "strategy", "dur (min)", "sulphur", "sulphur/min", "cells",
                         "chests", "bottles", "rare", "unique", "divine", "exalted", "chaos", "scarabs", "decks",
                     })
            {
                ImGui.TableSetupColumn(header);
            }

            ImGui.TableHeadersRow();
            ApplyTableSort(_analyticsVoyages, col => col switch
            {
                0 => (a, b) => a.Time.CompareTo(b.Time),
                1 => (a, b) => string.Compare(a.Strategy, b.Strategy, StringComparison.OrdinalIgnoreCase),
                2 => (a, b) => a.Minutes.CompareTo(b.Minutes),
                3 => (a, b) => a.SulGain.CompareTo(b.SulGain),
                4 => (a, b) => a.SulPerMin.CompareTo(b.SulPerMin),
                5 => (a, b) => a.Cells.CompareTo(b.Cells),
                6 => (a, b) => a.Chests.CompareTo(b.Chests),
                7 => (a, b) => a.Bottles.CompareTo(b.Bottles),
                8 => (a, b) => a.Rares.CompareTo(b.Rares),
                9 => (a, b) => a.Uniques.CompareTo(b.Uniques),
                10 => (a, b) => a.Div.CompareTo(b.Div),
                11 => (a, b) => a.Ex.CompareTo(b.Ex),
                12 => (a, b) => a.Chaos.CompareTo(b.Chaos),
                13 => (a, b) => a.Scarabs.CompareTo(b.Scarabs),
                14 => (a, b) => a.Decks.CompareTo(b.Decks),
                _ => null,
            });
            foreach (var r in _analyticsVoyages)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Time.ToString(AnalyticsTimeFormat));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Strategy);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Minutes:F1}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.SulGain:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.SulPerMin:N0}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Cells}/9");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Chests}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Bottles}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Rares}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Uniques}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Div}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Ex}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Chaos}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Scarabs}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted($"{r.Decks}");
            }

            ImGui.EndTable();
        }

        // Sub-div: centenas de solo charts não podem ocupar o menu — agregado por
        // bioma (o "tipo de zona" + colheita média) e os últimos 30 runs.
        if (ImGui.TreeNode("Solo charts"))
        {
            if (_analyticsBiomes is { Count: > 0 } &&
                ImGui.BeginTable("chartBiomes", 7,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Sortable |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit))
            {
                foreach (var header in new[] { "biome", "runs", "avg dur (s)", "avg sulphur", "chests/run", "scarabs/run", "chaos/run" })
                {
                    ImGui.TableSetupColumn(header);
                }

                ImGui.TableHeadersRow();
                ApplyTableSort(_analyticsBiomes, col => col switch
                {
                    0 => (a, b) => string.Compare(a.Biome, b.Biome, StringComparison.OrdinalIgnoreCase),
                    1 => (a, b) => a.Agg.Runs.CompareTo(b.Agg.Runs),
                    2 => (a, b) => (a.Agg.Dur / Math.Max(1, a.Agg.Runs)).CompareTo(b.Agg.Dur / Math.Max(1, b.Agg.Runs)),
                    3 => (a, b) => (a.Agg.Sul / Math.Max(1, a.Agg.Runs)).CompareTo(b.Agg.Sul / Math.Max(1, b.Agg.Runs)),
                    4 => (a, b) => ((double)a.Agg.Chests / Math.Max(1, a.Agg.Runs)).CompareTo((double)b.Agg.Chests / Math.Max(1, b.Agg.Runs)),
                    5 => (a, b) => ((double)a.Agg.Scarabs / Math.Max(1, a.Agg.Runs)).CompareTo((double)b.Agg.Scarabs / Math.Max(1, b.Agg.Runs)),
                    6 => (a, b) => ((double)a.Agg.Chaos / Math.Max(1, a.Agg.Runs)).CompareTo((double)b.Agg.Chaos / Math.Max(1, b.Agg.Runs)),
                    _ => null,
                });
                foreach (var (biome, agg) in _analyticsBiomes)
                {
                    var n = Math.Max(1, agg.Runs);
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(biome);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{agg.Runs}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{agg.Dur / n}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{agg.Sul / n:N0}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{(double)agg.Chests / n:F1}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{(double)agg.Scarabs / n:F1}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{(double)agg.Chaos / n:F1}");
                }

                ImGui.EndTable();
            }

            if (_analyticsCharts is { Count: > 0 } &&
                ImGui.BeginTable("chartRuns", 12,
                    ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Sortable |
                    ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingFixedFit))
            {
                foreach (var header in new[] { "time", "biome", "room", "dur (s)", "sulphur", "chests", "bottles", "rare", "unique", "scarabs", "chaos", "divine/exalted" })
                {
                    ImGui.TableSetupColumn(header);
                }

                ImGui.TableHeadersRow();
                ApplyTableSort(_analyticsCharts, col => col switch
                {
                    0 => (a, b) => a.Time.CompareTo(b.Time),
                    1 => (a, b) => string.Compare(a.Biome, b.Biome, StringComparison.OrdinalIgnoreCase),
                    2 => (a, b) => string.Compare(a.Room, b.Room, StringComparison.OrdinalIgnoreCase),
                    3 => (a, b) => a.DurSec.CompareTo(b.DurSec),
                    4 => (a, b) => a.SulGain.CompareTo(b.SulGain),
                    5 => (a, b) => a.Chests.CompareTo(b.Chests),
                    6 => (a, b) => a.Bottles.CompareTo(b.Bottles),
                    7 => (a, b) => a.Rares.CompareTo(b.Rares),
                    8 => (a, b) => a.Uniques.CompareTo(b.Uniques),
                    9 => (a, b) => a.Scarabs.CompareTo(b.Scarabs),
                    10 => (a, b) => a.Chaos.CompareTo(b.Chaos),
                    11 => (a, b) => (a.Div * 1000 + a.Ex).CompareTo(b.Div * 1000 + b.Ex),
                    _ => null,
                });
                foreach (var r in _analyticsCharts)
                {
                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Time.ToString(AnalyticsTimeFormat));
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(r.Biome);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted(string.IsNullOrEmpty(r.Room) ? "-" : r.Room);
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.DurSec}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.SulGain:N0}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.Chests}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.Bottles}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.Rares}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.Uniques}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.Scarabs}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.Chaos}");
                    ImGui.TableNextColumn();
                    ImGui.TextUnformatted($"{r.Div}/{r.Ex}");
                }

                ImGui.EndTable();
            }

            ImGui.TreePop();
        }

        // ÚLTIMO RECURSO: força o flush do registro em andamento (instância/cache
        // preso). Uso normal continua sendo o flush automático na troca de área —
        // por isso a trava de Shift, no padrão do delete de profile.
        ImGui.Spacing();
        ImGui.TextColored(Color.Gray.ToImguiVec4(), "Last resort (stuck record/cache):");
        ImGui.SameLine();
        if (ImGui.SmallButton("Finalize zone record now (hold Shift)"))
        {
            if (!Input.IsKeyDown(Keys.ShiftKey))
            {
                _analyticsStatus = "hold Shift to confirm";
            }
            else
            {
                FinalizeZoneStats();
                LoadZoneAnalytics();
                _analyticsStatus = "record finalized + reloaded";
            }
        }
    }
}
