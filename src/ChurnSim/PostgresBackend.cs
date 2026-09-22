using Microsoft.Extensions.Configuration;
using Wolverine;
using Wolverine.Postgresql;

namespace ChurnSim;

/// <summary>
/// The PostgreSQL arm — the original and the baseline every result in RESULTS.md was taken on.
///
/// Compiled only when the image is built without <c>-p:SimBackend=ravendb</c>; see
/// <see cref="RavenDbBackend"/> for the other half and ChurnSim.csproj for the switch. The two
/// files are mutually exclusive rather than an if/else because the package references are too:
/// a WolverineFx.RavenDb of a locally packed version only exists if that project was packed,
/// and a Postgres run must not be made to depend on it.
/// </summary>
internal static class SimBackend
{
    /// <summary>Must match the SIM_BACKEND env var the deployment declares; Program.cs asserts it.</summary>
    public const string Name = "postgres";

    public static void Configure(WolverineOptions opts, IConfiguration config, SimLog emit)
    {
        var settings = config.Get<PostgresOptions>() ?? new PostgresOptions();

        opts.PersistMessagesWithPostgresql(settings.ConnectionString, "wolverine");

        emit("ChurnSim.Startup", "CONFIG backend=postgres schema=wolverine",
            new Dictionary<string, object?> { ["Setting"] = "SIM_BACKEND", ["Value"] = Name });
    }
}

/// <summary>Separate from <see cref="SimOptions"/>: only one backend file compiles.</summary>
internal sealed class PostgresOptions
{
    [ConfigurationKeyName("POSTGRES_CONNECTION")]
    public string ConnectionString { get; set; } =
        "Host=localhost;Port=5433;Database=churnsim;Username=postgres;Password=postgres";
}
