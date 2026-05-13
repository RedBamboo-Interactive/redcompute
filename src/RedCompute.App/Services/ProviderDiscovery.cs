using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using RedCompute.Core.Configuration;
using RedCompute.Core.Providers;

namespace RedCompute.App.Services;

public class ProviderDiscovery
{
    private readonly Dictionary<string, Type> _providerTypes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;

    public ProviderDiscovery(Action<string> log)
    {
        _log = log;
    }

    public void ScanAssemblies()
    {
        // Scan the main app assembly
        ScanAssembly(Assembly.GetExecutingAssembly());

        // Scan referenced assemblies that might contain providers
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.FullName?.StartsWith("RedCompute.Plugin") == true)
                ScanAssembly(asm);
        }

        // Scan plugin DLLs from plugins/ directory
        var pluginsDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (Directory.Exists(pluginsDir))
        {
            foreach (var dll in Directory.GetFiles(pluginsDir, "RedCompute.Plugin.*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                    ScanAssembly(asm);
                }
                catch (Exception ex)
                {
                    _log($"[Discovery] Failed to load plugin {Path.GetFileName(dll)}: {ex.Message}");
                }
            }
        }

        // Register known aliases
        if (_providerTypes.ContainsKey("LocalWsl") && !_providerTypes.ContainsKey("LocalNative"))
            _providerTypes["LocalNative"] = _providerTypes["LocalWsl"];

        _log($"[Discovery] Found {_providerTypes.Count} provider type(s): {string.Join(", ", _providerTypes.Keys)}");
    }

    private void ScanAssembly(Assembly assembly)
    {
        try
        {
            foreach (var type in assembly.GetTypes())
            {
                if (type.IsAbstract || type.IsInterface) continue;
                if (!typeof(IPluginProvider).IsAssignableFrom(type)) continue;

                // Get the ProviderType from a temporary instance or from a static field
                // We use the convention that the constructor takes (ProviderConfig, string, Action<string>)
                // and read ProviderType after construction with dummy values
                var providerType = GetProviderTypeFromClass(type);
                if (providerType != null)
                {
                    _providerTypes[providerType] = type;
                    _log($"[Discovery] Registered provider type '{providerType}' from {type.FullName}");
                }
            }
        }
        catch (ReflectionTypeLoadException ex)
        {
            _log($"[Discovery] Partial load of {assembly.GetName().Name}: {ex.Message}");
            foreach (var type in ex.Types.Where(t => t != null))
            {
                if (type!.IsAbstract || type.IsInterface) continue;
                if (!typeof(IPluginProvider).IsAssignableFrom(type)) continue;
                var pt = GetProviderTypeFromClass(type);
                if (pt != null) _providerTypes[pt] = type;
            }
        }
        catch (Exception ex)
        {
            _log($"[Discovery] Failed to scan {assembly.GetName().Name}: {ex.Message}");
        }
    }

    private string? GetProviderTypeFromClass(Type type)
    {
        // Preferred: static ProviderTypeName property (no instantiation needed)
        var staticProp = type.GetProperty("ProviderTypeName", BindingFlags.Public | BindingFlags.Static);
        if (staticProp != null)
            return staticProp.GetValue(null)?.ToString();

        // Fallback: convention — class name minus "Provider" suffix
        var name = type.Name;
        if (name.EndsWith("Provider") && name.Length > 8)
            return name[..^8];

        return name;
    }

    public IPluginProvider? Create(string providerType, ProviderConfig config, string capabilitySlug, Action<string> log, object?[]? extraServices = null)
    {
        if (!_providerTypes.TryGetValue(providerType, out var type))
        {
            _log($"[Discovery] Unknown provider type: '{providerType}'. Available: {string.Join(", ", _providerTypes.Keys)}");
            return null;
        }

        try
        {
            return CreateInstance(type, config, capabilitySlug, log, extraServices) as IPluginProvider;
        }
        catch (Exception ex)
        {
            _log($"[Discovery] Failed to create provider '{providerType}': {ex.Message}");
            return null;
        }
    }

    private static object? CreateInstance(Type type, ProviderConfig config, string capabilitySlug, Action<string> log, object?[]? extraServices = null)
    {
        var constructors = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .ToList();

        foreach (var ctor in constructors)
        {
            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];
            var canResolve = true;

            for (int i = 0; i < parameters.Length; i++)
            {
                var paramType = parameters[i].ParameterType;

                if (paramType == typeof(ProviderConfig))
                    args[i] = config;
                else if (paramType == typeof(string) && parameters[i].Name == "capabilitySlug")
                    args[i] = capabilitySlug;
                else if (paramType == typeof(Action<string>))
                    args[i] = log;
                else if (extraServices != null)
                {
                    var match = extraServices.FirstOrDefault(s => s != null && paramType.IsAssignableFrom(s.GetType()));
                    if (match != null)
                        args[i] = match;
                    else if (parameters[i].HasDefaultValue)
                        args[i] = parameters[i].DefaultValue;
                    else
                        canResolve = false;
                }
                else if (parameters[i].HasDefaultValue)
                    args[i] = parameters[i].DefaultValue;
                else
                    canResolve = false;

                if (!canResolve) break;
            }

            if (canResolve)
                return ctor.Invoke(args);
        }

        throw new InvalidOperationException(
            $"No suitable constructor found for {type.FullName}. " +
            $"Expected (ProviderConfig config, string capabilitySlug, Action<string> log)");
    }

    public IReadOnlyCollection<string> AvailableTypes => _providerTypes.Keys;
}
