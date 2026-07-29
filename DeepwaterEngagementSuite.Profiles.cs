using ExileCore;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DeepwaterEngagementSuite;

public partial class DeepwaterEngagementSuite
{
    private void EnsureDefaultProfile()
    {
        var existing = Directory.GetFiles(_profilesDirectory, "*.json");
        if (existing.Length > 0)
            return;
        var defaultJsonPath = Path.Combine(DirectoryFullName, "profiles", "default.json");
        if (File.Exists(defaultJsonPath))
        {
            File.Copy(defaultJsonPath, Path.Combine(_profilesDirectory, "Default.json"), false);
        }
        else
        {
            LogError("Default profile template is missing");
        }
    }

    private void LoadProfiles()
    {
        Settings.VoyageSettings.Profiles.Clear();
        var files = Directory.GetFiles(_profilesDirectory, "*.json");
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var json = File.ReadAllText(file);
                var profile = JsonConvert.DeserializeObject<VoyageProfile>(json, ProfileJsonSettings);
                if (profile != null)
                {
                    Settings.VoyageSettings.Profiles.Add(new VoyageProfileEntry { Name = name, Profile = profile });
                }
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"Failed to load profile '{name}': {ex.Message}");
            }
        }

        Settings.VoyageSettings.ProfileSelector.Values = Settings.VoyageSettings.Profiles.Select(p => p.Name).ToList();
        if (!Settings.VoyageSettings.Profiles.Any(p => p.Name == Settings.VoyageSettings.ProfileSelector.Value) &&
            Settings.VoyageSettings.Profiles.Count > 0)
        {
            Settings.VoyageSettings.ProfileSelector.Value = Settings.VoyageSettings.Profiles[0].Name;
        }
    }

    private void SaveProfiles()
    {
        foreach (var entry in Settings.VoyageSettings.Profiles)
        {
            var path = Path.Combine(_profilesDirectory, $"{entry.Name}.json");
            try
            {
                var json = JsonConvert.SerializeObject(entry.Profile, Formatting.Indented, ProfileJsonSettings);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                DebugWindow.LogError($"Failed to save profile '{entry.Name}': {ex.Message}");
            }
        }
    }

    private void ApplyProfile(string name)
    {
        _pendingProfileName = null;
        var vs = Settings.VoyageSettings;
        var entry = vs.Profiles.FirstOrDefault(p => p.Name == name);
        if (entry == null)
            return;
        vs.ProfileSelector.Value = name;
        vs.BorderModifiers.Content.Clear();
        vs.ChartModifiers.Content.Clear();
        foreach (var mod in entry.Profile.BorderModifiers)
        {
            vs.BorderModifiers.Content.Add(mod);
        }

        foreach (var mod in entry.Profile.ChartModifiers)
        {
            vs.ChartModifiers.Content.Add(mod);
        }

        vs.PositionWeights = entry.Profile.PositionWeights is { Length: 3 } pw
            ? pw.Select(r => r.ToArray()).ToArray()
            : VoyageSettings.DefaultPositionWeights();
    }

    private void OnProfileSelected(string name)
    {
        if (Settings.VoyageSettings.ProfileSelector.Value != name)
        {
            SyncCurrentProfileToMemory();
        }

        ApplyProfile(name);
    }

    private void SyncCurrentProfileToMemory()
    {
        var name = Settings.VoyageSettings.ProfileSelector.Value;
        if (string.IsNullOrEmpty(name))
            return;
        var entry = Settings.VoyageSettings.Profiles.FirstOrDefault(p => p.Name == name);
        if (entry == null)
            return;
        entry.Profile.BorderModifiers.Clear();
        entry.Profile.ChartModifiers.Clear();
        foreach (var mod in Settings.VoyageSettings.BorderModifiers.Content)
        {
            entry.Profile.BorderModifiers.Add(mod);
        }

        foreach (var mod in Settings.VoyageSettings.ChartModifiers.Content)
        {
            entry.Profile.ChartModifiers.Add(mod);
        }

        entry.Profile.PositionWeights = Settings.VoyageSettings.PositionWeights.Select(r => r.ToArray()).ToArray();

        var path = Path.Combine(_profilesDirectory, $"{name}.json");
        try
        {
            var json = JsonConvert.SerializeObject(entry.Profile, Formatting.Indented, ProfileJsonSettings);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"Failed to save profile '{name}': {ex.Message}");
        }
    }

    private void OnAddProfile()
    {
        var baseName = "NewProfile";
        var name = baseName;
        var counter = 1;
        while (Settings.VoyageSettings.Profiles.Any(p => p.Name == name))
        {
            name = $"{baseName}{counter++}";
        }

        var templatePath = Path.Combine(DirectoryFullName, "profiles", "default.json");
        var targetPath = Path.Combine(_profilesDirectory, $"{name}.json");
        if (File.Exists(templatePath))
        {
            File.Copy(templatePath, targetPath, false);
        }
        else
        {
            File.WriteAllText(targetPath, JsonConvert.SerializeObject(new VoyageProfile(), Formatting.Indented, ProfileJsonSettings));
        }

        LoadProfiles();
        ApplyProfile(name);
    }

    private void OnReloadProfiles()
    {
        LoadProfiles();
        if (Settings.VoyageSettings.ProfileSelector.Value != null)
        {
            ApplyProfile(Settings.VoyageSettings.ProfileSelector.Value);
        }
    }

    private void OnDeleteCurrentProfile()
    {
        if (!Input.IsKeyDown(Keys.ShiftKey))
        {
            DebugWindow.LogMsg("Hold Shift to delete profile", 20, Color.Yellow);
            return;
        }

        var name = Settings.VoyageSettings.ProfileSelector.Value;
        if (string.IsNullOrEmpty(name))
            return;
        var path = Path.Combine(_profilesDirectory, $"{name}.json");
        if (File.Exists(path))
            File.Delete(path);
        LoadProfiles();
        Settings.VoyageSettings.BorderModifiers.Content.Clear();
        Settings.VoyageSettings.ChartModifiers.Content.Clear();
        if (Settings.VoyageSettings.Profiles.Count > 0)
        {
            ApplyProfile(Settings.VoyageSettings.Profiles[0].Name);
        }
    }

    private string _pendingProfileName;

    private void DrawProfileRenameNode()
    {
        var vs = Settings.VoyageSettings;
        if (string.IsNullOrEmpty(vs.ProfileSelector.Value))
        {
            ImGui.Text("No profile selected");
            return;
        }

        _pendingProfileName ??= vs.ProfileSelector.Value;
        if (ImGui.InputText("##ProfileName", ref _pendingProfileName, 64))
        {
            if (string.IsNullOrWhiteSpace(_pendingProfileName))
            {
                _pendingProfileName = vs.ProfileSelector.Value;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Rename"))
        {
            if (string.IsNullOrWhiteSpace(_pendingProfileName) || _pendingProfileName == vs.ProfileSelector.Value || vs.Profiles.Any(p => p.Name == _pendingProfileName))
                return;
            SyncCurrentProfileToMemory();
            var entry = vs.Profiles.FirstOrDefault(p => p.Name == vs.ProfileSelector.Value);
            if (entry != null)
            {
                var oldPath = Path.Combine(_profilesDirectory, $"{entry.Name}.json");
                var newPath = Path.Combine(_profilesDirectory, $"{_pendingProfileName}.json");
                if (File.Exists(oldPath))
                    File.Move(oldPath, newPath);
                entry.Name = _pendingProfileName;
                vs.ProfileSelector.Values = vs.Profiles.Select(p => p.Name).ToList();
                vs.ProfileSelector.Value = _pendingProfileName;
                _pendingProfileName = null;
            }
        }
    }

    private static readonly JsonSerializerSettings ProfileJsonSettings = new()
    {
        Converters = new List<JsonConverter>
        {
            new TextNodeConverter(),
            new RangeNodeFloatConverter(),
            new ToggleNodeConverter(),
            new ColorNodeConverter(),
        },
    };

    private string _profilesDirectory;
}