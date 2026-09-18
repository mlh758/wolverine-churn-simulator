namespace SafetyLab.Cluster;

/// <summary>
/// The synthetic-self-guard fault: hide ONE node's row from every read the app makes, using
/// PostgreSQL row-level security, while letting all its writes through.
///
/// This simulates the production condition the guard exists for — reads lagging writes — rather
/// than approximating it. The victim's own snapshot then omits the victim on every tick while its
/// assignment rows stay visible, which is precisely the "self missing from its own snapshot,
/// agents still claimed here" state. Three deletion-based attempts failed to open that window at
/// all (runs/synth-guard/*-nochaos, *-slowchaos, *-fastdelete).
///
/// Two things moved in here from the shell, and both are rules from docs/harness-traps.md:
///
/// **A fault injector must prove it fired.** The victim was chosen by a psql query whose result
/// went straight into the policy text. When that query returned nothing — no schema yet, an
/// unreachable store, a cluster that was all leader — the policy became <c>id &lt;&gt; ''</c>,
/// which hides nothing. The arm then ran its full duration injecting no fault, recorded zero
/// injections, and read as "the guard worked".
///
/// **It must also prove it STOPPED.** Arming and disarming were two statements a few hundred
/// seconds apart in a script with no trap, under <c>set -uo pipefail</c> with no <c>-e</c>. A
/// Ctrl-C, or any failing command in between, left the victim's row permanently invisible to the
/// app role — and every later experiment on that cluster was then silently measuring a crippled
/// node with nothing anywhere saying so. <see cref="Disarm"/> is idempotent and
/// <see cref="Status"/> exists so a leaked arm is caught before the next run rather than after it.
/// </summary>
public static class PostgresChaos
{
    public const string SelectPolicy = "chaos_nodes_sel";
    private const string Table = "wolverine.wolverine_nodes";

    public sealed record NodeRow(int NodeNumber, string Id, string Description);

    public enum ArmState { Disarmed, Armed, Unknown }

    // ------------------------------------------------------------------ pure

    /// <summary>
    /// <c>node_number TAB id TAB description</c>, one node per line.
    /// </summary>
    public static IReadOnlyList<NodeRow> ParseNodes(string tsv)
    {
        var rows = new List<NodeRow>();

        foreach (var line in tsv.Split('\n'))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[0].Trim(), out var number)) continue;
            if (parts[1].Trim().Length == 0) continue;

            rows.Add(new NodeRow(number, parts[1].Trim(), parts[2].Trim()));
        }

        return rows;
    }

    /// <summary>
    /// The lowest-numbered node that is NOT the leader, or a refusal explaining which precondition
    /// failed. The victim must be a non-leader so elections do not confound the two arms.
    ///
    /// Every branch that used to yield an empty string — and therefore a policy that hides nothing
    /// — is now a distinct, named refusal.
    /// </summary>
    public static bool TryChooseVictim(
        IReadOnlyList<NodeRow> nodes, string? leaderNodeId, out NodeRow? victim, out string problem)
    {
        victim = null;
        problem = "";

        if (nodes.Count == 0)
        {
            problem = "the node registry is empty — is the schema created and the cluster settled?";
            return false;
        }

        var leader = (leaderNodeId ?? "").Trim();

        var candidates = nodes
            .Where(x => leader.Length == 0 || !string.Equals(x.Id, leader, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.NodeNumber)
            .ToList();

        if (candidates.Count == 0)
        {
            problem = nodes.Count == 1
                ? "the only registered node is the leader; a victim must be a non-leader so " +
                  "elections do not confound the arms"
                : $"every one of the {nodes.Count} registered nodes is the leader, which cannot happen — " +
                  "the node registry and the leader assignment disagree";
            return false;
        }

        if (leader.Length == 0)
        {
            problem = "there is no leader assignment, so the cluster is mid-election or not settled; " +
                      "a victim chosen now could be the next leader";
            return false;
        }

        victim = candidates[0];
        return true;
    }

    // --------------------------------------------------------------- the SQL

    /// <summary>
    /// Hide the victim's node row from every SELECT the app role makes; permit every write.
    ///
    /// Only the NODE row is hidden, never the assignment rows. Hiding assignments too was the first
    /// attempt and it made the leader see the victim's agents as unowned and reassign them on
    /// ordinary ticks: plenty of churn, but not the guard's domain, and injections stayed at zero.
    /// </summary>
    public static string ArmSql(string victimId) =>
        $"""
         alter table {Table} enable row level security;
         alter table {Table} force row level security;
         drop policy if exists {SelectPolicy} on {Table};
         drop policy if exists chaos_nodes_ins on {Table};
         drop policy if exists chaos_nodes_upd on {Table};
         drop policy if exists chaos_nodes_del on {Table};
         create policy {SelectPolicy} on {Table} for select using (id <> '{victimId}');
         create policy chaos_nodes_ins on {Table} for insert with check (true);
         create policy chaos_nodes_upd on {Table} for update using (true) with check (true);
         create policy chaos_nodes_del on {Table} for delete using (true);
         """;

    /// <summary>
    /// Idempotent, and it drops the policies rather than only disabling RLS — so
    /// <see cref="Status"/> has one unambiguous thing to look for and a half-disarmed table cannot
    /// read as clean.
    /// </summary>
    public static string DisarmSql() =>
        $"""
         alter table {Table} no force row level security;
         alter table {Table} disable row level security;
         drop policy if exists {SelectPolicy} on {Table};
         drop policy if exists chaos_nodes_ins on {Table};
         drop policy if exists chaos_nodes_upd on {Table};
         drop policy if exists chaos_nodes_del on {Table};
         """;

    public static string StatusSql() =>
        $"""
         select (case when c.relrowsecurity then 'rls-on' else 'rls-off' end) || chr(9) ||
                (case when exists (select 1 from pg_policy p
                                    where p.polrelid = c.oid and p.polname = '{SelectPolicy}')
                      then 'policy-present' else 'policy-absent' end)
           from pg_class c join pg_namespace n on n.oid = c.relnamespace
          where n.nspname = 'wolverine' and c.relname = 'wolverine_nodes';
         """;

    /// <summary>
    /// Armed if either half is still in place. Erring toward "armed" is deliberate: a false alarm
    /// costs one disarm, and a missed one costs every measurement taken afterwards.
    /// </summary>
    public static ArmState ParseStatus(string tsv)
    {
        var line = tsv.Split('\n').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0);

        // No row at all means no such table: the schema has not been created, so nothing is armed.
        if (line is null) return ArmState.Disarmed;

        var parts = line.Split('\t');
        if (parts.Length != 2) return ArmState.Unknown;

        return parts[0] == "rls-on" || parts[1] == "policy-present" ? ArmState.Armed : ArmState.Disarmed;
    }
}
