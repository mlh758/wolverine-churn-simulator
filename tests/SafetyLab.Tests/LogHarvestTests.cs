using SafetyLab.Cluster;
using Xunit;

namespace SafetyLab.Tests;

/// <summary>
/// The harvester must read both console shapes into the SAME records. Every manifest now sets
/// SIM_JSON_LOGS=true, and before this the JSON shape was only half-read: identities were dated
/// at harvest time (the regex ran over the raw JSON and swallowed the rest of the object into
/// the timestamp group) and control-plane lines were not recognised at all.
///
/// Lines are real: the JSON ones from runs/heal-test-fixes-build and the text ones from the
/// 2026-09-25 MySQL cluster.
/// </summary>
public class LogHarvestTests
{
    private const string JsonIdentity =
        """{"Timestamp":"2026-09-10T03:06:29.6850958+00:00","EventId":0,"LogLevel":"Information","Category":"ChurnSim.Startup","Message":"SIM-IDENTITY nodeId=9bf94352-5318-4c70-a501-bc04c1289f14 podName=churnsim-6594487f97-r5hls podIp=10.244.0.55 at 2026-09-10T03:06:29.6849530+00:00","State":{"NodeId":"9bf94352-5318-4c70-a501-bc04c1289f14","PodName":"churnsim-6594487f97-r5hls","PodIp":"10.244.0.55"}}""";

    private const string TextIdentity =
        "SIM-IDENTITY nodeId=9bf94352-5318-4c70-a501-bc04c1289f14 podName=churnsim-6594487f97-r5hls podIp=10.244.0.55 at 2026-09-10T03:06:29.6849530+00:00";

    private const string JsonStart =
        """{"Timestamp":"2026-09-10T01:49:28.1004806+00:00","EventId":0,"LogLevel":"Information","Category":"ChurnSim.SimAgentFamily","Message":"AGENT-START sim://agent288/ at 2026-09-10T01:49:28.1003011+00:00","State":{"AgentUri":"sim://agent288/","Timestamp":"09/10/2026 01:49:28 +00:00","{OriginalFormat}":"AGENT-START {AgentUri} at {Timestamp:O}"}}""";

    private const string TextStart = "AGENT-START sim://agent288/ at 2026-09-10T01:49:28.1003011+00:00";

    private const string JsonControl =
        """{"Timestamp":"2026-09-09T23:26:35.0141786+00:00","EventId":0,"LogLevel":"Information","Category":"Wolverine.Runtime.Agents.NodeAgentController","Message":"Starting agents for Node 38312feb-ddd7-4dc1-9167-f3deff3ae4c7 with assigned node id 180 and Control Uri dbcontrol://38312feb-ddd7-4dc1-9167-f3deff3ae4c7/","State":{"NodeId":"38312feb-ddd7-4dc1-9167-f3deff3ae4c7","Id":180,"ControlUri":"dbcontrol://38312feb-ddd7-4dc1-9167-f3deff3ae4c7/","{OriginalFormat}":"Starting agents for Node {NodeId} with assigned node id {Id} and Control Uri {ControlUri}"}}""";

    private static readonly string[] TextControl =
    [
        "2026-09-09T23:26:35.014178600Z info: Wolverine.Runtime.Agents.NodeAgentController[0]",
        "2026-09-09T23:26:35.014200000Z       Starting agents for Node 38312feb-ddd7-4dc1-9167-f3deff3ae4c7 with assigned node id 180 and Control Uri dbcontrol://38312feb-ddd7-4dc1-9167-f3deff3ae4c7/"
    ];

    private static readonly DateTimeOffset IdentityTs = DateTimeOffset.Parse("2026-09-10T03:06:29.6849530+00:00");
    private static readonly DateTimeOffset StartTs = DateTimeOffset.Parse("2026-09-10T01:49:28.1003011+00:00");

    // ------------------------------------------------------------------ identity

    [Fact]
    public void a_json_identity_is_dated_by_the_line_not_by_the_harvest()
    {
        var record = Assert.IsType<IdentityRecord>(Assert.Single(LogHarvest.Parse("p", [JsonIdentity])));

        Assert.Equal(IdentityTs, record.Ts);
        Assert.Equal(Guid.Parse("9bf94352-5318-4c70-a501-bc04c1289f14"), record.NodeId);
        Assert.Equal("churnsim-6594487f97-r5hls", record.PodName);
        Assert.Equal("10.244.0.55", record.PodIp);
    }

    [Fact]
    public void text_and_json_identities_are_the_same_record()
    {
        var fromText = Assert.Single(LogHarvest.Parse("p", [TextIdentity]));
        var fromJson = Assert.Single(LogHarvest.Parse("p", [JsonIdentity]));

        Assert.Equal(fromText, fromJson);
    }

    // --------------------------------------------------------------------- agents

    [Fact]
    public void text_and_json_agent_events_are_the_same_record()
    {
        var fromText = Assert.IsType<AgentEventRecord>(Assert.Single(LogHarvest.Parse("p", [TextStart])));
        var fromJson = Assert.Single(LogHarvest.Parse("p", [JsonStart]));

        Assert.Equal(fromText, fromJson);
        Assert.Equal(StartTs, fromText.Ts);
        Assert.Equal("start", fromText.Event);
        Assert.Equal("sim://agent288/", fromText.AgentUri);
        Assert.Equal("p", fromText.PodName);
    }

    [Fact]
    public void a_kubectl_timestamps_prefix_on_a_json_line_is_stripped_first()
    {
        var record = Assert.IsType<AgentEventRecord>(
            Assert.Single(LogHarvest.Parse("p", ["2026-09-10T01:49:28.100480600Z " + JsonStart])));

        Assert.Equal(StartTs, record.Ts);
    }

    // -------------------------------------------------------------------- control

    [Fact]
    public void a_json_control_line_is_a_control_record_with_the_text_consoles_level_spelling()
    {
        var record = Assert.IsType<ControlEventRecord>(Assert.Single(LogHarvest.Parse("p", [JsonControl])));

        Assert.Equal("info", record.Level);
        Assert.Equal("Wolverine.Runtime.Agents.NodeAgentController", record.Category);
        Assert.StartsWith("Starting agents for Node 38312feb", record.Message);
        Assert.Equal(DateTimeOffset.Parse("2026-09-09T23:26:35.0141786+00:00"), record.Ts);
        Assert.Equal("p", record.PodName);
    }

    [Fact]
    public void text_and_json_control_lines_are_the_same_record()
    {
        var fromText = Assert.IsType<ControlEventRecord>(Assert.Single(LogHarvest.Parse("p", TextControl)));
        var fromJson = Assert.IsType<ControlEventRecord>(Assert.Single(LogHarvest.Parse("p", [JsonControl])));

        Assert.Equal(fromText.Level, fromJson.Level);
        Assert.Equal(fromText.Category, fromJson.Category);
        Assert.Equal(fromText.Message, fromJson.Message);
        // The text console has no clock of its own; the --timestamps prefix is the one it gets,
        // and the JSON record carries the same instant.
        Assert.Equal(fromJson.Ts, fromText.Ts);
    }

    [Fact]
    public void a_json_line_from_an_unrelated_category_is_not_a_control_record()
    {
        const string handlerWarning =
            """{"Timestamp":"2026-09-09T23:26:04.5637333+00:00","EventId":0,"LogLevel":"Warning","Category":"Wolverine.Configuration.HandlerDiscovery","Message":"Wolverine found no handlers.","State":{}}""";

        Assert.Empty(LogHarvest.Parse("p", [handlerWarning]));
    }

    [Fact]
    public void a_json_line_ends_a_pending_text_header_rather_than_becoming_its_message()
    {
        // Mixed shapes do not happen in one pod, but a stray header at the very end of a text
        // capture must not attach itself to whatever comes next in the file.
        var records = LogHarvest.Parse("p", [TextControl[0], JsonStart]).ToList();

        Assert.Single(records);
        Assert.IsType<AgentEventRecord>(records[0]);
    }

    [Fact]
    public void a_truncated_json_line_is_nothing()
        => Assert.Empty(LogHarvest.Parse("p", [JsonStart[..80]]));
}
