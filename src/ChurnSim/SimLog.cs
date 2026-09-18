namespace ChurnSim;

/// <summary>
/// Program.cs's startup emitter, passed into the backend wiring. Startup lines are written
/// before a logger exists but must not break the log stream: DuckDB reads the pod log as
/// newline-delimited JSON, and a single bare-text line makes the whole file unparseable.
/// </summary>
internal delegate void SimLog(string category, string message, IDictionary<string, object?> state);
