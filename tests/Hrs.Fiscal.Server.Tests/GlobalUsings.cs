global using Xunit;
// WebApplicationFactory<Program> builds hosts through a process-wide DiagnosticListener; two factories starting in
// parallel can capture each other's host and hang. Run the integration test classes one after another.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
