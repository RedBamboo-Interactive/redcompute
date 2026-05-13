using System.IO;
using System.Text.Json;
using RedCompute.Core.Capabilities;
using RedCompute.Core.Configuration;

namespace RedCompute.App.Services;

public class CapabilityManifestLoader
{
    private static readonly string UserCapabilitiesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RedCompute", "capabilities");

    private static readonly string ShippedCapabilitiesDir = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "capabilities");

    private static readonly string ProductionCapabilitiesDir = Path.Combine(
        AppContext.BaseDirectory, "capabilities");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public CapabilityDefinition Load(string slug, CapabilityConfig? capConfig = null)
    {
        var definition = new CapabilityDefinition
        {
            Slug = slug,
            DisplayName = TitleCase(slug)
        };

        // Layer 1: shipped manifest (repo dev path or production)
        var shipped = Path.Combine(ShippedCapabilitiesDir, $"{slug}.json");
        if (!File.Exists(shipped))
            shipped = Path.Combine(ProductionCapabilitiesDir, $"{slug}.json");
        if (File.Exists(shipped))
            MergeFromFile(definition, shipped);

        // Layer 2: plugin-shipped manifests (scan plugin dirs)
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(pluginsDir))
        {
            foreach (var dir in Directory.GetDirectories(pluginsDir))
            {
                var pluginManifest = Path.Combine(dir, "capabilities", $"{slug}.json");
                if (File.Exists(pluginManifest))
                    MergeFromFile(definition, pluginManifest);
            }
        }

        // Layer 3: user overrides
        var userFile = Path.Combine(UserCapabilitiesDir, $"{slug}.json");
        if (File.Exists(userFile))
            MergeFromFile(definition, userFile);

        // Layer 4: config overrides
        if (capConfig != null)
        {
            if (capConfig.DisplayName != null) definition.DisplayName = capConfig.DisplayName;
            if (capConfig.Icon != null) definition.Icon = capConfig.Icon;
            if (capConfig.Color != null) definition.Color = capConfig.Color;
        }

        // Fallback defaults
        definition.Icon ??= "fa-solid fa-cube";
        definition.Color ??= "#6B7280";

        return definition;
    }

    private static void MergeFromFile(CapabilityDefinition def, string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var manifest = JsonSerializer.Deserialize<CapabilityManifestJson>(json, JsonOptions);
            if (manifest == null) return;

            if (manifest.DisplayName != null) def.DisplayName = manifest.DisplayName;
            if (manifest.Description != null) def.Description = manifest.Description;
            if (manifest.Icon != null) def.Icon = manifest.Icon;
            if (manifest.Color != null) def.Color = manifest.Color;
            if (manifest.Category != null) def.Category = manifest.Category;
        }
        catch { }
    }

    private static string TitleCase(string slug)
    {
        return string.Join(' ', slug.Split('-').Select(w =>
            w.Length > 0 ? char.ToUpper(w[0]) + w[1..] : w));
    }

    private class CapabilityManifestJson
    {
        public string? DisplayName { get; set; }
        public string? Description { get; set; }
        public string? Icon { get; set; }
        public string? Color { get; set; }
        public string? Category { get; set; }
    }
}
