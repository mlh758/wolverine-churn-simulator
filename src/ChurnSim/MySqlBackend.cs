using Microsoft.Extensions.Configuration;
using Wolverine;
using Wolverine.MySql;

namespace ChurnSim;

/// <summary>
/// The MySQL arm — the second RDBMS store, and the one that tells a PostgreSQL result apart from
/// an RDBMS result.
///
/// Same ChurnSim, same <see cref="SimAgentFamily"/>, same control-plane timers as the other two
/// arms; only the message store changes. What makes this arm worth running next to PostgreSQL is
/// that the leadership machinery is the <em>same shape</em> but not the same mechanism, so a
/// finding that reproduces on both is Wolverine's and a finding that reproduces on one is the
/// driver's or the server's:
///
/// <list type="number">
///   <item>
///     <b>The leadership lock is a session-scoped NAMED lock</b>, not an advisory lock on an
///     integer pair: <c>MySqlAdvisoryLock</c> takes <c>GET_LOCK('wolverine_&lt;id&gt;', 0)</c> on
///     one long-lived connection, where <c>&lt;id&gt;</c> is the same
///     <c>schemaName.GetDeterministicHashCode()</c> the PostgreSQL store derives. Same id, same
///     "the death of the session IS the release" semantics, so every session-lock checker
///     (S1/S3/S4/S11) applies here unchanged — which is exactly why this arm is cheap.
///   </item>
///   <item>
///     <b>MySQL named locks STACK, and PostgreSQL's session advisory locks do too</b> — but MySQL
///     is the store where that bit Wolverine in a shipped test
///     (<c>Bug_advisory_lock_stacking_blocks_failover</c>): <c>GET_LOCK</c> on a name this session
///     already holds increments a hold count that one <c>RELEASE_LOCK</c> does not clear, and the
///     failover then stalls with nothing logged. <c>MySqlAdvisoryLock.TryAttainLockAsync</c>
///     short-circuits on <c>hasLockUnsafe</c> to avoid it. S4 is the check that would see it from
///     outside, and it is worth more here than anywhere else.
///   </item>
///   <item>
///     <b>In MySQL the schema IS a database.</b> <c>PersistMessagesWithMySql(.., "wolverine")</c>
///     puts every table in a database called <c>wolverine</c>, which Weasel creates on the first
///     migration — so this app connects to <c>churnsim</c> (which the server image creates) and
///     never to the schema it is about to build. That is deliberate: <c>just reset-schema</c>
///     drops the <c>wolverine</c> database, and a pod whose connection string named it would then
///     fail to connect at all rather than rebuild it.
///   </item>
/// </list>
///
/// One measurement difference to know before reading a number off this arm: Weasel maps
/// <c>DateTimeOffset</c> to a bare <c>DATETIME</c> on MySQL, so <c>health_check</c> and
/// <c>started</c> carry WHOLE SECONDS here where PostgreSQL's <c>timestamptz</c> carries
/// microseconds. ChurnSim heartbeats every 2s, so S11's "stopped heartbeating" still resolves;
/// nothing sub-second should be read out of this arm's timestamps.
///
/// Compiled only when the image is built with <c>-p:SimBackend=mysql</c>; see
/// <see cref="PostgresBackend"/>'s and <see cref="RavenDbBackend"/>'s files for the other halves
/// and ChurnSim.csproj for the switch.
/// </summary>
internal static class SimBackend
{
    /// <summary>Must match the SIM_BACKEND env var the deployment declares; Program.cs asserts it.</summary>
    public const string Name = "mysql";

    /// <summary>
    /// The Wolverine schema, which on MySQL is a database name. Held here rather than inlined
    /// because the leadership lock id is derived from it
    /// (<c>MySqlNodePersistence._lockId = schemaName.GetDeterministicHashCode()</c>) and SafetyLab
    /// derives the same id from <c>--schema</c>; the two have to name the same thing or the
    /// monitor watches a lock nobody takes and every leader check passes over nothing.
    /// </summary>
    public const string Schema = "wolverine";

    public static void Configure(WolverineOptions opts, IConfiguration config, SimLog emit)
    {
        var settings = config.Get<MySqlOptions>() ?? new MySqlOptions();

        opts.PersistMessagesWithMySql(settings.ConnectionString, Schema);

        emit("ChurnSim.Startup", $"CONFIG backend=mysql schema={Schema}",
            new Dictionary<string, object?> { ["Setting"] = "SIM_BACKEND", ["Value"] = Name });
    }
}

/// <summary>Separate from <see cref="SimOptions"/>: only one backend file compiles.</summary>
internal sealed class MySqlOptions
{
    /// <summary>
    /// Names the BOOTSTRAP database (<c>churnsim</c>), not the Wolverine schema. See
    /// <see cref="SimBackend"/> for why those are different on this arm.
    /// </summary>
    [ConfigurationKeyName("MYSQL_CONNECTION")]
    public string ConnectionString { get; set; } =
        "Server=localhost;Port=3307;Database=churnsim;User ID=root;Password=churnsim";
}
