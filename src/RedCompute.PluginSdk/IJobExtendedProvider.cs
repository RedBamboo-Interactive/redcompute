namespace RedCompute.PluginSdk;

public interface IJobExtendedProvider
{
    void CancelJob(string jobKey);
    Dictionary<Guid, string> GetJobSubStatuses(IEnumerable<Guid> jobIds);
}
