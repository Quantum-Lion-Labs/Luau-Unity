using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Luau;

var configuration = BenchmarkConfiguration.Parse(args);
var suite = new Stage6BenchmarkSuite(configuration);
var report = suite.Run();

var outputPath = Path.GetFullPath(configuration.OutputPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
File.WriteAllText(
    outputPath,
    JsonSerializer.Serialize(report, Stage6BenchmarkJsonContext.Default.BenchmarkReport));

Console.WriteLine($"Stage 6 benchmark report: {outputPath}");
foreach (var result in report.Results)
{
    Console.WriteLine(
        $"{result.Name,-43} {result.MeanNanoseconds,12:N0} ns/op  " +
        $"p95 {result.P95Nanoseconds,12:N0}  {result.AllocatedBytesPerOperation,10:N1} B/op");
}

sealed class Stage6BenchmarkSuite(BenchmarkConfiguration configuration)
{
    static readonly byte[] firstPartySource = Encoding.UTF8.GetBytes(
        "local M = {}\n" +
        "function M.step(players, dt)\n" +
        "  local total = 0\n" +
        "  for _, player in players do\n" +
        "    total += (player.velocity.Magnitude or 0) * dt\n" +
        "  end\n" +
        "  return total\n" +
        "end\n" +
        "return M\n");

    static readonly byte[] modSource = Encoding.UTF8.GetBytes(
        "local config = { force = 12.5, damping = 0.3 }\n" +
        "return function(body, input)\n" +
        "  if type(input) ~= 'number' then return 0 end\n" +
        "  local force = math.clamp(input, -1, 1) * config.force\n" +
        "  return force - (body.speed or 0) * config.damping\n" +
        "end\n");

    public BenchmarkReport Run()
    {
        var sourceIdentity = SourceControlIdentity.Capture();
        var identity = LuauCompiler.Compile("return 1"u8);
        var cases = CreateCases()
            .Where(benchmarkCase => configuration.Filter is null ||
                benchmarkCase.Name.StartsWith(configuration.Filter, StringComparison.Ordinal))
            .ToList();
        var results = new List<BenchmarkResult>(cases.Count);
        foreach (var benchmarkCase in cases)
        {
            results.Add(Measure(benchmarkCase));
        }

        return new BenchmarkReport(
            DateTimeOffset.UtcNow,
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            sourceIdentity.CommitHash,
            sourceIdentity.TreeHash,
            sourceIdentity.IsClean,
            identity.UpstreamRevisionHash,
            identity.HostBuildFingerprint,
            configuration.WarmupIterations,
            configuration.Iterations,
            results);
    }

    List<BenchmarkCase> CreateCases()
    {
        List<BenchmarkCase> cases =
        [
            new("invoke-primitive", CreatePrimitiveInvoke),
            new("reference-callback-result", CreateReferenceCallback),
            new("table-construction-span-map", CreateTableConstruction),
            new("reference-churn-live-32", () => CreateReferenceChurn(32), OperationsPerInvocation: 16),
            new("reference-churn-live-2048", () => CreateReferenceChurn(2048), OperationsPerInvocation: 16),
            new("small-compiled-operation", CreateSmallOperation),
            new("cached-small-module", CreateCachedModule),
            new("compile-first-party", () => CreateCompiler(firstPartySource), IterationDivisor: 10),
            new("compile-untrusted-mod", () => CreateCompiler(modSource), IterationDivisor: 10),
        ];

        AddCompilerMatrix(cases, "first-party", firstPartySource);
        AddCompilerMatrix(cases, "untrusted-mod", modSource);
        AddInterruptMatrix(cases);
        return cases;
    }

    // Every loop back-edge, call, and return is a Luau interrupt safepoint.
    // These cases report nanoseconds per loop iteration, so the per-operation
    // setup is amortized over the whole loop. The default profile's
    // 100,000-safepoint count limit bounds its loops; the other profiles run
    // long enough to make that setup negligible.
    static void AddInterruptMatrix(List<BenchmarkCase> cases)
    {
        const string loopSource = "local n = ... local s = 0 for i = 1, n do s = s + i end return s";
        const string callSource =
            "local n = ... local function f(x) return x + 1 end local s = 0 for i = 1, n do s = f(s) end return s";
        var profiles = new (string Name, LuauExecutionOptions Options, int LoopIterations, int CallIterations)[]
        {
            ("unbounded", LuauExecutionOptions.Unbounded, 1_000_000, 1_000_000),
            ("wall-clock", LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromSeconds(10) }, 1_000_000, 1_000_000),
            ("count", LuauExecutionOptions.Unbounded with { InterruptCountLimit = 1_000_000_000 }, 1_000_000, 1_000_000),
            // One back-edge per loop iteration; a call, return, and back-edge
            // per call iteration. Both stay below 100,000 safepoints.
            ("default", LuauExecutionOptions.Default, 90_000, 30_000),
        };

        foreach (var profile in profiles)
        {
            var captured = profile;
            cases.Add(new BenchmarkCase(
                $"interrupt-loop-{captured.Name}",
                () => CreateInterruptWorkload(loopSource, captured.Options, captured.LoopIterations),
                IterationDivisor: captured.LoopIterations >= 1_000_000 ? 50 : 2,
                OperationsPerInvocation: captured.LoopIterations));
            cases.Add(new BenchmarkCase(
                $"interrupt-calls-{captured.Name}",
                () => CreateInterruptWorkload(callSource, captured.Options, captured.CallIterations),
                IterationDivisor: captured.CallIterations >= 1_000_000 ? 50 : 2,
                OperationsPerInvocation: captured.CallIterations));
        }
    }

    static BenchmarkLease CreateInterruptWorkload(string source, LuauExecutionOptions options, int iterations)
    {
        var state = LuauState.Create(LuauStateOptions.UnboundedResources with { DefaultExecutionOptions = options });
        state.OpenBaseLibrary();
        var owner = state.DoString($"return function(...) {source} end", "@benchmark/interrupt.luau");
        var function = owner[0].Read<LuauFunction>();
        var arguments = new[] { LuauValue.FromNumber(iterations) };
        var destination = new LuauValue[1];
        return new BenchmarkLease(
            () =>
            {
                var count = function.InvokeInto(arguments, destination);
                if (count != 1 || destination[0].Read<double>() <= 0)
                {
                    throw new InvalidOperationException("Unexpected interrupt workload result.");
                }
            },
            () =>
            {
                DisposeResultContainer(owner);
                function.Dispose();
                state.Dispose();
            });
    }

    static void AddCompilerMatrix(List<BenchmarkCase> cases, string sourceName, byte[] source)
    {
        for (var optimizationLevel = 0; optimizationLevel <= 2; optimizationLevel++)
        {
            for (var typeInfoLevel = 0; typeInfoLevel <= 1; typeInfoLevel++)
            {
                var capturedOptimizationLevel = optimizationLevel;
                var capturedTypeInfoLevel = typeInfoLevel;
                cases.Add(new BenchmarkCase(
                    $"compile-{sourceName}-opt{capturedOptimizationLevel}-typeinfo{capturedTypeInfoLevel}",
                    () => CreateCompiler(source, capturedOptimizationLevel, capturedTypeInfoLevel),
                    IterationDivisor: 10));
            }
        }
    }

    BenchmarkResult Measure(BenchmarkCase benchmarkCase)
    {
        using var lease = benchmarkCase.Create();
        var iterations = Math.Max(5, configuration.Iterations / benchmarkCase.IterationDivisor);
        var warmups = Math.Max(1, configuration.WarmupIterations / benchmarkCase.IterationDivisor);

        for (var i = 0; i < warmups; i++)
        {
            lease.Action();
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var samples = new long[iterations];
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var totalStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < iterations; i++)
        {
            var start = Stopwatch.GetTimestamp();
            lease.Action();
            samples[i] = Stopwatch.GetTimestamp() - start;
        }
        var totalTicks = Stopwatch.GetTimestamp() - totalStart;
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Array.Sort(samples);
        var operationCount = checked((long)iterations * benchmarkCase.OperationsPerInvocation);
        return new BenchmarkResult(
            benchmarkCase.Name,
            iterations,
            benchmarkCase.OperationsPerInvocation,
            TicksToNanoseconds(totalTicks) / operationCount,
            TicksToNanoseconds(Percentile(samples, 0.50)) / benchmarkCase.OperationsPerInvocation,
            TicksToNanoseconds(Percentile(samples, 0.95)) / benchmarkCase.OperationsPerInvocation,
            TicksToNanoseconds(Percentile(samples, 0.99)) / benchmarkCase.OperationsPerInvocation,
            (double)allocatedBytes / operationCount);
    }

    static BenchmarkLease CreatePrimitiveInvoke()
    {
        var state = CreateState();
        var owner = state.DoString("return function(a, b) return a + b end");
        var function = owner[0].Read<LuauFunction>();
        var arguments = new[] { LuauValue.FromNumber(20), LuauValue.FromNumber(22) };
        return new BenchmarkLease(
            () =>
            {
                var results = function.Invoke(arguments);
                if (results[0].Read<double>() != 42) throw new InvalidOperationException("Unexpected invoke result.");
                DisposeResultContainer(results);
            },
            () =>
            {
                DisposeResultContainer(owner);
                function.Dispose();
                state.Dispose();
            });
    }

    static BenchmarkLease CreateReferenceCallback()
    {
        var state = CreateState();
        var callback = state.CreateFunction(
            "echoReference",
            context => context.Return(context.Read<LuauTable>(0)));
        state["echoReference"] = callback;
        var owner = state.DoString("return function() return echoReference({ answer = 42 }) end");
        var function = owner[0].Read<LuauFunction>();
        return new BenchmarkLease(
            () =>
            {
                var results = function.Invoke();
                if (results[0].Read<LuauTable>()["answer"].Read<long>() != 42)
                {
                    throw new InvalidOperationException("Unexpected callback result.");
                }
                DisposeResultContainer(results);
            },
            () =>
            {
                DisposeResultContainer(owner);
                function.Dispose();
                callback.Dispose();
                state.Dispose();
            });
    }

    static BenchmarkLease CreateTableConstruction()
    {
        var state = CreateState();
        var sequence = new[]
        {
            LuauValue.FromInteger(1),
            LuauValue.FromString("two"),
            LuauValue.FromBoolean(true),
        };
        var map = new Dictionary<LuauValue, LuauValue>
        {
            [LuauValue.FromString("one")] = LuauValue.FromInteger(1),
            [LuauValue.FromString("two")] = LuauValue.FromInteger(2),
            [LuauValue.FromString("three")] = LuauValue.FromInteger(3),
        };

        return new BenchmarkLease(
            () =>
            {
                using var sequenceTable = state.CreateTable(sequence);
                using var mapTable = state.CreateTable(map);
            },
            state.Dispose);
    }

    static BenchmarkLease CreateReferenceChurn(int liveCount)
    {
        var state = CreateState();
        var live = new LuauTable[liveCount];
        for (var i = 0; i < live.Length; i++)
        {
            live[i] = state.CreateTable();
        }

        return new BenchmarkLease(
            () =>
            {
                for (var i = 0; i < 16; i++)
                {
                    state.CreateTable().Dispose();
                }
            },
            () =>
            {
                foreach (var table in live) table.Dispose();
                state.Dispose();
            });
    }

    static BenchmarkLease CreateSmallOperation()
    {
        var state = CreateState();
        var output = LuauCompiler.Compile("return 6 * 7"u8);
        var destination = new LuauValue[1];
        return new BenchmarkLease(
            () =>
            {
                var count = state.ExecuteCompilerOutputInto(output, destination, "benchmark");
                if (count != 1 || destination[0].Read<long>() != 42)
                {
                    throw new InvalidOperationException("Unexpected operation result.");
                }
            },
            () =>
            {
                state.Dispose();
            });
    }

    static BenchmarkLease CreateCachedModule()
    {
        var state = CreateState();
        state.OpenRequireLibrary(new ConstantRequirer());
        var owner = state.DoString("return function() return require('benchmark-module') end");
        var function = owner[0].Read<LuauFunction>();
        var prime = function.Invoke();
        DisposeResultContainer(prime);

        return new BenchmarkLease(
            () =>
            {
                var results = function.Invoke();
                if (results[0].Read<long>() != 73) throw new InvalidOperationException("Unexpected module result.");
                DisposeResultContainer(results);
            },
            () =>
            {
                DisposeResultContainer(owner);
                function.Dispose();
                state.Dispose();
            });
    }

    static BenchmarkLease CreateCompiler(byte[] source)
        => CreateCompiler(source, optimizationLevel: 1, typeInfoLevel: 1);

    static BenchmarkLease CreateCompiler(byte[] source, int optimizationLevel, int typeInfoLevel)
    {
        var options = new LuauCompileOptions
        {
            OptimizationLevel = optimizationLevel,
            DebugLevel = 1,
            TypeInfoLevel = typeInfoLevel,
            CoverageLevel = 0,
        };
        return new BenchmarkLease(
            () =>
            {
                var output = LuauCompiler.Compile(source, options);
                if (output.BytecodeLength == 0) throw new InvalidOperationException("Compiler returned no bytecode.");
            },
            static () => { });
    }

    static LuauState CreateState()
    {
        var state = LuauState.Create();
        state.OpenBaseLibrary();
        state.OpenMathLibrary();
        state.OpenTableLibrary();
        return state;
    }

    static void DisposeResultContainer(object results)
    {
        if (results is IDisposable disposable)
        {
            disposable.Dispose();
            return;
        }

        if (results is not IEnumerable<LuauValue> values) return;
        foreach (var value in values)
        {
            switch (value.Type)
            {
                case LuauType.Table:
                    value.Read<LuauTable>().Dispose();
                    break;
                case LuauType.Function:
                    value.Read<LuauFunction>().Dispose();
                    break;
                case LuauType.Buffer:
                    value.Read<LuauBuffer>().Dispose();
                    break;
                case LuauType.UserData when value.TryRead<LuauUserData>(out var userData):
                    userData.Dispose();
                    break;
            }
        }
    }

    static long Percentile(long[] sorted, double percentile)
    {
        var index = (int)Math.Ceiling(sorted.Length * percentile) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    static double TicksToNanoseconds(long ticks) => ticks * 1_000_000_000d / Stopwatch.Frequency;

    sealed class ConstantRequirer : LuauRequirer
    {
        protected override bool TryLoadModule(
            LuauState state,
            string fullPath,
            string requireArgument,
            out LuauValue result)
        {
            result = LuauValue.FromInteger(73);
            return true;
        }

        protected override bool TryGetAliasPath(
            string alias,
            [NotNullWhen(true)] out string? path)
        {
            path = null;
            return false;
        }
    }
}

sealed record BenchmarkCase(
    string Name,
    Func<BenchmarkLease> Create,
    int IterationDivisor = 1,
    int OperationsPerInvocation = 1);

sealed class BenchmarkLease(Action action, Action dispose) : IDisposable
{
    public Action Action { get; } = action;

    public void Dispose() => dispose();
}

sealed record BenchmarkConfiguration(int WarmupIterations, int Iterations, string OutputPath, string? Filter)
{
    public static BenchmarkConfiguration Parse(string[] args)
    {
        var warmup = 100;
        var iterations = 1_000;
        var output = Path.Combine("artifacts", "stage-6-benchmarks", "latest.json");
        string? filter = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--quick":
                    warmup = 10;
                    iterations = 50;
                    break;
                case "--warmup" when i + 1 < args.Length:
                    warmup = ParsePositive(args[++i], "--warmup");
                    break;
                case "--iterations" when i + 1 < args.Length:
                    iterations = ParsePositive(args[++i], "--iterations");
                    break;
                case "--output" when i + 1 < args.Length:
                    output = args[++i];
                    break;
                case "--filter" when i + 1 < args.Length:
                    filter = args[++i];
                    break;
                default:
                    throw new ArgumentException($"Unknown or incomplete benchmark argument '{args[i]}'.");
            }
        }

        return new BenchmarkConfiguration(warmup, iterations, output, filter);
    }

    static int ParsePositive(string value, string option)
    {
        if (!int.TryParse(value, out var parsed) || parsed <= 0)
        {
            throw new ArgumentOutOfRangeException(option, value, "The value must be a positive integer.");
        }

        return parsed;
    }
}

sealed record BenchmarkReport(
    DateTimeOffset CapturedAtUtc,
    string RuntimeVersion,
    string OperatingSystem,
    string Architecture,
    string SourceCommitHash,
    string SourceTreeHash,
    bool SourceTreeClean,
    ulong UpstreamRevisionHash,
    ulong HostBuildFingerprint,
    int WarmupIterations,
    int RequestedIterations,
    IReadOnlyList<BenchmarkResult> Results);

sealed record BenchmarkResult(
    string Name,
    int SampleCount,
    int OperationsPerSample,
    double MeanNanoseconds,
    double P50Nanoseconds,
    double P95Nanoseconds,
    double P99Nanoseconds,
    double AllocatedBytesPerOperation);

sealed record SourceControlIdentity(string CommitHash, string TreeHash, bool IsClean)
{
    public static SourceControlIdentity Capture()
    {
        var repositoryRoot = FindRepositoryRoot(Environment.CurrentDirectory)
            ?? FindRepositoryRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                "The benchmark must run from a Git checkout so its source identity can be recorded.");

        var commitHash = RunGit(repositoryRoot, "rev-parse", "--verify", "HEAD");
        var treeHash = RunGit(repositoryRoot, "rev-parse", "--verify", "HEAD^{tree}");
        var status = RunGit(repositoryRoot, "status", "--porcelain=v1", "--untracked-files=normal");
        return new SourceControlIdentity(commitHash, treeHash, status.Length == 0);
    }

    static string? FindRepositoryRoot(string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath)) return directory.FullName;
            directory = directory.Parent;
        }

        return null;
    }

    static string RunGit(string repositoryRoot, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryRoot);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start Git while capturing benchmark source identity.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git failed while capturing benchmark source identity: {error.Trim()}");
        }

        return output.Trim();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BenchmarkReport))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
partial class Stage6BenchmarkJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
