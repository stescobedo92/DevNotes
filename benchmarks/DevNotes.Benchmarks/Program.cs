using BenchmarkDotNet.Running;

// Examples:
//   dotnet run -c Release --project benchmarks/DevNotes.Benchmarks -- --filter *Search*
//   dotnet run -c Release --project benchmarks/DevNotes.Benchmarks -- --filter * --job short
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
