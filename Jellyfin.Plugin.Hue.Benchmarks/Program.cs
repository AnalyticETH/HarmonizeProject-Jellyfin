using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using Jellyfin.Plugin.Hue.Benchmarks;

// Run all benchmarks
var config = DefaultConfig.Instance
    .WithOptions(ConfigOptions.DisableOptimizationsValidator);

Console.WriteLine("Jellyfin Hue Sync Plugin - Performance Benchmarks");
Console.WriteLine("=".PadRight(50, '='));
Console.WriteLine();
Console.WriteLine("Available benchmark suites:");
Console.WriteLine("  1. ColorProcessingBenchmarks - RGB/HSL conversion, sampling");
Console.WriteLine("  2. PacketBuildingBenchmarks - HueStream packet construction");
Console.WriteLine();

if (args.Length > 0 && args[0] == "--all")
{
    // Run all benchmarks
    BenchmarkRunner.Run<ColorProcessingBenchmarks>(config);
    BenchmarkRunner.Run<PacketBuildingBenchmarks>(config);
}
else if (args.Length > 0 && args[0] == "--color")
{
    BenchmarkRunner.Run<ColorProcessingBenchmarks>(config);
}
else if (args.Length > 0 && args[0] == "--packet")
{
    BenchmarkRunner.Run<PacketBuildingBenchmarks>(config);
}
else
{
    // Interactive selection
    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
}
