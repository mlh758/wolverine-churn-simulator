using System.Text.Json;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// A member's <c>/cluster/topology</c> answer, parsed leniently. P1 decides "the partition took"
/// from <c>Leader</c> being absent on a minority member, so an absent field has to read as null
/// and a malformed document must not stop the monitor.
/// </summary>
public class ClusterViewTests
{
    [Fact]
    public void a_healthy_member_names_itself_its_leader_and_its_peers()
    {
        var json = """
            {
              "Topology": {
                "TopologyId": "t1",
                "AllNodes": {"A": "http://ravendb-0:8080", "B": "http://ravendb-1:8080", "C": "http://ravendb-2:8080"},
                "Members": {"A": "http://ravendb-0:8080", "B": "http://ravendb-1:8080", "C": "http://ravendb-2:8080"},
                "Promotables": {}, "Watchers": {}
              },
              "Leader": "A",
              "CurrentState": "Follower",
              "NodeTag": "C"
            }
            """;

        var view = ClusterView.Parse(JsonDocument.Parse(json).RootElement);

        Assert.Equal("C", view.NodeTag);
        Assert.Equal("A", view.Leader);
        Assert.Equal("Follower", view.State);
        Assert.Equal(3, view.Members.Count);
        Assert.Equal("http://ravendb-1:8080", view.Members["B"]);
    }

    [Fact]
    public void a_minority_member_with_no_leader_reads_as_leaderless()
    {
        // What a cut-off member answers: still knows the members, has no leader, is a Candidate.
        var json = """
            {"Topology": {"Members": {"A": "u", "B": "u", "C": "u"}}, "Leader": null, "CurrentState": "Candidate", "NodeTag": "C"}
            """;

        var view = ClusterView.Parse(JsonDocument.Parse(json).RootElement);

        Assert.Null(view.Leader);
        Assert.Equal("Candidate", view.State);
    }

    [Fact]
    public void a_passive_fresh_server_has_no_members_and_no_state_to_speak_of()
    {
        var view = ClusterView.Parse(JsonDocument.Parse("""{"Topology": {}, "NodeTag": "?"}""").RootElement);

        Assert.Null(view.Leader);
        Assert.Null(view.State);
        Assert.Empty(view.Members);
    }
}
