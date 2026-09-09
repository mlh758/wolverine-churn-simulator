# docs/

Why this rig exists and what it is trying to find out. Written to be picked up cold — by a
future session, a different agent, or me in three months having forgotten all of it.

| | |
|---|---|
| [experiments.md](experiments.md) | **Start here.** What we are hunting, the experiment catalogue, what counts as a finding versus an artifact, and what is currently open. |
| [harness-traps.md](harness-traps.md) | The specific ways this rig has lied to us. Every entry cost real time; several produced confident wrong answers. Read before trusting any number. |

The measured results themselves live in [../RESULTS.md](../RESULTS.md), newest first and dated.
The README covers how to *operate* the rig; these docs cover why you would want to.

## The one-paragraph version

Wolverine elects a leader by holding a PostgreSQL session-level advisory lock, and the leader
assigns agents to nodes through a table. Recent issues cluster around that machinery misbehaving
during Kubernetes rolling deploys. This rig replays those deploys against a real cluster and
checks safety properties from **outside** the process, because every check Wolverine ships is an
in-process assertion — it can only confirm what a node *believes*, and the interesting bugs are
exactly where belief and server disagree.

We have one confirmed, never-healed duplicate-agent divergence on stock `main` 6.35.0. The
current work is establishing how often it happens.
