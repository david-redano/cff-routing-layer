// CffPlanTransformer – entry point
//
// Usage (single file):
//   dotnet run --project CffPlanTransformer -- <input-file> [output-file]
//   If output-file is omitted, YAML is written to stdout.
//
// Usage (batch – all files in a directory):
//   dotnet run --project CffPlanTransformer -- --all <input-dir> <output-dir>
//
using CffPlanTransformer.Parser;
using CffPlanTransformer.Transformer;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

if (args[0] == "--all")
{
    // Batch mode
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Batch mode requires: --all <input-dir> <output-dir>");
        return 1;
    }
    return RunBatch(args[1], args[2]);
}

// Single-file mode
return RunSingle(args[0], args.Length > 1 ? args[1] : null);

// ── Helpers ──────────────────────────────────────────────────────────────────

static int RunSingle(string inputPath, string? outputPath)
{
    if (!File.Exists(inputPath))
    {
        Console.Error.WriteLine($"File not found: {inputPath}");
        return 1;
    }

    var fileIndex = ParseIndex(Path.GetFileNameWithoutExtension(inputPath));

    try
    {
        var plan = RealPlanParser.Parse(inputPath);
        var yaml = PlanTransformer.Transform(plan, fileIndex);

        if (outputPath is null)
        {
            Console.Write(yaml);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            File.WriteAllText(outputPath, yaml);
            Console.WriteLine($"Written: {outputPath}");
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error processing {inputPath}: {ex.Message}");
        return 1;
    }
}

static int RunBatch(string inputDir, string outputDir)
{
    if (!Directory.Exists(inputDir))
    {
        Console.Error.WriteLine($"Input directory not found: {inputDir}");
        return 1;
    }

    Directory.CreateDirectory(outputDir);

    var files   = Directory.GetFiles(inputDir, "*.txt", SearchOption.TopDirectoryOnly)
                            .OrderBy(f => f)
                            .ToArray();

    if (files.Length == 0)
    {
        Console.Error.WriteLine($"No .txt files found in: {inputDir}");
        return 1;
    }

    var ok      = 0;
    var skipped = 0;
    var failed  = 0;
    var seenFingerprints = new HashSet<string>(StringComparer.Ordinal);

    foreach (var file in files)
    {
        var baseName   = Path.GetFileNameWithoutExtension(file);   // response_001
        var fileIndex  = ParseIndex(baseName);
        var outputFile = Path.Combine(outputDir, $"{baseName}.yaml");

        try
        {
            var plan        = RealPlanParser.Parse(file);
            var fingerprint = PlanTransformer.GetFingerprint(plan);

            if (!seenFingerprints.Add(fingerprint))
            {
                var intent = PlanTransformer.GetIntent(plan);
                Console.WriteLine($"[SKIP] {baseName} — same understanding + structure as an earlier plan (intent: '{intent}')");
                skipped++;
                continue;
            }

            var yaml = PlanTransformer.Transform(plan, fileIndex);
            File.WriteAllText(outputFile, yaml);
            Console.WriteLine($"[OK]   {baseName}.yaml");
            ok++;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[FAIL] {baseName}: {ex.Message}");
            failed++;
        }
    }

    Console.WriteLine();
    Console.WriteLine($"Done: {ok} written, {skipped} skipped (duplicate), {failed} failed, {files.Length} total.");
    return failed > 0 ? 1 : 0;
}

/// <summary>
/// Extracts the trailing integer from filenames like "response_042",
/// falling back to a hash-based index if no number is found.
/// </summary>
static int ParseIndex(string baseName)
{
    var m = System.Text.RegularExpressions.Regex.Match(baseName, @"(\d+)$");
    return m.Success ? int.Parse(m.Groups[1].Value) : Math.Abs(baseName.GetHashCode() % 1000);
}

static void PrintUsage()
{
    Console.WriteLine("CffPlanTransformer – converts real copilot plan files to routing-demo YAML");
    Console.WriteLine();
    Console.WriteLine("Single file:  CffPlanTransformer <input.txt> [output.yaml]");
    Console.WriteLine("Batch:        CffPlanTransformer --all <input-dir> <output-dir>");
}
