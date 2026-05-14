namespace RedCompute.PluginSdk;

public interface IPluginEventSource
{
    event Action<string, object>? PluginEvent;
}
