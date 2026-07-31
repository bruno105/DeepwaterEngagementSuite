using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
        string Time, string Strategy, double Minutes, int SulGain, double SulPerMin,
        int Cells, int Chests, int Div, int Ex, int Chaos, int Scarabs, int Decks);

    private List<ZoneAnalyticsRow> _analyticsVoyages;
    private string _analyticsChartsSummary;
    private string _analyticsStatus;

    private void LoadZoneAnalytics()
    {
        try
        {
            var path = Path.Combine(ConfigDirectory, "zone_stats.jsonl");
            var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
            var voyages = new List<ZoneAnalyticsRow>();
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
                if (record["chests"] is JObject chestsObj)
                {
                    foreach (var p in chestsObj.Properties())
                    {
                        chests += (int?)p.Value ?? 0;
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
                    ? dt.ToLocalTime().ToString("HH:mm")
                    : "?";
                var strat = (string)record["planned"]?["strategy"];
                var minutes = dur / 60.0;
                voyages.Add(new ZoneAnalyticsRow(time, string.IsNullOrEmpty(strat) ? "?" : strat,
                    minutes, gain, minutes > 0.1 ? gain / minutes : 0,
                    cells, chests, div, ex, chaos, scarabs, decks));
            }

            voyages.Reverse();
            _analyticsVoyages = voyages.Take(15).ToList();
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

    private void DrawZoneAnalytics()
    {
        if (!Settings.ShowZoneAnalytics.Value)
        {
            return;
        }

        if (!ImGui.Begin("Zone Analytics"))
        {
            ImGui.End();
            return;
        }

        if (_analyticsVoyages == null)
        {
            LoadZoneAnalytics();
        }

        if (ImGui.Button("Refresh"))
        {
            LoadZoneAnalytics();
        }

        ImGui.SameLine();
        if (ImGui.Button("Finalize zone record now"))
        {
            FinalizeZoneStats();
            LoadZoneAnalytics();
            _analyticsStatus = "record finalized + reloaded";
        }

        if (_analyticsStatus != null)
        {
            ImGui.SameLine();
            ImGui.TextColored(Color.Gray.ToImguiVec4(), _analyticsStatus);
        }

        ImGui.TextUnformatted(_analyticsChartsSummary ?? "");

        if (_analyticsVoyages is { Count: > 0 } &&
            ImGui.BeginTable("zoneAnalytics", 12, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            foreach (var header in new[]
                     {
                         "time", "strategy", "min", "sulphur", "sul/min", "cells",
                         "chests", "div", "ex", "chaos", "scarab", "deck",
                     })
            {
                ImGui.TableSetupColumn(header);
            }

            ImGui.TableHeadersRow();
            foreach (var r in _analyticsVoyages)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(r.Time);
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

        ImGui.End();
    }
}
