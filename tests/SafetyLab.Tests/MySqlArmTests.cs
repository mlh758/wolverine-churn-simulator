using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The MySQL arm's pure decisions.
///
/// The claim the whole arm rests on is that MySQL's leadership lock is PostgreSQL's under another
/// name: <c>MySqlNodePersistence</c> and <c>PostgresqlNodePersistence</c> both derive the lock id
/// from <c>schemaName.GetDeterministicHashCode()</c>, and MySQL merely spells the result
/// <c>wolverine_&lt;id&gt;</c> before handing it to <c>GET_LOCK</c>. Every leader-side checker
/// matches on that id, so if the spelling and the parse ever stop being inverses the monitor
/// watches a lock nobody takes and every leader check passes over nothing — silently, which is
/// the one failure this rig will not tolerate.
/// </summary>
public class MySqlArmTests
{
    [Fact]
    public void the_lock_name_is_the_schema_hash_the_postgres_arm_locks_on()
    {
        // 832201495 is "wolverine".GetDeterministicHashCode(), confirmed against a live cluster
        // and hard-coded in tests/make_fixtures.py for the same reason.
        Assert.Equal(832201495, RunHistory.LockIdForSchema("wolverine"));
        Assert.Equal("wolverine_832201495", MySqlClusterMonitor.LockName(832201495));
    }

    [Theory]
    [InlineData("wolverine", 832201495)]
    [InlineData("churnsim", 1843231225)]
    // A schema whose hash is NEGATIVE. pg_locks.objid is an unsigned oid, so PostgreSQL shows the
    // 2^32 complement and RunHistory.LeaderLockHolders masks to the low 32 bits to cope; MySQL
    // carries the signed integer verbatim in the lock's name instead, so the round trip has to
    // survive a minus sign the other arm never sees. The expected values are written out rather
    // than recomputed, so a change to LockIdForSchema fails here instead of agreeing with itself.
    [InlineData("wolverine2", -665954415)]
    public void a_lock_name_round_trips_back_to_its_id(string schema, int expected)
    {
        var id = RunHistory.LockIdForSchema(schema);
        Assert.Equal(expected, id);

        Assert.Equal($"wolverine_{expected}", MySqlClusterMonitor.LockName(id));
        Assert.Equal(id, MySqlClusterMonitor.LockIdFromName(MySqlClusterMonitor.LockName(id)));
    }

    [Theory]
    [InlineData("wolverine_lock")]        // a suffix that is not a number
    [InlineData("wolverine_")]            // the prefix and nothing else
    [InlineData("mylock")]                // somebody else's named lock
    [InlineData("wolverine_1_2")]
    [InlineData(null)]
    public void a_name_that_is_not_wolverines_yields_no_id(string? name)
    {
        // Null rather than 0. The monitor writes the result into LockRow.ObjId, and 0 is a lock
        // id a schema could genuinely hash to -- so guessing here would let an unrelated named
        // lock be read as somebody's leadership claim.
        Assert.Null(MySqlClusterMonitor.LockIdFromName(name));
    }

    [Theory]
    [InlineData("wolverine")]
    [InlineData("wolverine_2")]
    [InlineData("w$1")]
    public void a_plain_identifier_is_accepted_as_a_schema(string schema)
        => MySqlClusterMonitor.AssertSchemaIsAnIdentifier(schema);

    [Theory]
    [InlineData("")]
    [InlineData("wolverine`")]
    [InlineData("wolverine; drop database churnsim")]
    [InlineData("wolverine.nodes")]
    [InlineData("wolverine-2")]           // MySQL rejects dashes in a schema name anyway
    public void anything_that_is_not_a_plain_identifier_is_refused(string schema)
    {
        // The schema is an identifier, so it cannot be a parameter and has to be interpolated
        // into the sampling query. Refusing at construction means the monitor never starts --
        // strictly better than one that starts and samples nothing.
        Assert.Throws<ArgumentException>(() => MySqlClusterMonitor.AssertSchemaIsAnIdentifier(schema));
    }

    [Fact]
    public void the_sql_reaches_the_mysql_client_as_an_argument_and_never_as_shell()
    {
        var sql = "select 'it''s fine' /* \" */;";

        var argv = StoreQueries.MySqlExec("mysql-0", sql);

        // The last element is the SQL, whole and unaltered, and the -c script before it mentions
        // only "$1". Anything else would mean a query containing a quote could be reassembled
        // into a different query by the shell inside the pod.
        Assert.Equal(sql, argv[^1]);
        Assert.Contains("\"$1\"", StoreQueries.MySqlShell);
        Assert.DoesNotContain(sql, StoreQueries.MySqlShell);

        // MYSQL_PWD, not -p: `-p<pass>` makes the client warn on stderr, and every caller reads a
        // non-empty stderr as the store having failed.
        Assert.Contains("MYSQL_PWD", StoreQueries.MySqlShell);
        Assert.DoesNotContain(" -p", StoreQueries.MySqlShell);

        // -N -B, so the client emits the same tab-separated shape ParseTsv reads on every arm.
        Assert.Contains("-N -B", StoreQueries.MySqlShell);
    }

    [Fact]
    public void an_unknown_sim_backend_is_an_outcome_and_not_a_default()
    {
        // The one thing that must never happen is measuring one backend and filing it as another,
        // so a typo in SIM_BACKEND has to be refused rather than fall through to postgres.
        Assert.Contains(Backends.MySql, Backends.All);
        Assert.True(Backends.IsSessionLock(Backends.MySql));
        Assert.True(Backends.IsSessionLock(Backends.Postgres));
        Assert.False(Backends.IsSessionLock(Backends.RavenDb));
    }

    [Fact]
    public void a_mysql_history_reads_as_a_session_lock_and_names_the_store_in_its_own_words()
    {
        var history = new RunHistory
        {
            Directory = "(none)",
            Meta = new MetaRecord(DateTimeOffset.UnixEpoch, 1000, 832201495, "wolverine",
                "8.0.39", Backends.MySql, "wolverine_832201495"),
            Samples = [], Identities = [], AgentEvents = [], Marks = []
        };

        Assert.True(history.IsSessionLock);
        Assert.False(history.IsRavenDb);
        Assert.Equal("MySQL", history.StoreName);
        Assert.Equal("named lock", history.SessionLockNoun);
        Assert.Contains("wolverine_832201495", history.LeaderLockDescription);
    }
}
