using ChurnSim;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Postgresql;
using Wolverine.Runtime.Agents;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                       ?? "Host=localhost;Port=5433;Database=churnsim;Username=postgres;Password=postgres";

builder.UseWolverine(opts =>
{
    opts.ServiceName = "churnsim";

    // No message handlers needed -- the point is the agent assignment plane
    opts.Discovery.DisableConventionalDiscovery();

    opts.PersistMessagesWithPostgresql(connectionString, "wolverine");

    // Tighten the control-plane timers a bit so a short simulated rollout
    // exercises several health-check / assignment cycles. These stay well
    // within realistic production shapes (defaults are 10s / 30s).
    opts.Durability.HealthCheckPollingTime = TimeSpan.FromSeconds(2);
    opts.Durability.CheckAssignmentPeriod = TimeSpan.FromSeconds(5);

    // The GH-3987/GH-3959 proposal settings only exist on the patched build
    // (6.33.0-proposal.*). Set them reflectively from env vars so this same
    // program runs unchanged against released 5.39, stock main, and the
    // proposal build -- on builds without the property the knob is just
    // reported as unavailable.
    void trySet(string property, object value)
    {
        var prop = opts.Durability.GetType().GetProperty(property);
        if (prop?.CanWrite == true)
        {
            prop.SetValue(opts.Durability, value);
            Console.WriteLine($"CONFIG {property}={value}");
        }
        else
        {
            Console.WriteLine($"CONFIG {property} not available on this Wolverine build");
        }
    }

    if (int.TryParse(Environment.GetEnvironmentVariable("SIM_STABILITY_WINDOW_SECONDS"), out var window) && window > 0)
    {
        trySet("AssignmentStabilityWindow", TimeSpan.FromSeconds(window));
    }

    if (Environment.GetEnvironmentVariable("SIM_CAPACITY_AWARE") == "true")
    {
        trySet("CapacityAwareAssignment", true);

        if (double.TryParse(Environment.GetEnvironmentVariable("SIM_OVERLOAD_THRESHOLD"), out var threshold) && threshold > 0)
        {
            trySet("NodeOverloadThreshold", threshold);
        }
    }

    opts.Services.AddSingleton<IAgentFamily, SimAgentFamily>();
});

await builder.Build().RunAsync();
