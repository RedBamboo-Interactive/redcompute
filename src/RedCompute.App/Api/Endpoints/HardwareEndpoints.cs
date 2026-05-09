using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RedCompute.App.Services.Hardware;

namespace RedCompute.App.Api.Endpoints;

public static class HardwareEndpoints
{
    public static void Map(WebApplication app, HardwareMonitorService hardwareMonitor)
    {
        app.MapGet("/hardware", () =>
        {
            var snapshot = hardwareMonitor.GetSnapshot();
            if (snapshot == null)
                return Results.Ok(new { available = false, message = "Hardware monitoring not yet initialized" });

            return Results.Ok(new
            {
                available = true,
                snapshot.Timestamp,
                cpu = new
                {
                    usagePercent = snapshot.Cpu.UsagePercent,
                    coreCount = snapshot.Cpu.CoreCount
                },
                ram = new
                {
                    totalBytes = snapshot.Ram.TotalBytes,
                    usedBytes = snapshot.Ram.UsedBytes,
                    availableBytes = snapshot.Ram.AvailableBytes,
                    usagePercent = snapshot.Ram.UsagePercent
                },
                gpus = snapshot.Gpus.Select(g => new
                {
                    g.Index,
                    name = g.Name,
                    memory = new
                    {
                        totalBytes = g.Memory.TotalBytes,
                        usedBytes = g.Memory.UsedBytes,
                        freeBytes = g.Memory.FreeBytes
                    },
                    utilizationPercent = g.UtilizationPercent,
                    memoryUtilizationPercent = g.MemoryUtilizationPercent,
                    powerWatts = g.PowerWatts,
                    powerLimitWatts = g.PowerLimitWatts,
                    temperatureCelsius = g.TemperatureCelsius,
                    graphicsClockMHz = g.GraphicsClockMHz,
                    memoryClockMHz = g.MemoryClockMHz,
                    processes = g.Processes.Select(p => new
                    {
                        pid = p.Pid,
                        processName = p.ProcessName,
                        usedMemoryBytes = p.UsedMemoryBytes,
                        capabilitySlug = p.CapabilitySlug
                    }),
                    capabilityVram = g.CapabilityVram
                })
            });
        });
    }
}
