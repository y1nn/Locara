using System.Text.Json;
using System.Text.Json.Serialization;
using Locara.Core;

Console.OutputEncoding=System.Text.Encoding.UTF8;
if(args.Length==0||args[0] is "help" or "--help")
{
    Console.WriteLine("""
        Locara
        steam
        create <project-folder> <source-folder-or-file> [auto|generic|godot|rpgmaker|renpy|unity|unreal|gamemaker]
        source <project-folder> <path> [--relocate]
        packages <project-folder>
        languages <project-folder>
        capabilities <project-folder>
        package <project-folder> <relative-path|--loose>
        resources <project-folder>
        inspect <project-folder> <resource>
        configure <project-folder> <resource> <rule-json-file|--auto>
        plan <project-folder> [--final]
        scan <project-folder>
        files <project-folder>
        clusters <project-folder>
        advice <project-folder>
        scout <project-folder> <gemini|anthropic|deepseek|openai-compatible|azure> <model> [base-url] [--apply]
        include <project-folder> <file-path> [--human-only]
        exclude <project-folder> <file-path>
        list <project-folder> [search] [offset]
        edit <project-folder> <unit-id> <translation>
        approve <project-folder> <unit-id>
        build <project-folder> <new-output-folder> [--final]
        export-xunity <project-folder> <output-folder> [--final]
        install <project-folder> <output-folder> <game-copy-folder>
        restore <project-folder> <installation-receipt-folder>
        estimate <project-folder>
        translate <project-folder> <gemini|anthropic|deepseek|openai-compatible|azure> <model> [base-url]
        acknowledge <project-folder>
        glossary <project-folder> <source-term> <target-term>
        Use LOCARA_API_KEY, LOCARA_ZIP_PASSWORD and LOCARA_REGION for session credentials.
        Translate limits default to 50,000 source characters / 100 requests. Configure
        LOCARA_MAX_CHARACTERS and LOCARA_MAX_REQUESTS to set explicit limits.
        """);return;
}
using var stop=new CancellationTokenSource();Console.CancelKeyPress+=(_,e)=>{e.Cancel=true;stop.Cancel();};
try
{
    if (args is ["steam"])
    {
        Console.WriteLine(JsonSerializer.Serialize(SteamLibrary.Discover(stop.Token)));
        return;
    }
    if(args.Length<2)throw new ArgumentException("Project path required. Use --help.");
    if(args[0]=="create")
    {
        if(args.Length<3)throw new ArgumentException("Source path required.");
        using var created=ProjectStore.Create(args[1],Path.GetFileName(Path.GetFullPath(args[1])),args[2],args.ElementAtOrDefault(3)??"auto");Console.WriteLine("Created "+created.DirectoryPath);return;
    }
    using var project=new ProjectStore(args[1]);var service=new ProjectService(project);var password=Environment.GetEnvironmentVariable("LOCARA_ZIP_PASSWORD");
    switch(args[0])
    {
        case "source":
            if (args.Length is not (3 or 4) || (args.Length == 4 && args[3] != "--relocate"))
                throw new ArgumentException("source <project-folder> <path> [--relocate]");
            project.ChangeSource(args[2], args.Length == 4);
            Console.WriteLine("Source selected. Scan before building.");
            break;
        case "packages": Console.WriteLine(JsonSerializer.Serialize(service.DiscoverPackages(password, stop.Token))); break;
        case "languages": Console.WriteLine(JsonSerializer.Serialize(service.Languages(stop.Token))); break;
        case "capabilities":
            var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter<GameLaunch>() } };
            Console.WriteLine(JsonSerializer.Serialize(service.Capabilities(password, stop.Token), options));
            break;
        case "package":
            if (args.Length != 3) throw new ArgumentException("Select a relative package path or --loose.");
            service.SelectPackage(args[2] == "--loose" ? null : args[2]);
            break;
        case "resources": Console.WriteLine(JsonSerializer.Serialize(service.ListConfigurableResources(password, stop.Token))); break;
        case "inspect":
            if (args.Length != 3) throw new ArgumentException("Resource path required.");
            Console.WriteLine(JsonSerializer.Serialize(service.InspectResource(args[2], password, stop.Token)));
            break;
        case "configure":
            if (args.Length != 4) throw new ArgumentException("Resource path and rule JSON file or --auto required.");
            service.ConfigureResource(args[2], args[3] == "--auto" ? null :
                JsonSerializer.Deserialize<ExtractionRule>(File.ReadAllText(args[3]))
                ?? throw new ArgumentException("Invalid extraction rule."));
            break;
        case "plan": Console.WriteLine(JsonSerializer.Serialize(service.Preview(args.Contains("--final"), password, stop.Token))); break;
        case "scan":Console.WriteLine(JsonSerializer.Serialize(service.Scan(password,stop.Token)));break;
        case "files":
            var fileOpts = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter<FileQualityVerdict>() } };
            Console.WriteLine(JsonSerializer.Serialize(service.ScannedFiles(), fileOpts));
            break;
        case "clusters":
            var clusterOpts = new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter<FileQualityVerdict>(), new JsonStringEnumConverter<FieldSemanticHint>() } };
            Console.WriteLine(JsonSerializer.Serialize(service.BuildSchemaClusters(), clusterOpts));
            break;
        case "advice":
            Console.WriteLine(JsonSerializer.Serialize(service.AnalyzeDeployment(password, stop.Token), new JsonSerializerOptions { WriteIndented = true }));
            break;
        case "scout":
        {
            string[] positional = args.Skip(2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
            if (positional.Length < 2) throw new ArgumentException("scout <project-folder> <provider> <model> [base-url] [--apply]");
            bool applyScout = args.Contains("--apply");
            string apiKey = Environment.GetEnvironmentVariable("LOCARA_API_KEY") ?? "";
            string? baseUrl = positional.ElementAtOrDefault(2);
            var scoutOpts = new ProviderOptions(positional[0], positional[1], apiKey, baseUrl, Environment.GetEnvironmentVariable("LOCARA_REGION"));
            var scoutPlan = await LocalizationScout.AnalyzeAsync(project, scoutOpts, stop.Token);
            if (applyScout && scoutPlan.Recommendations.Count > 0)
            {
                var applied = service.ApplyScoutPlan(scoutPlan, null, password, stop.Token);
                Console.WriteLine(JsonSerializer.Serialize(new { plan = scoutPlan, applied }, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine(JsonSerializer.Serialize(scoutPlan, new JsonSerializerOptions { WriteIndented = true }));
            }
            break;
        }
        case "include":
        case "exclude":
            if (args.Length < 3 || args.Length > 4) throw new ArgumentException($"{args[0]} <project-folder> <file-path> [--human-only]");
            bool humanOnly = args[0] == "include" && args.Contains("--human-only");
            service.SetFileIncluded(args[2], args[0] == "include", humanOnly, password);
            Console.WriteLine($"{(args[0] == "include" ? (humanOnly ? "Included (human-only)" : "Included") : "Excluded")} {args[2]}");
            break;
        case "list":Console.WriteLine(JsonSerializer.Serialize(project.Units(args.ElementAtOrDefault(2)??"",int.Parse(args.ElementAtOrDefault(3)??"0")),new JsonSerializerOptions{WriteIndented=true}));break;
        case "edit":case "approve":
            if(args.Length<3)throw new ArgumentException("Unit ID required.");
            var unit=project.FindUnit(args[2]);if(args[0]=="edit")unit.Target=args.ElementAtOrDefault(3)??throw new ArgumentException("Translation required.");project.Save(unit,args[0]=="approve");Console.WriteLine(unit.Status+" "+unit.Warning);break;
        case "build":Console.WriteLine(JsonSerializer.Serialize(service.Build(args.ElementAtOrDefault(2)??throw new ArgumentException("Output required."),args.Contains("--final"),password,stop.Token)));break;
        case "export-xunity":
            Console.WriteLine(JsonSerializer.Serialize(service.ExportXUnityAutoTranslator(args.ElementAtOrDefault(2)??throw new ArgumentException("Output required."),args.Contains("--final"))));break;
        case "install":
            if(args.Length!=4)throw new ArgumentException("Output directory and game copy required.");
            Console.WriteLine(JsonSerializer.Serialize(PackageInstaller.Install(project,args[2],args[3],stop.Token)));break;
        case "restore":
            if(args.Length!=3)throw new ArgumentException("Installation receipt directory required.");
            Console.WriteLine($"Restored {PackageInstaller.Restore(args[2],stop.Token)} files.");break;
        case "estimate":var e=new TranslationRunner(project).Estimate();Console.WriteLine($"{e.Units} units / {e.Characters} source characters. Provider pricing depends on model and token usage.");break;
        case "translate":
            if(args.Length<4)throw new ArgumentException("Provider and model required.");
            await new TranslationRunner(project).Run(new(args[2],args[3],Environment.GetEnvironmentVariable("LOCARA_API_KEY")??"",args.ElementAtOrDefault(4),Environment.GetEnvironmentVariable("LOCARA_REGION"),MaxCharacters:int.Parse(Environment.GetEnvironmentVariable("LOCARA_MAX_CHARACTERS")??"50000"),MaxRequests:int.Parse(Environment.GetEnvironmentVariable("LOCARA_MAX_REQUESTS")??"100")),new Progress<TranslationProgress>(p=>Console.WriteLine($"{p.Completed}/{p.Total} {p.Message}")),stop.Token);break;
        case "acknowledge":new TranslationRunner(project).AcknowledgeAttempts();Console.WriteLine("Unresolved attempts acknowledged. Pending units can be requested again.");break;
        case "glossary":
            if (args.Length != 4) throw new ArgumentException("Source term and target term required.");
            project.Glossary(args[2], args[3]);
            break;
        default:throw new ArgumentException("Unknown command. Use --help.");
    }
}
catch(OperationCanceledException){Console.Error.WriteLine("Cancelled. Saved work is retained.");Environment.ExitCode=2;}
catch(Exception ex){Console.Error.WriteLine(ex.Message);Environment.ExitCode=1;}
