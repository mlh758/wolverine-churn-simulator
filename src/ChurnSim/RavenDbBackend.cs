using Microsoft.Extensions.DependencyInjection;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using Wolverine;
using Wolverine.RavenDb;

namespace ChurnSim;

/// <summary>
/// The RavenDB arm. Same ChurnSim, same <see cref="SimAgentFamily"/>, same control-plane timers —
/// only the message store changes, which is the point: everything the assignment plane does is
/// held constant so the difference in the results is the backend's semantics and nothing else.
///
/// Those semantics differ from the RDBMS stores in three ways that matter to this rig:
///
/// 1. <b>The leadership lock is a compare-exchange value with a five-minute expiry</b>
///    (<c>RavenDbMessageStore.Locking</c>), not a session-scoped advisory lock. A PostgreSQL
///    leader that dies releases its lock the instant its backend goes away; a RavenDB leader that
///    dies leaves <c>wolverine/leader/&lt;service&gt;</c> sitting in the cluster until a peer
///    notices it has expired and CAS-replaces it. That is a failover-stall window with a
///    five-minute ceiling and no server-side actor to close it — see SafetyLab's S8.
/// 2. <b><c>HasLeadershipLock()</c> is pure in-process belief.</b> It reads a local field and its
///    expiry; it never asks the server who currently owns the key. This is exactly the
///    belief-versus-server split that GH-2602 was, so S3 is worth more here than on Postgres.
/// 3. <b>Assignments are documents, not rows.</b> One <c>AgentAssignments</c> document per agent,
///    keyed by a munged agent URI, so single-owner-per-agent is enforced by document identity
///    rather than by a primary key — but reads of the set go through queries, and query results
///    carry an <c>IsStale</c> flag that a SQL read never has to. The monitor records it.
///
/// Compiled only when the image is built with <c>-p:SimBackend=ravendb</c>; see
/// <see cref="PostgresBackend"/>'s file for the other half and ChurnSim.csproj for the switch.
/// </summary>
internal static class SimBackend
{
    /// <summary>Must match the SIM_BACKEND env var the deployment declares; Program.cs asserts it.</summary>
    public const string Name = "ravendb";

    public static void Configure(WolverineOptions opts, SimLog emit)
    {
        var urls = (Environment.GetEnvironmentVariable("RAVENDB_URLS") ?? "http://ravendb:8080")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var database = Environment.GetEnvironmentVariable("RAVENDB_DATABASE") ?? "churnsim";

        requireNativeControlQueue();

        // RAVENDB_PIN_NODE=true: this node talks ONLY to the url(s) it was given and never learns
        // the rest of the cluster. The RavenDB client fetches the database group's topology on
        // startup and fails over to any member it can reach, which is the right behaviour for an
        // application and the wrong one for a partition experiment: cut this pod's member off and
        // the client quietly reroutes to the majority, so there is no minority side to observe and
        // the run measures client failover instead of a split. The replicated arm pins each pod to
        // its own store member by StatefulSet ordinal (k8s/churnsim-ravendb-cluster.yaml). Off by
        // default, so the single-node arm and every earlier result are unchanged.
        var pinNode = Environment.GetEnvironmentVariable("RAVENDB_PIN_NODE") == "true";

        // RAVENDB_REPLICATION_FACTOR: what ensureDatabaseReady creates the database with when it
        // finds none. Default 1, which is what every single-node run used. The replicated arm
        // forms its cluster and creates the database at factor 3 BEFORE any pod starts (deploy.sh
        // runs `safetylab raven-cluster form`), so this only matters after a `reset-schema` on
        // that arm — where recreating at factor 1 would silently turn a three-member cluster into
        // three servers replicating nothing.
        var replicationFactor =
            int.TryParse(Environment.GetEnvironmentVariable("RAVENDB_REPLICATION_FACTOR"), out var rf) && rf > 0
                ? rf
                : 1;

        var store = new DocumentStore
        {
            Urls = urls,
            Database = database,
            // Surfaces in RavenDbMessageStore.Name / Describe(), so a CritterWatch or log read
            // names the database rather than an empty string.
            Identifier = database
        };

        if (pinNode)
        {
            store.Conventions.DisableTopologyUpdates = true;
        }

        store.Initialize();
        ensureDatabaseReady(store, database, replicationFactor, emit);

        emit("ChurnSim.Startup", $"CONFIG RAVENDB_PIN_NODE={pinNode.ToString().ToLowerInvariant()}",
            new Dictionary<string, object?>
            {
                ["Setting"] = "RAVENDB_PIN_NODE",
                ["Value"] = pinNode.ToString().ToLowerInvariant()
            });
        emit("ChurnSim.Startup", $"CONFIG RAVENDB_REPLICATION_FACTOR={replicationFactor}",
            new Dictionary<string, object?>
            {
                ["Setting"] = "RAVENDB_REPLICATION_FACTOR",
                ["Value"] = replicationFactor.ToString()
            });

        // Wolverine's RavenDb package takes the store from the container rather than building
        // one, so the app owns its lifetime. Nothing here disposes it: the process only ever
        // ends by pod termination, and a disposed store during shutdown would turn the node's
        // final deregistration into an ObjectDisposedException.
        opts.Services.AddSingleton<IDocumentStore>(store);

        opts.UseRavenDbPersistence();

        emit("ChurnSim.Startup", $"CONFIG backend=ravendb urls={string.Join(",", urls)} database={database}",
            new Dictionary<string, object?>
            {
                ["Setting"] = "SIM_BACKEND",
                ["Value"] = Name,
                ["Urls"] = string.Join(",", urls),
                ["Database"] = database
            });
    }

    /// <summary>
    /// Under Balanced durability every node needs a control endpoint to exchange agent commands,
    /// and the only one that works for RavenDB in Kubernetes is the native <c>ravendb://</c>
    /// control queue that landed in Wolverine on 2026-07-02. Without it the runtime dies on
    /// <c>WolverineNode.For</c>'s "ControlEndpoint cannot be null for this usage", which says
    /// nothing about the actual cause.
    ///
    /// <c>UseTcpForControlEndpoint()</c> is NOT a usable fallback here: it advertises
    /// <c>tcp://localhost:&lt;port&gt;</c>, so in a pod-per-node cluster every peer would dial its
    /// own loopback. There is no escape hatch — the version under test either has the control
    /// queue or cannot run this arm.
    /// </summary>
    private static void requireNativeControlQueue()
    {
        var transport = typeof(WolverineRavenDbExtensions).Assembly
            .GetType("Wolverine.RavenDb.Internals.Transport.RavenDbControlTransport");

        if (transport is not null) return;

        throw new InvalidOperationException(
            "This WolverineFx.RavenDb build has no native RavenDB control queue " +
            "(Wolverine.RavenDb.Internals.Transport.RavenDbControlTransport is missing), so Balanced " +
            "durability cannot elect a control endpoint and the agent assignment plane will not start. " +
            "The control queue landed on 2026-07-02; pack a Wolverine build from after that date. " +
            "UseTcpForControlEndpoint() is not a substitute — it advertises tcp://localhost, which no " +
            "peer pod can reach.");
    }

    /// <summary>
    /// Make the database exist AND serve requests before Wolverine is handed the store, and do
    /// not care which of the several transients showed up on the way.
    ///
    /// This is not defensive padding; without it the RavenDB arm loses a pod on every cold start.
    /// RavenDB has no <c>CREATE DATABASE IF NOT EXISTS</c> and the client creates nothing
    /// implicitly, so the first pod up has to create it — and all three replicas start together
    /// and all three find no record. What happens to the losers depends on exactly where in the
    /// winner's create their own request lands, and it is not one failure:
    ///
    /// <list type="bullet">
    ///   <item>
    ///     <c>ConcurrencyException</c> — the Raft command was rejected because the record now
    ///     exists. The benign case, and the only one originally handled.
    ///   </item>
    ///   <item>
    ///     <c>DatabaseDisabledException</c> as a 503 out of <c>PUT /admin/databases</c> — the
    ///     server is inside <c>UnloadAndLockDatabaseImpl</c> ("unloaded and locked because
    ///     Checking if we need to recreate indexes") on the database it has just created, and
    ///     refuses the second create outright. Measured: one of three pods died here on a fresh
    ///     server, every time.
    ///   </item>
    ///   <item>
    ///     The same exception from the first ordinary operation afterwards, because that unload
    ///     window opens asynchronously and can land after a create that succeeded.
    ///   </item>
    /// </list>
    ///
    /// Any of those propagates out of <c>UseWolverine</c> and takes the host with it, which in
    /// Kubernetes is CrashLoopBackOff with exponential backoff on a store that is actually fine —
    /// and a <c>rollout status</c> that can time out before the cluster has ever formed.
    ///
    /// So the loop below is written around a postcondition rather than around an exception list:
    /// re-check the record, create it only while it is still missing, then prove the database
    /// really answers by asking it for its statistics. Every failure is retried identically
    /// because every one of them has the same correct response, and the last one is rethrown if
    /// the deadline passes so an unreachable server still fails loudly instead of after a silent
    /// minute.
    /// </summary>
    private static void ensureDatabaseReady(IDocumentStore store, string database, int replicationFactor, SimLog emit)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var attempts = 0;
        var created = false;
        Exception? last = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            attempts++;

            try
            {
                if (store.Maintenance.Server.Send(new GetDatabaseRecordOperation(database)) is null)
                {
                    store.Maintenance.Server.Send(
                        new CreateDatabaseOperation(new DatabaseRecord(database), replicationFactor));
                    created = true;
                }

                // Touches the database, not the server: the server answers happily while the
                // database is still locked, so anything less than this is not a readiness probe.
                store.Maintenance.Send(new GetStatisticsOperation());

                emit("ChurnSim.Startup",
                    $"CONFIG ravendb database '{database}' ready after {attempts} attempt(s), " +
                    $"created={created.ToString().ToLowerInvariant()}" +
                    (created ? $" at replication factor {replicationFactor}" : ""),
                    new Dictionary<string, object?>
                    {
                        ["Database"] = database,
                        ["Attempts"] = attempts,
                        ["Created"] = created,
                        ["ReplicationFactor"] = created ? replicationFactor : null
                    });
                return;
            }
            catch (Exception e)
            {
                last = e;
                Thread.Sleep(500);
            }
        }

        throw new InvalidOperationException(
            $"RavenDB database '{database}' did not become ready within 60 seconds " +
            $"({attempts} attempts). Last failure is the inner exception.", last);
    }
}
