using System.CommandLine;
using SafetyLab;

// The whole CLI lives in CommandLineDefinition, so it can be parsed and asserted on in tests
// without running anything. Unknown tokens and bad values fail here with a non-zero exit rather
// than being discarded into a default, which is the one behaviour this rig cannot tolerate from
// its own configuration surface.
return await CommandLineDefinition.Build().Parse(args).InvokeAsync();
