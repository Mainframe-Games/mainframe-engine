using MainframeEngine.Benchmarks;

// Examples:
//   dotnet run -c Release --project Tests/MainframeEngine.Benchmarks -- --filter '*'
//   ... -- --filter '*' --baseline-compare Tests/MainframeEngine.Benchmarks/baseline.json
//   ... -- --filter '*' --baseline-write Tests/MainframeEngine.Benchmarks/baseline.json
return BenchmarkHost.Run(args);
