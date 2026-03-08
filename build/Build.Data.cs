using Serilog;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Nuke.Common.Assert;

internal sealed partial class BuildScript
{
    static readonly Regex DataParserEntryRegex = new(
        @"^\s*(?<id>[0-9A-Fa-f]{4})(?:\s*\(Offset\s*[0-9A-Fa-f]+\))?\s*-\s*(?<payload>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex DataParserSectionRegex = new(
        @"^---\s*(?<section>.+?)\s*---\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex MonsterIndexRegex = new(
        @"^Index\s+(?<id>\d+)\s+\[[^\]]+\].*-\s*Name:\s*(?<name>.+?)(?:\s+\(Offset\s+[0-9A-Fa-f]+\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex ScriptDisplayIdRegex = new(
        @"^(?<id>[A-Za-z0-9_]+)(?:\s*\(.+\))?\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex ScriptStringLiteralRegex = new(
        "string=\"(?<text>.*)\"\\s+\\[(?<idx>[0-9A-Fa-f]+)h\\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex StringDumpLineRegex = new(
        @"^String\s+\d+\s+\[(?<idx>[0-9A-Fa-f]+)h\]:\s*(?<text>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    static readonly Regex BattleStringPathRegex = new(
        @"(?:^|/)ffx_ps2/ffx/master/new_[^/]+pc/battle/btl/(?<id>[^/]+)/[^/]+\.bin$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex EventStringPathRegex = new(
        @"(?:^|/)ffx_ps2/ffx/master/new_[^/]+pc/event/obj_ps3/[^/]+/(?<id>[^/]+)/[^/]+\.bin$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    sealed class LocalizedCommandEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty;
        public string? DamageType { get; set; }
    }

    sealed class LocalizedMonsterEntry
    {
        public string Name { get; set; } = string.Empty;
        public string Sensor { get; set; } = string.Empty;
        public string Scan { get; set; } = string.Empty;
    }

    sealed class LocalizedDomainFile<TEntry>
    {
        public int SchemaVersion { get; set; } = 1;
        public string Domain { get; set; } = string.Empty;
        public string Locale { get; set; } = string.Empty;
        public string GeneratedAtUtc { get; set; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        public List<string> SourceFiles { get; set; } = [];
        public Dictionary<string, TEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    sealed class RuntimeLocalizedBundle
    {
        public int SchemaVersion { get; set; } = 1;
        public string Locale { get; set; } = string.Empty;
        public string GeneratedAtUtc { get; set; } = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        public RuntimeLocalizedDomains Domains { get; set; } = new();
    }

    sealed class RuntimeLocalizedDomains
    {
        public Dictionary<string, LocalizedCommandEntry> Commands { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LocalizedCommandEntry> AutoAbilities { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LocalizedCommandEntry> KeyItems { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, LocalizedMonsterEntry> Monsters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> Battles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Dictionary<string, string>> Events { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly record struct ParserInvocation(string Mode, IReadOnlyList<string> Args, string OutputBaseName);

    static readonly string[] DataParserModes =
    [
        "GREP",
        "TRANSLATE",
        "READ_ALL_COMMANDS",
        "READ_KEY_ITEMS",
        "READ_GEAR_ABILITIES",
        "READ_TREASURES",
        "READ_GEAR_SHOPS",
        "READ_ITEM_SHOPS",
        "READ_MISC_TEXTS",
        "READ_MONSTER_LOCALIZATIONS",
        "READ_WEAPON_FILE",
        "READ_STRING_FILE",
        "PARSE_ATEL_FILE",
        "PARSE_MONSTER",
        "PARSE_ALL_MONSTERS",
        "PARSE_BATTLE",
        "PARSE_ALL_BATTLES",
        "PARSE_EVENT",
        "PARSE_ALL_EVENTS",
        "READ_SPHERE_GRID_NODE_TYPES",
        "READ_SPHERE_GRID_LAYOUT",
        "READ_CUSTOMIZATIONS",
        "READ_MACROS",
        "MAKE_EDITS",
        "MAKE_AUTOHASTE_MOD",
        "READ_BLITZBALL_STATS",
        "READ_ENCOUNTER_TABLE",
        "READ_MIX_TABLE",
        "READ_CTB_BASE",
        "READ_PC_STATS",
        "READ_WEAPON_NAMES",
        "ADD_ATEL_SPACE",
        "REMAKE_SIZE_TABLE",
        "RECOMPILE",
        "GUI",
        "CUSTOM"
    ];

    void SetupDataParserCore()
    {
        EnsureJavaInstalled();

        var parserDir = ResolvePath(ParserDir);
        var parserRoot = Path.GetDirectoryName(parserDir);
        if (!string.IsNullOrWhiteSpace(parserRoot))
        {
            EnsureDir(parserRoot);
        }

        var gitDir = Path.Combine(parserDir, ".git");
        if (!Directory.Exists(parserDir))
        {
            if (DryRun)
            {
                Log.Information($"[DRY-RUN] git clone {ParserRepo} {parserDir}");
            }
            else
            {
                RunChecked("git", $"clone {Quote(ParserRepo)} {Quote(parserDir)}", "Clone FFXDataParser", showSpinner: true, silent: true);
            }
        }
        else if (!Directory.Exists(gitDir))
        {
            Fail($"Data parser directory exists but is not a git clone: {parserDir}");
        }
        else
        {
            if (DryRun)
            {
                Log.Information($"[DRY-RUN] git -C {parserDir} fetch --all --tags --prune");
                if (string.IsNullOrWhiteSpace(ParserRef))
                {
                    Log.Information($"[DRY-RUN] git -C {parserDir} pull --ff-only");
                }
            }
            else
            {
                RunChecked("git", "fetch --all --tags --prune", "Update FFXDataParser", workingDirectory: parserDir, showSpinner: true, silent: true);
                if (string.IsNullOrWhiteSpace(ParserRef))
                {
                    RunChecked("git", "pull --ff-only", "Fast-forward FFXDataParser", workingDirectory: parserDir, showSpinner: true, silent: true);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(ParserRef))
        {
            if (DryRun)
            {
                Log.Information($"[DRY-RUN] git -C {parserDir} checkout {ParserRef}");
            }
            else
            {
                RunChecked("git", $"checkout {Quote(ParserRef)}", "Checkout FFXDataParser ref", workingDirectory: parserDir, showSpinner: true, silent: true);
            }
        }

        BuildDataParserJar(parserDir);

        if (!DryRun)
        {
            var jarPath = ResolveDataParserJarPath(parserDir);
            Log.Information($"FFXDataParser ready: {jarPath}");
        }
    }

    void ParseDataCore()
    {
        var mode = NormalizeDataParserMode(DataMode);
        var args = SplitCommandLineTokens(DataArgs);
        var invocation = CreateParserInvocation(mode, args);
        RunParserInvocationsCore([invocation], failIfMissingDataRoot: !DryRun);
    }

    void ParseDataAllCore()
    {
        var specs = ParseListArgument(DataBatch);
        if (specs.Count == 0)
        {
            specs = ParseListArgument("READ_ALL_COMMANDS;READ_GEAR_ABILITIES;READ_KEY_ITEMS;READ_MONSTER_LOCALIZATIONS us;READ_MONSTER_LOCALIZATIONS de");
        }

        var invocations = new List<ParserInvocation>();
        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            if (string.IsNullOrWhiteSpace(spec))
            {
                continue;
            }

            var tokens = SplitCommandLineTokens(spec);
            if (tokens.Count == 0)
            {
                continue;
            }

            var mode = NormalizeDataParserMode(tokens[0]);
            var args = tokens.Skip(1).ToArray();
            invocations.Add(CreateParserInvocation(mode, args));
        }

        if (invocations.Count == 0)
        {
            Fail("No valid parser mode found in --databatch.");
        }

        RunParserInvocationsCore(invocations, failIfMissingDataRoot: !DryRun);
    }

    void ImportLocalizedMappingsCore()
    {
        var parserOutDir = ResolvePath(DataOut);
        EnsureDir(parserOutDir);

        var locales = ResolveMappingLocales();
        if (locales.Count == 0)
        {
            Fail("No locales configured for mapping import. Pass --locales, e.g. us,de");
        }

        ensure_parser_output_file(parserOutDir, "READ_ALL_COMMANDS.txt", "READ_ALL_COMMANDS");
        ensure_parser_output_file(parserOutDir, "READ_GEAR_ABILITIES.txt", "READ_GEAR_ABILITIES");
        ensure_parser_output_file(parserOutDir, "READ_KEY_ITEMS.txt", "READ_KEY_ITEMS");

        var sourceRoot = ResolvePath(MapSource);
        if (!DryRun)
        {
            EnsureDir(sourceRoot);
        }

        var commandsPath = Path.Combine(parserOutDir, "READ_ALL_COMMANDS.txt");
        var autoAbilitiesPath = Path.Combine(parserOutDir, "READ_GEAR_ABILITIES.txt");
        var keyItemsPath = Path.Combine(parserOutDir, "READ_KEY_ITEMS.txt");

        var commands = ParseCommandDomainFromDump(commandsPath, forcedKind: null);
        var autoAbilities = ParseCommandDomainFromDump(autoAbilitiesPath, forcedKind: "AutoAbility");
        var keyItems = ParseCommandDomainFromDump(keyItemsPath, forcedKind: "KeyItem");
        var localizedCommandsByLocale = new Dictionary<string, Dictionary<string, LocalizedCommandEntry>>(StringComparer.OrdinalIgnoreCase);
        var localizedAutoAbilitiesByLocale = new Dictionary<string, Dictionary<string, LocalizedCommandEntry>>(StringComparer.OrdinalIgnoreCase);
        var localizedKeyItemsByLocale = new Dictionary<string, Dictionary<string, LocalizedCommandEntry>>(StringComparer.OrdinalIgnoreCase);
        var localizedCommandSourcesByLocale = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var parsedEvents = ParseScriptDomainFromParserDumps(parserOutDir, scriptDomain: "events", out var parsedEventSources);
        var parsedBattles = ParseScriptDomainFromParserDumps(parserOutDir, scriptDomain: "battles", out var parsedBattleSources);
        var parsedEventStringTablesByLocale = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var parsedBattleStringTablesByLocale = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);
        var parsedEventStringSourcesByLocale = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var parsedBattleStringSourcesByLocale = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        var usEventStringDir = build_localized_string_relative_dir("us", "events");
        var usBattleStringDir = build_localized_string_relative_dir("us", "battles");
        var usEventStrings = ParseScriptDomainFromReadStringDump(
            parserOutDir,
            scriptDomain: "events",
            relativeDir: usEventStringDir,
            locale: "us",
            out var usEventStringSources);
        var usBattleStrings = ParseScriptDomainFromReadStringDump(
            parserOutDir,
            scriptDomain: "battles",
            relativeDir: usBattleStringDir,
            locale: "us",
            out var usBattleStringSources);

        for (var i = 0; i < locales.Count; i++)
        {
            var locale = locales[i];
            if (locale.Equals("us", StringComparison.OrdinalIgnoreCase))
            {
                parsedEventStringTablesByLocale[locale] = clone_script_domain(usEventStrings);
                parsedBattleStringTablesByLocale[locale] = clone_script_domain(usBattleStrings);
                parsedEventStringSourcesByLocale[locale] = new List<string>(usEventStringSources);
                parsedBattleStringSourcesByLocale[locale] = new List<string>(usBattleStringSources);
                continue;
            }

            var localeEventStringDir = build_localized_string_relative_dir(locale, "events");
            var localeBattleStringDir = build_localized_string_relative_dir(locale, "battles");
            var localeEventStrings = ParseScriptDomainFromReadStringDump(
                parserOutDir,
                scriptDomain: "events",
                relativeDir: localeEventStringDir,
                locale: locale,
                out var localeEventStringSources);
            var localeBattleStrings = ParseScriptDomainFromReadStringDump(
                parserOutDir,
                scriptDomain: "battles",
                relativeDir: localeBattleStringDir,
                locale: locale,
                out var localeBattleStringSources);

            if (count_script_entries(localeEventStrings) == 0)
            {
                parsedEventStringTablesByLocale[locale] = clone_script_domain(usEventStrings);
                parsedEventStringSourcesByLocale[locale] = usEventStringSources.Select(x => $"{x} (fallback-from-us)").ToList();
            }
            else
            {
                parsedEventStringTablesByLocale[locale] = localeEventStrings;
                parsedEventStringSourcesByLocale[locale] = localeEventStringSources;
            }

            if (count_script_entries(localeBattleStrings) == 0)
            {
                parsedBattleStringTablesByLocale[locale] = clone_script_domain(usBattleStrings);
                parsedBattleStringSourcesByLocale[locale] = usBattleStringSources.Select(x => $"{x} (fallback-from-us)").ToList();
            }
            else
            {
                parsedBattleStringTablesByLocale[locale] = localeBattleStrings;
                parsedBattleStringSourcesByLocale[locale] = localeBattleStringSources;
            }
        }
        if (commands.Count == 0 && autoAbilities.Count == 0 && keyItems.Count == 0 && !DryRun)
        {
            Fail($"No command-like mappings extracted from {parserOutDir}.");
        }

        Dictionary<string, LocalizedMonsterEntry> fallbackMonsters = [];
        string? fallbackMonsterSource = FindMonsterLocalizationDump(parserOutDir, "us");
        if (!string.IsNullOrWhiteSpace(fallbackMonsterSource))
        {
            fallbackMonsters = ParseMonsterDomainFromLocalizationDump(fallbackMonsterSource);
        }

        for (var i = 0; i < locales.Count; i++)
        {
            var locale = locales[i];
            if (try_load_localized_command_domains(
                parserOutDir,
                locale,
                out var localeCommands,
                out var localeAutoAbilities,
                out var localeKeyItems,
                out var localeCommandSources)
                && (localeCommands.Count + localeAutoAbilities.Count + localeKeyItems.Count) > 0)
            {
                localizedCommandsByLocale[locale] = localeCommands;
                localizedAutoAbilitiesByLocale[locale] = localeAutoAbilities;
                localizedKeyItemsByLocale[locale] = localeKeyItems;
                localizedCommandSourcesByLocale[locale] = localeCommandSources;
            }
            else
            {
                localizedCommandsByLocale[locale] = commands;
                localizedAutoAbilitiesByLocale[locale] = autoAbilities;
                localizedKeyItemsByLocale[locale] = keyItems;
                localizedCommandSourcesByLocale[locale] = [Path.GetFileName(commandsPath), $"{Path.GetFileName(autoAbilitiesPath)} (fallback-from-us)", $"{Path.GetFileName(keyItemsPath)} (fallback-from-us)"];
            }
        }

        for (var i = 0; i < locales.Count; i++)
        {
            var locale = locales[i];
            var localeDir = Path.Combine(sourceRoot, locale);
            var localeMonsterSource = FindMonsterLocalizationDump(parserOutDir, locale);
            Dictionary<string, LocalizedMonsterEntry> monsters;
            List<string> monsterSources;

            if (!string.IsNullOrWhiteSpace(localeMonsterSource))
            {
                monsters = ParseMonsterDomainFromLocalizationDump(localeMonsterSource);
                monsterSources = [Path.GetFileName(localeMonsterSource)];
            }
            else
            {
                monsters = new Dictionary<string, LocalizedMonsterEntry>(fallbackMonsters, StringComparer.OrdinalIgnoreCase);
                monsterSources = fallbackMonsterSource is null
                    ? []
                    : [$"{Path.GetFileName(fallbackMonsterSource)} (fallback-from-us)"];
            }
            var localeCommands = localizedCommandsByLocale.TryGetValue(locale, out var localizedCommands)
                ? localizedCommands
                : commands;
            var localeAutoAbilities = localizedAutoAbilitiesByLocale.TryGetValue(locale, out var localizedAutoAbilities)
                ? localizedAutoAbilities
                : autoAbilities;
            var localeKeyItems = localizedKeyItemsByLocale.TryGetValue(locale, out var localizedKeyItems)
                ? localizedKeyItems
                : keyItems;
            var localeCommandSources = localizedCommandSourcesByLocale.TryGetValue(locale, out var localizedCommandSources)
                ? localizedCommandSources
                : [Path.GetFileName(commandsPath)];

            backfill_damage_type(localeCommands, commands);
            backfill_damage_type(localeAutoAbilities, autoAbilities);
            backfill_damage_type(localeKeyItems, keyItems);

            var eventsMap = clone_script_domain(parsedEvents);
            var battlesMap = clone_script_domain(parsedBattles);
            var eventSources = new List<string>();
            var battleSources = new List<string>();
            var localeEventStrings = parsedEventStringTablesByLocale.TryGetValue(locale, out var localeEventStringMap)
                ? localeEventStringMap
                : clone_script_domain(usEventStrings);
            var localeBattleStrings = parsedBattleStringTablesByLocale.TryGetValue(locale, out var localeBattleStringMap)
                ? localeBattleStringMap
                : clone_script_domain(usBattleStrings);
            var localeEventStringSources = parsedEventStringSourcesByLocale.TryGetValue(locale, out var localeEventSourceList)
                ? localeEventSourceList
                : usEventStringSources.Select(x => $"{x} (fallback-from-us)").ToList();
            var localeBattleStringSources = parsedBattleStringSourcesByLocale.TryGetValue(locale, out var localeBattleSourceList)
                ? localeBattleSourceList
                : usBattleStringSources.Select(x => $"{x} (fallback-from-us)").ToList();

            merge_script_domain(eventsMap, localeEventStrings, overwriteExisting: false);
            merge_script_domain(battlesMap, localeBattleStrings, overwriteExisting: false);

            if (parsedEventSources.Count > 0)
            {
                if (locale.Equals("us", StringComparison.OrdinalIgnoreCase))
                {
                    eventSources.AddRange(parsedEventSources);
                }
                else
                {
                    eventSources.AddRange(parsedEventSources.Select(x => $"{x} (fallback-from-us)"));
                }
            }

            if (parsedBattleSources.Count > 0)
            {
                if (locale.Equals("us", StringComparison.OrdinalIgnoreCase))
                {
                    battleSources.AddRange(parsedBattleSources);
                }
                else
                {
                    battleSources.AddRange(parsedBattleSources.Select(x => $"{x} (fallback-from-us)"));
                }
            }

            if (localeEventStringSources.Count > 0)
            {
                eventSources.AddRange(localeEventStringSources);
            }

            if (localeBattleStringSources.Count > 0)
            {
                battleSources.AddRange(localeBattleStringSources);
            }

            if (DryRun)
            {
                Log.Information($"[DRY-RUN] Would write canonical mappings for locale '{locale}' into {localeDir}");
                continue;
            }

            EnsureDir(localeDir);
            WriteLocalizedDomainFile(
                Path.Combine(localeDir, "commands.json"),
                domain: "commands",
                locale: locale,
                entries: localeCommands,
                sourceFiles: localeCommandSources.Where(x => x.Contains("command", StringComparison.OrdinalIgnoreCase) || x.Contains("READ_ALL_COMMANDS_LOCALIZED", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            WriteLocalizedDomainFile(
                Path.Combine(localeDir, "autoabilities.json"),
                domain: "autoabilities",
                locale: locale,
                entries: localeAutoAbilities,
                sourceFiles: localeCommandSources.Where(x => x.Contains("auto", StringComparison.OrdinalIgnoreCase) || x.Contains("READ_ALL_COMMANDS_LOCALIZED", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            WriteLocalizedDomainFile(
                Path.Combine(localeDir, "keyitems.json"),
                domain: "keyitems",
                locale: locale,
                entries: localeKeyItems,
                sourceFiles: localeCommandSources.Where(x => x.Contains("key", StringComparison.OrdinalIgnoreCase) || x.Contains("READ_ALL_COMMANDS_LOCALIZED", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            WriteLocalizedDomainFile(
                Path.Combine(localeDir, "monsters.json"),
                domain: "monsters",
                locale: locale,
                entries: monsters,
                sourceFiles: monsterSources);
            WriteLocalizedDomainFile(
                Path.Combine(localeDir, "battles.json"),
                domain: "battles",
                locale: locale,
                entries: battlesMap,
                sourceFiles: battleSources);
            WriteLocalizedDomainFile(
                Path.Combine(localeDir, "events.json"),
                domain: "events",
                locale: locale,
                entries: eventsMap,
                sourceFiles: eventSources);
        }

        if (parsedEvents.Count == 0)
        {
            Log.Warning("No event string mappings found. Generate parser outputs with `build.cmd dataparse --datamode PARSE_ALL_EVENTS`.");
        }

        if (parsedBattles.Count == 0)
        {
            Log.Warning("No battle string mappings found. Generate parser outputs with `build.cmd dataparse --datamode PARSE_ALL_BATTLES`.");
        }

        Log.Information($"Imported canonical locale mappings for: {string.Join(", ", locales)}");
    }

    void BuildLocalizedBundlesCore()
    {
        var sourceRoot = ResolvePath(MapSource);
        var outDir = ResolvePath(MapOut);
        var publishDir = ResolvePath(MapPublish);
        var publishToSeparateDir = !string.Equals(outDir, publishDir, StringComparison.OrdinalIgnoreCase);
        var locales = ResolveMappingLocales();
        if (locales.Count == 0)
        {
            Fail("No locales configured for mapping bundles. Pass --locales, e.g. us,de");
        }

        if (!DryRun)
        {
            EnsureDir(outDir);
            EnsureDir(publishDir);
        }

        for (var i = 0; i < locales.Count; i++)
        {
            var locale = locales[i];
            var localeDir = Path.Combine(sourceRoot, locale);
            var commands = ReadLocalizedDomainEntries<LocalizedCommandEntry>(Path.Combine(localeDir, "commands.json"));
            var autoAbilities = ReadLocalizedDomainEntries<LocalizedCommandEntry>(Path.Combine(localeDir, "autoabilities.json"));
            var keyItems = ReadLocalizedDomainEntries<LocalizedCommandEntry>(Path.Combine(localeDir, "keyitems.json"));
            var monsters = ReadLocalizedDomainEntries<LocalizedMonsterEntry>(Path.Combine(localeDir, "monsters.json"));
            var battles = ReadLocalizedDomainEntries<Dictionary<string, string>>(Path.Combine(localeDir, "battles.json"));
            var eventsMap = ReadLocalizedDomainEntries<Dictionary<string, string>>(Path.Combine(localeDir, "events.json"));

            var bundle = new RuntimeLocalizedBundle
            {
                SchemaVersion = 1,
                Locale = locale,
                GeneratedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                Domains = new RuntimeLocalizedDomains
                {
                    Commands = commands,
                    AutoAbilities = autoAbilities,
                    KeyItems = keyItems,
                    Monsters = monsters,
                    Battles = battles,
                    Events = eventsMap
                }
            };

            var fileName = $"ffx-mappings.{locale}.json";
            var outFile = Path.Combine(outDir, fileName);
            var publishFile = Path.Combine(publishDir, fileName);

            if (DryRun)
            {
                Log.Information($"[DRY-RUN] Would write localized mapping bundle: {outFile}");
                if (publishToSeparateDir)
                {
                    Log.Information($"[DRY-RUN] Would publish localized mapping bundle: {publishFile}");
                }
                continue;
            }

            WriteJsonFile(outFile, bundle);
            if (publishToSeparateDir)
            {
                File.Copy(outFile, publishFile, overwrite: true);
            }

            if (locale.Equals("us", StringComparison.OrdinalIgnoreCase))
            {
                // Keep non-locale file for compatibility with older loaders and tools.
                File.Copy(outFile, Path.Combine(outDir, "ffx-mappings.json"), overwrite: true);
                if (publishToSeparateDir)
                {
                    File.Copy(outFile, Path.Combine(publishDir, "ffx-mappings.json"), overwrite: true);
                }
            }

            Log.Information(
                $"Built bundle {fileName}: cmd={commands.Count}, auto={autoAbilities.Count}, key={keyItems.Count}, " +
                $"monster={monsters.Count}, battle={battles.Count}, event={eventsMap.Count}");
        }
    }

    List<string> ResolveMappingLocales()
    {
        var raw = new List<string>();
        if (Locales is { Length: > 0 })
        {
            raw.AddRange(Locales);
        }

        if (raw.Count == 0)
        {
            raw = ["us", "de"];
        }

        return raw
            .Select(NormalizeMappingLocale)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string NormalizeMappingLocale(string locale)
    {
        var lower = (locale ?? string.Empty).Trim().ToLowerInvariant();
        if (lower.StartsWith("de", StringComparison.Ordinal)) return "de";
        if (lower.StartsWith("fr", StringComparison.Ordinal)) return "fr";
        if (lower.StartsWith("it", StringComparison.Ordinal)) return "it";
        if (lower.StartsWith("es", StringComparison.Ordinal) || lower.StartsWith("sp", StringComparison.Ordinal)) return "sp";
        if (lower.StartsWith("ja", StringComparison.Ordinal) || lower.StartsWith("jp", StringComparison.Ordinal)) return "jp";
        if (lower.StartsWith("ko", StringComparison.Ordinal) || lower.StartsWith("kr", StringComparison.Ordinal)) return "kr";
        if (lower.StartsWith("zh", StringComparison.Ordinal) || lower.StartsWith("ch", StringComparison.Ordinal)) return "ch";
        return "us";
    }

    void ensure_parser_output_file(string parserOutDir, string fileName, string mode)
    {
        var path = Path.Combine(parserOutDir, fileName);
        if (File.Exists(path))
        {
            return;
        }

        Log.Information($"{fileName} not found. Running parser mode {mode}.");
        RunParserInvocationsCore(
            [CreateParserInvocation(mode, Array.Empty<string>())],
            failIfMissingDataRoot: !DryRun);
    }

    Dictionary<string, LocalizedCommandEntry> ParseCommandDomainFromDump(string path, string? forcedKind)
    {
        var result = new Dictionary<string, LocalizedCommandEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        var section = string.Empty;
        foreach (var line in File.ReadLines(path))
        {
            var sectionMatch = DataParserSectionRegex.Match(line);
            if (sectionMatch.Success)
            {
                section = sectionMatch.Groups["section"].Value.Trim();
                continue;
            }

            var entryMatch = DataParserEntryRegex.Match(line);
            if (!entryMatch.Success)
            {
                continue;
            }

            if (!ushort.TryParse(entryMatch.Groups["id"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) || id == 0)
            {
                continue;
            }

            var payload = (entryMatch.Groups["payload"].Value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(payload))
            {
                continue;
            }

            var key = id.ToString("X4", CultureInfo.InvariantCulture);
            var name = ExtractMappingName(payload);
            var description = extract_description(payload);
            var kind = string.IsNullOrWhiteSpace(forcedKind)
                ? ClassifyCommandKind(id, section)
                : forcedKind;
            var damageType = extract_damage_type(payload);

            if (!result.TryGetValue(key, out var entry))
            {
                entry = new LocalizedCommandEntry();
                result[key] = entry;
            }

            if (string.IsNullOrWhiteSpace(entry.Name) && !string.IsNullOrWhiteSpace(name))
            {
                entry.Name = name;
            }

            if (string.IsNullOrWhiteSpace(entry.Description) && !string.IsNullOrWhiteSpace(description))
            {
                entry.Description = description;
            }

            if (string.IsNullOrWhiteSpace(entry.Kind))
            {
                entry.Kind = kind;
            }

            if (entry.DamageType is null)
            {
                entry.DamageType = damageType;
            }
        }

        return result;
    }

    static string extract_description(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return string.Empty;
        }

        var closingBrace = find_metadata_closing_brace(payload);
        if (closingBrace >= 0 && closingBrace + 1 < payload.Length)
        {
            return payload[(closingBrace + 1)..].Trim();
        }

        return string.Empty;
    }

    static string extract_damage_type(string payload)
    {
        var braceOpen = payload.IndexOf('{');
        if (braceOpen < 0)
        {
            return "Unknown";
        }

        var contentStart = braceOpen + 1;
        var commaPos = payload.IndexOf(',', contentStart);
        var braceClose = payload.IndexOf('}', contentStart);

        int tokenEnd;
        if (commaPos >= 0 && (braceClose < 0 || commaPos < braceClose))
        {
            tokenEnd = commaPos;
        }
        else if (braceClose >= 0)
        {
            tokenEnd = braceClose;
        }
        else
        {
            return "Unknown";
        }

        var token = payload[contentStart..tokenEnd].Trim();

        if (token.StartsWith("Physical", StringComparison.OrdinalIgnoreCase))
        {
            return "Physical";
        }

        if (token.StartsWith("Magical", StringComparison.OrdinalIgnoreCase))
        {
            return "Magical";
        }

        if (token.StartsWith("Special", StringComparison.OrdinalIgnoreCase))
        {
            return "Special";
        }

        return "Unknown";
    }

    static void backfill_damage_type(
        Dictionary<string, LocalizedCommandEntry> target,
        Dictionary<string, LocalizedCommandEntry> source)
    {
        foreach (var (key, entry) in target)
        {
            if (entry.DamageType is not null && entry.DamageType != "Unknown")
            {
                continue;
            }

            if (source.TryGetValue(key, out var sourceEntry) && sourceEntry.DamageType is not null)
            {
                entry.DamageType = sourceEntry.DamageType;
            }
        }
    }

    static int find_metadata_closing_brace(string payload)
    {
        var start = payload.IndexOf('{');
        if (start < 0) return -1;

        var depth = 0;
        for (var i = start; i < payload.Length; i++)
        {
            var c = payload[i];
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    string? FindMonsterLocalizationDump(string parserOutDir, string locale)
    {
        if (!Directory.Exists(parserOutDir))
        {
            return null;
        }

        var normalized = NormalizeMappingLocale(locale);
        var candidates = Directory.EnumerateFiles(parserOutDir, "READ_MONSTER_LOCALIZATIONS*.txt", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < candidates.Count; i++)
        {
            var name = Path.GetFileNameWithoutExtension(candidates[i]).ToLowerInvariant();
            if (name.EndsWith($"__{normalized}", StringComparison.Ordinal))
            {
                return candidates[i];
            }
        }

        return null;
    }

    Dictionary<string, LocalizedMonsterEntry> ParseMonsterDomainFromLocalizationDump(string path)
    {
        var result = new Dictionary<string, LocalizedMonsterEntry>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return result;
        }

        var currentId = -1;
        var captureMode = string.Empty;
        foreach (var raw in File.ReadLines(path))
        {
            var line = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var indexMatch = MonsterIndexRegex.Match(line);
            if (indexMatch.Success)
            {
                if (!int.TryParse(indexMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out currentId))
                {
                    currentId = -1;
                    captureMode = string.Empty;
                    continue;
                }

                var idKey = currentId.ToString(CultureInfo.InvariantCulture);
                if (!result.TryGetValue(idKey, out var entry))
                {
                    entry = new LocalizedMonsterEntry();
                    result[idKey] = entry;
                }

                var parsedName = indexMatch.Groups["name"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(parsedName))
                {
                    entry.Name = parsedName;
                }

                captureMode = string.Empty;
                continue;
            }

            if (line.StartsWith("- Sensor Text -", StringComparison.OrdinalIgnoreCase))
            {
                captureMode = "sensor";
                continue;
            }

            if (line.StartsWith("- Scan Text -", StringComparison.OrdinalIgnoreCase))
            {
                captureMode = "scan";
                continue;
            }

            if (currentId < 0 || string.IsNullOrWhiteSpace(captureMode))
            {
                continue;
            }

            var key = currentId.ToString(CultureInfo.InvariantCulture);
            if (!result.TryGetValue(key, out var current))
            {
                current = new LocalizedMonsterEntry();
                result[key] = current;
            }

            if (captureMode == "sensor" && string.IsNullOrWhiteSpace(current.Sensor))
            {
                current.Sensor = line;
                captureMode = string.Empty;
            }
            else if (captureMode == "scan" && string.IsNullOrWhiteSpace(current.Scan))
            {
                current.Scan = line;
                captureMode = string.Empty;
            }
        }

        return result;
    }

    Dictionary<string, Dictionary<string, string>> ParseScriptDomainFromParserDumps(
        string parserOutDir,
        string scriptDomain,
        out List<string> sourceFiles)
    {
        sourceFiles = [];
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(parserOutDir))
        {
            return result;
        }

        var isEvents = scriptDomain.Equals("events", StringComparison.OrdinalIgnoreCase);
        var isBattles = scriptDomain.Equals("battles", StringComparison.OrdinalIgnoreCase);
        if (!isEvents && !isBattles)
        {
            return result;
        }

        var candidates = Directory.EnumerateFiles(parserOutDir, "*.txt", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return false;
                }

                if (isEvents)
                {
                    return name.StartsWith("PARSE_EVENT", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("PARSE_ALL_EVENTS", StringComparison.OrdinalIgnoreCase);
                }

                return name.StartsWith("PARSE_BATTLE", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("PARSE_ALL_BATTLES", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < candidates.Count; i++)
        {
            var file = candidates[i];
            var beforeCount = count_script_entries(result);
            parse_script_dump_file(file, result);
            var afterCount = count_script_entries(result);
            if (afterCount > beforeCount)
            {
                sourceFiles.Add(Path.GetFileName(file));
            }
        }

        return result;
    }

    static int count_script_entries(Dictionary<string, Dictionary<string, string>> map)
    {
        var count = 0;
        foreach (var script in map.Values)
        {
            count += script.Count;
        }

        return count;
    }

    static void parse_script_dump_file(string path, Dictionary<string, Dictionary<string, string>> destination)
    {
        if (!File.Exists(path))
        {
            return;
        }

        string currentScriptId = string.Empty;
        var expectScriptDisplayLine = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw ?? string.Empty;
            var trimmed = line.Trim();
            if (trimmed.StartsWith("---", StringComparison.Ordinal) && trimmed.EndsWith("---", StringComparison.Ordinal))
            {
                expectScriptDisplayLine = true;
                currentScriptId = string.Empty;
                continue;
            }

            if (expectScriptDisplayLine)
            {
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                expectScriptDisplayLine = false;
                if (try_extract_script_id_from_display_line(trimmed, out var scriptId))
                {
                    currentScriptId = scriptId;
                }
                else
                {
                    currentScriptId = string.Empty;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(currentScriptId))
            {
                continue;
            }

            if (!try_extract_script_text_and_index(trimmed, out var stringIndex, out var text))
            {
                continue;
            }

            if (!destination.TryGetValue(currentScriptId, out var lines))
            {
                lines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                destination[currentScriptId] = lines;
            }

            var key = stringIndex.ToString(CultureInfo.InvariantCulture);
            if (!lines.ContainsKey(key))
            {
                lines[key] = text;
            }
        }
    }

    static bool try_extract_script_id_from_display_line(string line, out string scriptId)
    {
        scriptId = string.Empty;
        var match = ScriptDisplayIdRegex.Match(line ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        var id = normalize_script_id_for_mapping(match.Groups["id"].Value);
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        scriptId = id;
        return true;
    }

    static bool try_extract_script_text_and_index(string line, out int index, out string text)
    {
        index = -1;
        text = string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var match = ScriptStringLiteralRegex.Match(line);
        if (!match.Success)
        {
            return false;
        }

        if (!int.TryParse(match.Groups["idx"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out index) || index < 0)
        {
            return false;
        }

        text = (match.Groups["text"].Value ?? string.Empty).Trim();
        return !string.IsNullOrWhiteSpace(text);
    }

    static string build_localized_string_relative_dir(string locale, string scriptDomain)
    {
        var normalizedLocale = NormalizeMappingLocale(locale);
        var suffix = scriptDomain.Equals("battles", StringComparison.OrdinalIgnoreCase)
            ? "battle/btl"
            : "event/obj_ps3";
        return $"ffx_ps2/ffx/master/new_{normalizedLocale}pc/{suffix}";
    }

    Dictionary<string, Dictionary<string, string>> ParseScriptDomainFromReadStringDump(
        string parserOutDir,
        string scriptDomain,
        string relativeDir,
        string locale,
        out List<string> sourceFiles)
    {
        sourceFiles = [];
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(parserOutDir) || string.IsNullOrWhiteSpace(relativeDir))
        {
            return result;
        }

        var outputBaseName = BuildInvocationOutputBaseName("READ_STRING_FILE_LOCALIZED", [relativeDir, NormalizeMappingLocale(locale)]);
        var outputPath = Path.Combine(parserOutDir, $"{outputBaseName}.txt");
        ensure_localized_read_string_dump(parserOutDir, relativeDir, locale, outputPath);
        if (!File.Exists(outputPath))
        {
            return result;
        }

        var totalBefore = count_script_entries(result);
        parse_read_string_dump_file(outputPath, scriptDomain, result);
        var totalAfter = count_script_entries(result);
        if (totalAfter > totalBefore)
        {
            sourceFiles.Add(Path.GetFileName(outputPath));
        }

        return result;
    }

    void ensure_localized_read_string_dump(
        string parserOutDir,
        string relativeDir,
        string locale,
        string outputPath,
        bool forceRebuildHelper = false)
    {
        if (File.Exists(outputPath))
        {
            return;
        }

        Log.Information($"Localized string dump missing for '{relativeDir}' (locale={NormalizeMappingLocale(locale)}). Generating {Path.GetFileName(outputPath)}.");
        run_localized_string_dump_helper(parserOutDir, relativeDir, locale, outputPath, forceRebuildHelper);
    }

    void run_localized_string_dump_helper(
        string parserOutDir,
        string relativeDir,
        string locale,
        string outputPath,
        bool forceRebuildHelper)
    {
        var parserDir = ResolvePath(ParserDir);
        var jarPath = ResolveDataParserJarPath(parserDir);
        var inputRoot = ResolveDataParserInputRoot(failIfMissing: !DryRun);
        var normalizedLocale = NormalizeMappingLocale(locale);

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] Would generate localized string dump: locale={normalizedLocale}, relativeDir={relativeDir}, output={outputPath}");
            return;
        }

        var helperDir = Path.Combine(parserOutDir, "_localized-string-helper");
        EnsureDir(helperDir);
        var sourcePath = Path.Combine(helperDir, "LocalizedStringDump.java");
        var classPath = Path.Combine(helperDir, "LocalizedStringDump.class");
        write_localized_string_dump_helper_source(sourcePath);

        var shouldCompileHelper = forceRebuildHelper
            || !File.Exists(classPath)
            || File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(classPath);
        if (shouldCompileHelper)
        {
            RunChecked(
                "javac",
                $"-cp {Quote(jarPath)} -d {Quote(helperDir)} {Quote(sourcePath)}",
                "Compile localized string dump helper",
                showSpinner: true,
                silent: true);
        }

        var runtimeClassPath = string.Join(Path.PathSeparator, [jarPath, helperDir]);
        var result = RunProcess(
            fileName: "java",
            args: $"-cp {Quote(runtimeClassPath)} LocalizedStringDump {Quote(EnsureTrailingDirectorySeparator(inputRoot))} {Quote(normalizedLocale)} {Quote(relativeDir)}",
            description: $"Run localized string dump helper ({normalizedLocale})",
            workingDirectory: parserOutDir,
            showSpinner: true,
            silent: true);
        if (result.ExitCode != 0)
        {
            Fail(
                $"Localized string dump helper failed with code {result.ExitCode}.{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{result.StdOut}");
        }

        var normalizedOutput = result.StdOut.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        File.WriteAllText(outputPath, normalizedOutput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log.Information($"Localized string dump captured: {outputPath}");
    }

    static void write_localized_string_dump_helper_source(string sourcePath)
    {
        const string source = """
import main.DataReadingManager;
import main.StringHelper;
import model.strings.FieldString;
import reading.FileAccessorWithMods;

import java.io.File;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Comparator;
import java.util.List;

public final class LocalizedStringDump {
    private LocalizedStringDump() {}

    public static void main(String[] args) {
        if (args.length < 3) {
            System.err.println("Usage: LocalizedStringDump <gameRoot> <locale> <relativePath>");
            System.exit(2);
            return;
        }

        String gameRoot = ensureTrailingSeparator(args[0]);
        String locale = args[1];
        String relativePath = normalizeRelative(args[2]);

        FileAccessorWithMods.GAME_FILES_ROOT = gameRoot;
        DataReadingManager.initializeInternals();

        File root = FileAccessorWithMods.getRealFile(relativePath);
        if (!root.exists()) {
            return;
        }

        List<File> files = new ArrayList<>();
        collectBinFiles(root, files);
        files.sort(Comparator.comparing(File::getAbsolutePath, String.CASE_INSENSITIVE_ORDER));

        Path gameRootPath = new File(gameRoot).toPath().toAbsolutePath().normalize();
        for (File file : files) {
            Path filePath = file.toPath().toAbsolutePath().normalize();
            String rel;
            try {
                rel = gameRootPath.relativize(filePath).toString().replace('\\', '/');
            } catch (Exception ignored) {
                rel = normalizeRelative(file.getPath());
            }

            System.out.println("--- " + rel + " ---");
            List<FieldString> strings = StringHelper.readStringFile(rel, false, locale);
            if (strings == null) {
                continue;
            }

            for (int i = 0; i < strings.size(); i++) {
                String value = strings.get(i) == null ? "" : strings.get(i).getString();
                if (value == null) {
                    value = "";
                }
                System.out.printf("String %d [%02Xh]: %s%n", i, i, value);
            }
        }
    }

    private static void collectBinFiles(File path, List<File> out) {
        if (path == null || !path.exists()) {
            return;
        }
        if (path.isFile()) {
            String name = path.getName().toLowerCase();
            if (name.endsWith(".bin")) {
                out.add(path);
            }
            return;
        }

        File[] children = path.listFiles();
        if (children == null || children.length == 0) {
            return;
        }
        Arrays.sort(children, Comparator.comparing(File::getName, String.CASE_INSENSITIVE_ORDER));
        for (File child : children) {
            if (child.getName().startsWith(".")) {
                continue;
            }
            collectBinFiles(child, out);
        }
    }

    private static String ensureTrailingSeparator(String root) {
        if (root == null || root.isBlank()) {
            return "";
        }
        if (root.endsWith("/") || root.endsWith("\\")) {
            return root;
        }
        return root + File.separator;
    }

    private static String normalizeRelative(String path) {
        if (path == null) {
            return "";
        }
        return path.replace('\\', '/');
    }
}
""";

        File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    static void parse_read_string_dump_file(
        string path,
        string scriptDomain,
        Dictionary<string, Dictionary<string, string>> destination)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var isBattle = scriptDomain.Equals("battles", StringComparison.OrdinalIgnoreCase);
        var isEvent = scriptDomain.Equals("events", StringComparison.OrdinalIgnoreCase);
        if (!isBattle && !isEvent)
        {
            return;
        }

        string currentScriptId = string.Empty;
        foreach (var raw in File.ReadLines(path))
        {
            var line = (raw ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var headerMatch = DataParserSectionRegex.Match(line);
            if (headerMatch.Success)
            {
                currentScriptId = extract_script_id_from_string_dump_path(headerMatch.Groups["section"].Value, isBattle, isEvent);
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentScriptId))
            {
                continue;
            }

            var stringMatch = StringDumpLineRegex.Match(line);
            if (!stringMatch.Success)
            {
                continue;
            }

            if (!int.TryParse(stringMatch.Groups["idx"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var index) || index < 0)
            {
                continue;
            }

            var text = normalize_field_string_line(stringMatch.Groups["text"].Value);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            if (!destination.TryGetValue(currentScriptId, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                destination[currentScriptId] = map;
            }

            var key = index.ToString(CultureInfo.InvariantCulture);
            if (!map.ContainsKey(key))
            {
                map[key] = text;
            }
        }
    }

    static string extract_script_id_from_string_dump_path(string section, bool isBattle, bool isEvent)
    {
        var normalized = (section ?? string.Empty).Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        if (isBattle)
        {
            var match = BattleStringPathRegex.Match(normalized);
            if (match.Success)
            {
                return normalize_script_id_for_mapping(match.Groups["id"].Value);
            }
        }

        if (isEvent)
        {
            var match = EventStringPathRegex.Match(normalized);
            if (match.Success)
            {
                return normalize_script_id_for_mapping(match.Groups["id"].Value);
            }
        }

        return string.Empty;
    }

    static string normalize_field_string_line(string text)
    {
        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        const string simplifiedMarker = " (Simplified: ";
        var markerIndex = value.IndexOf(simplifiedMarker, StringComparison.Ordinal);
        if (markerIndex > 0 && value.EndsWith(')'))
        {
            return value[..markerIndex].Trim();
        }

        return value;
    }

    bool try_load_localized_command_domains(
        string parserOutDir,
        string locale,
        out Dictionary<string, LocalizedCommandEntry> commands,
        out Dictionary<string, LocalizedCommandEntry> autoAbilities,
        out Dictionary<string, LocalizedCommandEntry> keyItems,
        out List<string> sourceFiles)
    {
        commands = new Dictionary<string, LocalizedCommandEntry>(StringComparer.OrdinalIgnoreCase);
        autoAbilities = new Dictionary<string, LocalizedCommandEntry>(StringComparer.OrdinalIgnoreCase);
        keyItems = new Dictionary<string, LocalizedCommandEntry>(StringComparer.OrdinalIgnoreCase);
        sourceFiles = [];

        var outputBaseName = BuildInvocationOutputBaseName("READ_ALL_COMMANDS_LOCALIZED", [NormalizeMappingLocale(locale)]);
        var outputPath = Path.Combine(parserOutDir, $"{outputBaseName}.txt");
        ensure_localized_command_dump(parserOutDir, locale, outputPath);
        if (!File.Exists(outputPath))
        {
            return false;
        }

        var allEntries = ParseCommandDomainFromDump(outputPath, forcedKind: null);
        if (allEntries.Count == 0)
        {
            return false;
        }

        foreach (var pair in allEntries)
        {
            if (!ushort.TryParse(pair.Key, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            if (id >= 0x8000 && id <= 0x8FFF)
            {
                autoAbilities[pair.Key] = pair.Value;
            }
            else if (id >= 0xA000 && id <= 0xAFFF)
            {
                keyItems[pair.Key] = pair.Value;
            }
            else
            {
                commands[pair.Key] = pair.Value;
            }
        }

        sourceFiles.Add(Path.GetFileName(outputPath));
        return commands.Count + autoAbilities.Count + keyItems.Count > 0;
    }

    void ensure_localized_command_dump(
        string parserOutDir,
        string locale,
        string outputPath,
        bool forceRebuildHelper = false)
    {
        if (File.Exists(outputPath))
        {
            return;
        }

        var normalizedLocale = NormalizeMappingLocale(locale);
        Log.Information($"Localized command dump missing for locale={normalizedLocale}. Generating {Path.GetFileName(outputPath)}.");
        run_localized_command_dump_helper(parserOutDir, normalizedLocale, outputPath, forceRebuildHelper);
    }

    void run_localized_command_dump_helper(
        string parserOutDir,
        string locale,
        string outputPath,
        bool forceRebuildHelper)
    {
        var parserDir = ResolvePath(ParserDir);
        var jarPath = ResolveDataParserJarPath(parserDir);
        var inputRoot = ResolveDataParserInputRoot(failIfMissing: !DryRun);

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] Would generate localized command dump: locale={locale}, output={outputPath}");
            return;
        }

        var helperDir = Path.Combine(parserOutDir, "_localized-command-helper");
        EnsureDir(helperDir);
        var sourcePath = Path.Combine(helperDir, "LocalizedCommandDump.java");
        var classPath = Path.Combine(helperDir, "LocalizedCommandDump.class");
        write_localized_command_dump_helper_source(sourcePath);

        var shouldCompileHelper = forceRebuildHelper
            || !File.Exists(classPath)
            || File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(classPath);
        if (shouldCompileHelper)
        {
            RunChecked(
                "javac",
                $"-cp {Quote(jarPath)} -d {Quote(helperDir)} {Quote(sourcePath)}",
                "Compile localized command dump helper",
                showSpinner: true,
                silent: true);
        }

        var runtimeClassPath = string.Join(Path.PathSeparator, [jarPath, helperDir]);
        var result = RunProcess(
            fileName: "java",
            args: $"-cp {Quote(runtimeClassPath)} LocalizedCommandDump {Quote(EnsureTrailingDirectorySeparator(inputRoot))} {Quote(locale)}",
            description: $"Run localized command dump helper ({locale})",
            workingDirectory: parserOutDir,
            showSpinner: true,
            silent: true);
        if (result.ExitCode != 0)
        {
            Fail(
                $"Localized command dump helper failed with code {result.ExitCode}.{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{result.StdOut}");
        }

        var normalizedOutput = result.StdOut.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        File.WriteAllText(outputPath, normalizedOutput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Log.Information($"Localized command dump captured: {outputPath}");
    }

    static void write_localized_command_dump_helper_source(string sourcePath)
    {
        const string source = """
import main.DataAccess;
import main.DataReadingManager;
import model.AutoAbilityDataObject;
import model.CommandDataObject;
import model.KeyItemDataObject;
import reading.FileAccessorWithMods;

import java.io.File;

public final class LocalizedCommandDump {
    private LocalizedCommandDump() {}

    public static void main(String[] args) {
        if (args.length < 2) {
            System.err.println("Usage: LocalizedCommandDump <gameRoot> <locale>");
            System.exit(2);
            return;
        }

        String gameRoot = ensureTrailingSeparator(args[0]);
        String locale = args[1];
        FileAccessorWithMods.GAME_FILES_ROOT = gameRoot;

        DataReadingManager.initializeInternals();
        DataReadingManager.prepareCommands();
        DataAccess.AUTO_ABILITIES = DataReadingManager.readGearAbilities("battle/kernel/a_ability.bin", DataReadingManager.PATH_ORIGINALS_KERNEL + "arms_rate.bin", false);
        DataAccess.KEY_ITEMS = DataReadingManager.readKeyItems("battle/kernel/important.bin", false);

        System.out.println("--- command.bin ---");
        for (int i = 0x3000; i < 0x3200; i++) {
            CommandDataObject move = DataAccess.getCommand(i);
            if (move == null) continue;
            int offset = 0x14 + (i - 0x3000) * CommandDataObject.PCCOM_LENGTH;
            printEntry(i, offset, move.getName(locale), move.description.getLocalizedString(locale));
        }

        System.out.println("--- monmagic1.bin ---");
        for (int i = 0x4000; i < 0x4200; i++) {
            CommandDataObject move = DataAccess.getCommand(i);
            if (move == null) continue;
            int offset = 0x14 + (i - 0x4000) * CommandDataObject.COM_LENGTH;
            printEntry(i, offset, move.getName(locale), move.description.getLocalizedString(locale));
        }

        System.out.println("--- monmagic2.bin ---");
        for (int i = 0x6000; i < 0x6200; i++) {
            CommandDataObject move = DataAccess.getCommand(i);
            if (move == null) continue;
            int offset = 0x14 + (i - 0x6000) * CommandDataObject.COM_LENGTH;
            printEntry(i, offset, move.getName(locale), move.description.getLocalizedString(locale));
        }

        System.out.println("--- item.bin ---");
        for (int i = 0x2000; i < 0x2200; i++) {
            CommandDataObject move = DataAccess.getCommand(i);
            if (move == null) continue;
            int offset = 0x14 + (i - 0x2000) * CommandDataObject.PCCOM_LENGTH;
            printEntry(i, offset, move.getName(locale), move.description.getLocalizedString(locale));
        }

        System.out.println("--- a_ability.bin ---");
        if (DataAccess.AUTO_ABILITIES != null) {
            for (int i = 0; i < DataAccess.AUTO_ABILITIES.length; i++) {
                AutoAbilityDataObject ability = DataAccess.AUTO_ABILITIES[i];
                if (ability == null) continue;
                printEntry(0x8000 + i, -1, ability.getName(locale), ability.description.getLocalizedString(locale));
            }
        }

        System.out.println("--- important.bin ---");
        if (DataAccess.KEY_ITEMS != null) {
            for (int i = 0; i < DataAccess.KEY_ITEMS.length; i++) {
                KeyItemDataObject keyItem = DataAccess.KEY_ITEMS[i];
                if (keyItem == null) continue;
                printEntry(0xA000 + i, -1, keyItem.getName(locale), keyItem.description.getLocalizedString(locale));
            }
        }
    }

    private static void printEntry(int id, int offset, String name, String description) {
        String safeName = sanitize(name);
        String safeDescription = sanitize(description);
        if (offset >= 0) {
            System.out.printf("%04X (Offset %04X) - %s {} %s%n", id, offset, safeName, safeDescription);
        } else {
            System.out.printf("%04X - %s {} %s%n", id, safeName, safeDescription);
        }
    }

    private static String sanitize(String input) {
        if (input == null) return "";
        return input
            .replace("\r\n", "{\\n}")
            .replace("\n", "{\\n}")
            .replace("\r", "{\\n}")
            .trim();
    }

    private static String ensureTrailingSeparator(String root) {
        if (root == null || root.isBlank()) {
            return "";
        }
        if (root.endsWith("/") || root.endsWith("\\")) {
            return root;
        }
        return root + File.separator;
    }
}
""";

        File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    static string normalize_script_id_for_mapping(string scriptId) => (scriptId ?? string.Empty).Trim().ToLowerInvariant();

    static Dictionary<string, Dictionary<string, string>> clone_script_domain(
        Dictionary<string, Dictionary<string, string>> source)
    {
        var clone = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            clone[pair.Key] = new Dictionary<string, string>(pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        return clone;
    }

    static void merge_script_domain(
        Dictionary<string, Dictionary<string, string>> destination,
        Dictionary<string, Dictionary<string, string>> source,
        bool overwriteExisting)
    {
        foreach (var script in source)
        {
            if (!destination.TryGetValue(script.Key, out var scriptMap))
            {
                scriptMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                destination[script.Key] = scriptMap;
            }

            foreach (var line in script.Value)
            {
                if (!scriptMap.ContainsKey(line.Key))
                {
                    scriptMap[line.Key] = line.Value;
                }
                else if (overwriteExisting && !string.IsNullOrWhiteSpace(line.Value))
                {
                    scriptMap[line.Key] = line.Value;
                }
            }
        }
    }

    void WriteLocalizedDomainFile<TEntry>(string path, string domain, string locale, Dictionary<string, TEntry> entries, List<string> sourceFiles)
    {
        var file = new LocalizedDomainFile<TEntry>
        {
            SchemaVersion = 1,
            Domain = domain,
            Locale = locale,
            GeneratedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            SourceFiles = sourceFiles ?? [],
            Entries = entries ?? new Dictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase)
        };

        WriteJsonFile(path, file);
    }

    Dictionary<string, TEntry> ReadLocalizedDomainEntries<TEntry>(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var json = File.ReadAllText(path);
            var parsed = JsonSerializer.Deserialize<LocalizedDomainFile<TEntry>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (parsed?.Entries == null)
            {
                return new Dictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
            }

            return new Dictionary<string, TEntry>(parsed.Entries, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, TEntry>(StringComparer.OrdinalIgnoreCase);
        }
    }

    static void WriteJsonFile<T>(string path, T value)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };
        var json = JsonSerializer.Serialize(value, options);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    void RunParserInvocationsCore(IReadOnlyList<ParserInvocation> invocations, bool failIfMissingDataRoot)
    {
        if (invocations == null || invocations.Count == 0)
        {
            return;
        }

        var parserDir = ResolvePath(ParserDir);
        if (!Directory.Exists(parserDir))
        {
            SetupDataParserCore();
        }

        var inputRoot = ResolveDataParserInputRoot(failIfMissing: failIfMissingDataRoot);
        var parserInputRoot = EnsureTrailingDirectorySeparator(inputRoot);
        var outputDir = ResolvePath(DataOut);
        EnsureDir(outputDir);

        if (DryRun)
        {
            if (string.IsNullOrWhiteSpace(inputRoot))
            {
                Log.Warning("[DRY-RUN] No extracted data root detected yet. Pass --dataroot or extract FFX_Data.vbf first.");
                parserInputRoot = "<path-containing-ffx_ps2>/";
            }

            for (var i = 0; i < invocations.Count; i++)
            {
                var invocation = invocations[i];
                var modePart = Quote(invocation.Mode);
                var argsPart = invocation.Args.Count > 0
                    ? " " + string.Join(" ", invocation.Args.Select(Quote))
                    : string.Empty;
                var outputFile = Path.Combine(outputDir, $"{invocation.OutputBaseName}.txt");
                Log.Information($"[DRY-RUN] java -jar <resolved-jar> {Quote(parserInputRoot)} {modePart}{argsPart}");
                Log.Information($"[DRY-RUN] output file: {outputFile}");
            }
            return;
        }

        var jarPath = ResolveDataParserJarPath(parserDir);
        for (var i = 0; i < invocations.Count; i++)
        {
            RunDataParserInvocation(jarPath, parserInputRoot, outputDir, invocations[i]);
        }
    }

    void RunDataParserInvocation(string jarPath, string parserInputRoot, string outputDir, ParserInvocation invocation)
    {
        var outputFile = Path.Combine(outputDir, $"{invocation.OutputBaseName}.txt");
        var argsBuilder = new StringBuilder();
        argsBuilder.Append("-jar ").Append(Quote(jarPath)).Append(' ');
        argsBuilder.Append(Quote(parserInputRoot)).Append(' ');
        argsBuilder.Append(Quote(invocation.Mode));
        for (var i = 0; i < invocation.Args.Count; i++)
        {
            argsBuilder.Append(' ').Append(Quote(invocation.Args[i]));
        }

        var runArgs = argsBuilder.ToString();
        var result = RunProcess(
            fileName: "java",
            args: runArgs,
            description: $"Run FFXDataParser ({invocation.OutputBaseName})",
            workingDirectory: outputDir,
            showSpinner: true,
            silent: true);
        if (result.ExitCode != 0)
        {
            Fail(
                $"FFXDataParser failed with code {result.ExitCode}.{Environment.NewLine}" +
                $"Command: java {runArgs}{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{result.StdOut}");
        }

        var normalizedOutput = result.StdOut.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        File.WriteAllText(outputFile, normalizedOutput, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        if (invocation.Mode.Equals("READ_ALL_COMMANDS", StringComparison.Ordinal))
        {
            var aliasPath = Path.Combine(outputDir, "READ_ALL_COMMANDS.txt");
            if (!outputFile.Equals(aliasPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(outputFile, aliasPath, overwrite: true);
            }
        }

        if (string.IsNullOrWhiteSpace(result.StdOut))
        {
            Log.Warning($"FFXDataParser produced no stdout for {invocation.OutputBaseName}. File created: {outputFile}");
        }
        else
        {
            Log.Information($"FFXDataParser output captured: {outputFile}");
        }
    }

    static ParserInvocation CreateParserInvocation(string mode, IReadOnlyList<string> args)
    {
        var safeMode = (mode ?? string.Empty).Trim().ToUpperInvariant();
        var safeArgs = args?.Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? Array.Empty<string>();
        var baseName = BuildInvocationOutputBaseName(safeMode, safeArgs);
        return new ParserInvocation(safeMode, safeArgs, baseName);
    }

    static string BuildInvocationOutputBaseName(string mode, IReadOnlyList<string> args)
    {
        static string sanitize(string value)
        {
            var raw = Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9._-]+", "-", RegexOptions.CultureInvariant);
            raw = raw.Trim('-');
            return string.IsNullOrWhiteSpace(raw) ? "arg" : raw;
        }

        if (args == null || args.Count == 0)
        {
            return sanitize(mode);
        }

        var suffix = string.Join("_", args.Select(sanitize));
        return $"{sanitize(mode)}__{suffix}";
    }

    static List<string> SplitCommandLineTokens(string raw)
    {
        var tokens = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return tokens;
        }

        var sb = new StringBuilder(raw.Length);
        var inQuotes = false;
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (sb.Length > 0)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }

                continue;
            }

            sb.Append(c);
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    static string ExtractMappingName(string payload)
    {
        var text = payload.Trim();
        var braceIndex = text.IndexOf('{');
        if (braceIndex >= 0)
        {
            text = text[..braceIndex].Trim();
        }

        if (text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal) && text.Length > 2)
        {
            text = text[1..^1].Trim();
        }

        return text;
    }

    static string ClassifyCommandKind(ushort id, string section)
    {
        var normalizedSection = (section ?? string.Empty).Trim().ToLowerInvariant();
        if (id >= 0xA000 && id <= 0xAFFF) return "KeyItem";
        if (id >= 0x8000 && id <= 0x8FFF) return "AutoAbility";
        if (normalizedSection.Contains("item.bin", StringComparison.Ordinal)) return "ItemCommand";
        if (normalizedSection.Contains("monmagic2.bin", StringComparison.Ordinal)) return "BossCommand";
        if (normalizedSection.Contains("monmagic1.bin", StringComparison.Ordinal)) return "MonsterCommand";
        if (normalizedSection.Contains("command.bin", StringComparison.Ordinal)) return "PlayerCommand";
        if (id >= 0x6000 && id <= 0x6FFF) return "BossCommand";
        if (id >= 0x4000 && id <= 0x4FFF) return "MonsterCommand";
        if (id >= 0x3000 && id <= 0x3FFF) return "PlayerCommand";
        if (id >= 0x2000 && id <= 0x2FFF) return "ItemCommand";
        return "Command";
    }

    void BuildDataParserJar(string parserDir)
    {
        var mvnw = Path.Combine(parserDir, "mvnw.cmd");
        if (File.Exists(mvnw))
        {
            if (DryRun)
            {
                Log.Information($"[DRY-RUN] {mvnw} -q -DskipTests package");
            }
            else
            {
                RunChecked("cmd", $"/c \"\"{mvnw}\" -q -DskipTests package\"", "Build FFXDataParser", workingDirectory: parserDir, showSpinner: true, silent: true);
            }

            return;
        }

        var mavenCommand = EnsureMavenInstalled();

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] {mavenCommand} -q -DskipTests package (cwd: {parserDir})");
            return;
        }

        RunChecked(mavenCommand, "-q -DskipTests package", "Build FFXDataParser", workingDirectory: parserDir, showSpinner: true, silent: true);
    }

    string ResolveDataParserJarPath(string parserDir)
    {
        var targetDir = Path.Combine(parserDir, "target");
        if (!Directory.Exists(targetDir))
        {
            Fail($"FFXDataParser target directory not found: {targetDir}. Run build.cmd datasetup first.");
        }

        var candidates = Directory.EnumerateFiles(targetDir, "*.jar", SearchOption.TopDirectoryOnly)
            .Where(path => !path.EndsWith("-sources.jar", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("-javadoc.jar", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => Path.GetFileName(path).Contains("jar-with-dependencies", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => new FileInfo(path).Length)
            .ThenByDescending(path => File.GetLastWriteTimeUtc(path))
            .ToList();

        if (candidates.Count == 0)
        {
            Fail($"No parser jar found in {targetDir}. Build may have failed.");
        }

        return candidates[0];
    }

    string ResolveDataParserInputRoot(bool failIfMissing)
    {
        var explicitSource = NormalizeExtractedDataRoot(DataRoot);
        if (!string.IsNullOrWhiteSpace(DataRoot) && string.IsNullOrWhiteSpace(explicitSource))
        {
            Fail($"Invalid --dataroot '{DataRoot}'. Expected a directory containing ffx_ps2.");
        }

        if (!string.IsNullOrWhiteSpace(explicitSource))
        {
            return explicitSource;
        }

        var gameDir = ResolveGameDirOrEmpty();
        if (IsValidGameDir(gameDir))
        {
            var gameCandidates = new[]
            {
                Path.Combine(gameDir, "data", "ffx_data"),
                Path.Combine(gameDir, "data"),
                Path.Combine(gameDir, "ffx_data"),
                Path.Combine(gameDir, "ffx_ps2")
            };

            foreach (var candidate in gameCandidates)
            {
                var normalized = NormalizeExtractedDataRoot(candidate);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }
        }

        var workspaceCandidates = new[]
        {
            ResolvePath(".workspace/data"),
            ResolvePath(".workspace/data/ffx_data"),
            ResolvePath(".workspace/data/ffx_data_raw"),
            ResolvePath(".workspace/ffx_data"),
            ResolvePath(".workspace/data/ffx_ps2")
        };
        foreach (var candidate in workspaceCandidates)
        {
            var normalized = NormalizeExtractedDataRoot(candidate);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }
        }

        if (failIfMissing)
        {
            Fail(
                "Could not locate extracted game data for FFXDataParser.\n" +
                "Expected a folder containing 'ffx_ps2'.\n" +
                "Use --dataroot <path> or place extracted data under .workspace/data.\n" +
                "If you only have FFX_Data.vbf, extract it first (for example with vbfextract).");
        }

        return string.Empty;
    }

    static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return normalized;
    }

    static string NormalizeExtractedDataRoot(string? path)
    {
        var normalized = NormalizePathOrEmpty(path);
        if (string.IsNullOrWhiteSpace(normalized) || !Directory.Exists(normalized))
        {
            return string.Empty;
        }

        if (Directory.Exists(Path.Combine(normalized, "ffx_ps2")))
        {
            return normalized;
        }

        var leaf = Path.GetFileName(normalized.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (leaf.Equals("ffx_ps2", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(normalized);
            if (parent != null && Directory.Exists(Path.Combine(parent.FullName, "ffx_ps2")))
            {
                return parent.FullName;
            }
        }

        return string.Empty;
    }

    string NormalizeDataParserMode(string mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToUpperInvariant();
        if (DataParserModes.Contains(normalized, StringComparer.Ordinal))
        {
            return normalized;
        }

        Fail(
            $"Invalid --datamode '{mode}'. Supported values:{Environment.NewLine}" +
            string.Join(Environment.NewLine, DataParserModes.Select(x => $"  - {x}")));
        return string.Empty;
    }

    string ResolveGameDirOrEmpty()
    {
        var fromArg = NormalizePathOrEmpty(GameDir);
        if (IsValidGameDir(fromArg))
        {
            return fromArg;
        }

        var cfg = LoadLocalConfig();
        var fromConfig = NormalizePathOrEmpty(cfg.GameDir);
        if (IsValidGameDir(fromConfig))
        {
            return fromConfig;
        }

        var detected = DetectGameDir();
        return IsValidGameDir(detected) ? detected : string.Empty;
    }

    void EnsureJavaInstalled()
    {
        const int requiredJavaMajor = 21;
        var javaCompatible = ResolveJavaExecutable(minMajor: requiredJavaMajor);
        if (!string.IsNullOrWhiteSpace(javaCompatible))
        {
            ActivateJavaExecutable(javaCompatible);
            Log.Information($"[OK] Java {requiredJavaMajor}+ runtime is ready: {javaCompatible}");
            return;
        }

        if (DryRun)
        {
            Log.Warning($"[DRY-RUN] Java {requiredJavaMajor}+ runtime not found. Would install package: Microsoft.OpenJDK.{requiredJavaMajor}");
            return;
        }

        EnsureWingetAvailable();
        PromptInstallOrFail(
            title: $"Java {requiredJavaMajor}+ runtime not found.",
            detail: $"FFXDataParser requires Java {requiredJavaMajor} or newer.",
            adminRequired: true);

        InstallWingetPackage($"Microsoft.OpenJDK.{requiredJavaMajor}", $"OpenJDK {requiredJavaMajor}", overrideArgs: null);

        javaCompatible = ResolveJavaExecutable(minMajor: requiredJavaMajor);
        if (string.IsNullOrWhiteSpace(javaCompatible))
        {
            Fail($"Java {requiredJavaMajor}+ install completed but no compatible java executable was detected.");
        }

        ActivateJavaExecutable(javaCompatible);
        Log.Information($"Java installation verified: {javaCompatible}");
    }

    string ResolveJavaExecutable(int minMajor)
    {
        var candidates = new List<string>();

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));
        }

        var whereJava = RunProcess("where", "java", "Locate Java", showSpinner: false, silent: true);
        if (whereJava.ExitCode == 0)
        {
            candidates.AddRange(
                whereJava.StdOut
                    .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(path => path.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)));
        }

        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium")
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories));
            }
            catch
            {
                // Best effort only.
            }
        }

        string best = string.Empty;
        int bestMajor = 0;
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;

            int major = GetJavaMajorVersion(candidate);
            if (major < minMajor) continue;
            if (major < bestMajor) continue;

            best = candidate;
            bestMajor = major;
        }

        return best;
    }

    int GetJavaMajorVersion(string javaExecutable)
    {
        var result = RunProcess(javaExecutable, "-version", "Probe Java version", showSpinner: false, silent: true);
        var output = (result.StdErr + Environment.NewLine + result.StdOut).Trim();
        if (string.IsNullOrWhiteSpace(output))
        {
            return 0;
        }

        var match = Regex.Match(output, "version\\s+\"(?<ver>[^\"]+)\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return 0;
        }

        var raw = match.Groups["ver"].Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return 0;
        }

        var tokens = raw.Split(new[] { '.', '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return 0;
        }

        if (tokens[0] == "1" && tokens.Length > 1 && int.TryParse(tokens[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var legacy))
        {
            return legacy;
        }

        if (int.TryParse(tokens[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return major;
        }

        return 0;
    }

    void ActivateJavaExecutable(string javaExecutable)
    {
        var javaBin = Path.GetDirectoryName(javaExecutable);
        if (string.IsNullOrWhiteSpace(javaBin))
        {
            return;
        }

        var javaHomeDir = Directory.GetParent(javaBin)?.FullName;
        if (!string.IsNullOrWhiteSpace(javaHomeDir))
        {
            Environment.SetEnvironmentVariable("JAVA_HOME", javaHomeDir);
        }

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var entries = currentPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!entries.Contains(javaBin, StringComparer.OrdinalIgnoreCase))
        {
            var updated = javaBin + Path.PathSeparator + currentPath;
            Environment.SetEnvironmentVariable("PATH", updated);
        }
    }

    string EnsureMavenInstalled()
    {
        if (CommandExists("mvn"))
        {
            Log.Information("[OK] Maven is already installed.");
            return "mvn";
        }

        var localMaven = TryFindLocalMavenExecutable();
        if (!string.IsNullOrWhiteSpace(localMaven))
        {
            Log.Information($"[OK] Using local Maven: {localMaven}");
            return localMaven;
        }

        if (DryRun)
        {
            Log.Warning("[DRY-RUN] Maven not found. Would bootstrap local Maven under .workspace/tools/maven.");
            return "mvn";
        }

        var bootstrapped = BootstrapLocalMaven();
        if (!string.IsNullOrWhiteSpace(bootstrapped))
        {
            Log.Information($"Maven bootstrap complete: {bootstrapped}");
            return bootstrapped;
        }

        EnsureWingetAvailable();
        PromptInstallOrFail(
            title: "Maven not found.",
            detail: "FFXDataParser build requires Maven.",
            adminRequired: true);

        InstallWingetPackage("Apache.Maven", "Apache Maven", overrideArgs: null);

        if (!CommandExists("mvn"))
        {
            Fail("Maven install completed but command not found on PATH yet. Open a new terminal and retry.");
        }

        Log.Information("Maven installation verified.");
        return "mvn";
    }

    string TryFindLocalMavenExecutable()
    {
        var root = ResolvePath(".workspace/tools/maven");
        if (!Directory.Exists(root))
        {
            return string.Empty;
        }

        var candidates = Directory.EnumerateFiles(root, "mvn.cmd", SearchOption.AllDirectories)
            .Where(path => path.Contains("apache-maven", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count == 0)
        {
            return string.Empty;
        }

        return candidates[0];
    }

    string BootstrapLocalMaven()
    {
        var root = ResolvePath(".workspace/tools/maven");
        EnsureDir(root);

        var version = ResolveLatestMavenVersion();
        var zipUrl = $"https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/{version}/apache-maven-{version}-bin.zip";
        var tempRoot = Path.Combine(Path.GetTempPath(), $"maven-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempRoot, $"apache-maven-{version}-bin.zip");
        EnsureDir(tempRoot);

        try
        {
            DownloadFile(zipUrl, zipPath);
            ZipFile.ExtractToDirectory(zipPath, root, overwriteFiles: true);
        }
        catch (Exception ex)
        {
            Log.Warning($"Local Maven bootstrap failed: {ex.Message}");
            return string.Empty;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        return TryFindLocalMavenExecutable();
    }

    static string ResolveLatestMavenVersion()
    {
        const string metadataUrl = "https://repo.maven.apache.org/maven2/org/apache/maven/apache-maven/maven-metadata.xml";
        using var client = new HttpClient();
        var xml = client.GetStringAsync(metadataUrl).GetAwaiter().GetResult();

        var releaseMatch = Regex.Match(xml, "<release>(?<v>[^<]+)</release>", RegexOptions.CultureInvariant);
        if (releaseMatch.Success)
        {
            return releaseMatch.Groups["v"].Value.Trim();
        }

        var latestMatch = Regex.Match(xml, "<latest>(?<v>[^<]+)</latest>", RegexOptions.CultureInvariant);
        if (latestMatch.Success)
        {
            return latestMatch.Groups["v"].Value.Trim();
        }

        Fail($"Unable to resolve latest Maven version from {metadataUrl}");
        return string.Empty;
    }

    void SetupVbfExtractorCore()
    {
        var toolDir = ResolvePath(VbfDir);
        EnsureDir(toolDir);

        var required = new[] { "vbfextract.exe", "FFX_Data.txt", "FFX2_Data.txt", "metamenu.txt" };
        var missing = required.Where(name => !File.Exists(Path.Combine(toolDir, name))).ToList();
        if (missing.Count == 0)
        {
            Log.Information($"VBFTool is ready: {toolDir}");
            return;
        }

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] Would download latest VBFTool release from {VbfApi}");
            Log.Information($"[DRY-RUN] Missing files: {string.Join(", ", missing)}");
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), $"vbftool-{Guid.NewGuid():N}");
        var zipPath = Path.Combine(tempRoot, "VBFTool.zip");
        var unpackDir = Path.Combine(tempRoot, "unzipped");
        EnsureDir(tempRoot);
        EnsureDir(unpackDir);

        try
        {
            var zipUrl = ResolveLatestVbfToolZipUrl(VbfApi);
            DownloadFile(zipUrl, zipPath);
            ZipFile.ExtractToDirectory(zipPath, unpackDir);
            CopyRequiredFilesFromExtract(unpackDir, toolDir, required);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }

        var stillMissing = required.Where(name => !File.Exists(Path.Combine(toolDir, name))).ToList();
        if (stillMissing.Count > 0)
        {
            Fail(
                "VBFTool setup incomplete. Missing required files: " +
                string.Join(", ", stillMissing) + Environment.NewLine +
                $"Tool directory: {toolDir}");
        }

        Log.Information($"VBFTool ready: {toolDir}");
    }

    void ExtractGameDataCore()
    {
        SetupVbfExtractorCore();

        var toolDir = ResolvePath(VbfDir);
        var gameDataDir = ResolveVbfGameDataDir();
        var outputRoot = ResolvePath(ExtractOut);
        EnsureDir(outputRoot);

        var extractor = ResolveVbfToolPath(toolDir, gameDataDir, "vbfextract.exe");
        var ffxList = ResolveVbfToolPath(toolDir, gameDataDir, "FFX_Data.txt");
        var ffx2List = ResolveVbfToolPath(toolDir, gameDataDir, "FFX2_Data.txt");
        var metaList = ResolveVbfToolPath(toolDir, gameDataDir, "metamenu.txt");

        var ffxVbf = Path.Combine(gameDataDir, "FFX_Data.vbf");
        var ffx2Vbf = Path.Combine(gameDataDir, "FFX2_Data.vbf");
        var metaVbf = Path.Combine(gameDataDir, "metamenu.vbf");

        EnsureFileExists(ffxVbf, "Missing game archive");
        EnsureFileExists(ffx2Vbf, "Missing game archive");

        RunVbfExtract(extractor, outputRoot, ffxList, ffxVbf, "Extract FFX_Data.vbf");
        RunVbfExtract(extractor, outputRoot, ffx2List, ffx2Vbf, "Extract FFX2_Data.vbf");

        if (ExtractMetaMenu)
        {
            if (File.Exists(metaVbf))
            {
                RunVbfExtract(extractor, outputRoot, metaList, metaVbf, "Extract metamenu.vbf");
            }
            else
            {
                Log.Warning($"metamenu.vbf not found in {gameDataDir}; skipping metamenu extraction.");
            }
        }

        Log.Information($"Game data extraction complete: {outputRoot}");
    }

    static void EnsureFileExists(string path, string message)
    {
        if (!File.Exists(path))
        {
            Fail($"{message}: {path}");
        }
    }

    void RunVbfExtract(string extractor, string outputRoot, string listFile, string vbfFile, string description)
    {
        EnsureFileExists(extractor, "vbfextract executable not found");
        EnsureFileExists(listFile, "VBF file list not found");
        EnsureFileExists(vbfFile, "VBF archive not found");

        if (DryRun)
        {
            Log.Information($"[DRY-RUN] {description}");
            Log.Information($"[DRY-RUN] {extractor} -o {outputRoot} -f {listFile} {vbfFile}");
            return;
        }

        RunChecked(extractor, $"-o {Quote(outputRoot)} -f {Quote(listFile)} {Quote(vbfFile)}", description, showSpinner: true, silent: true);
        Log.Information($"{description} finished.");
    }

    string ResolveVbfGameDataDir()
    {
        var explicitDir = NormalizePathOrEmpty(VbfGameDir);
        if (IsValidVbfDataDir(explicitDir))
        {
            return explicitDir;
        }

        var fromGameDirArg = NormalizePathOrEmpty(GameDir);
        if (IsValidGameDir(fromGameDirArg))
        {
            var data = Path.Combine(fromGameDirArg, "data");
            if (IsValidVbfDataDir(data))
            {
                return data;
            }
        }

        var cfg = LoadLocalConfig();
        var cfgGameDir = NormalizePathOrEmpty(cfg.GameDir);
        if (IsValidGameDir(cfgGameDir))
        {
            var data = Path.Combine(cfgGameDir, "data");
            if (IsValidVbfDataDir(data))
            {
                return data;
            }
        }

        var detectedGameDir = DetectGameDir();
        if (IsValidGameDir(detectedGameDir))
        {
            var data = Path.Combine(detectedGameDir, "data");
            if (IsValidVbfDataDir(data))
            {
                return data;
            }
        }

        Fail(
            "Could not locate game data directory with VBF files.\n" +
            "Pass --vbfgamedatadir <path-to-game-data-folder> or configure GAME_DIR.\n" +
            "Expected files: FFX_Data.vbf and FFX2_Data.vbf.");
        return string.Empty;
    }

    static bool IsValidVbfDataDir(string path) =>
        !string.IsNullOrWhiteSpace(path) &&
        File.Exists(Path.Combine(path, "FFX_Data.vbf")) &&
        File.Exists(Path.Combine(path, "FFX2_Data.vbf"));

    static string ResolveVbfToolPath(string toolDir, string gameDataDir, string fileName)
    {
        var fromTool = Path.Combine(toolDir, fileName);
        if (File.Exists(fromTool))
        {
            return fromTool;
        }

        var fromGame = Path.Combine(gameDataDir, fileName);
        if (File.Exists(fromGame))
        {
            return fromGame;
        }

        return fromTool;
    }

    static string ResolveLatestVbfToolZipUrl(string releaseApiUrl)
    {
        using var client = CreateGitHubHttpClient();
        var json = client.GetStringAsync(releaseApiUrl).GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            Fail($"Invalid GitHub API response: missing assets array. URL={releaseApiUrl}");
        }

        string? firstZip = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(url) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            firstZip ??= url;
            if (name.Contains("windows", StringComparison.OrdinalIgnoreCase) || name.Contains("win", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        if (!string.IsNullOrWhiteSpace(firstZip))
        {
            return firstZip;
        }

        Fail($"No .zip asset found in latest VBFTool release. URL={releaseApiUrl}");
        return string.Empty;
    }

    static HttpClient CreateGitHubHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("fahrenheit-parry-mod-build/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    static void DownloadFile(string url, string destinationPath)
    {
        using var client = CreateGitHubHttpClient();
        using var response = client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        using var stream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
        using var file = File.Create(destinationPath);
        stream.CopyTo(file);
    }

    static void CopyRequiredFilesFromExtract(string extractedRoot, string toolDir, IReadOnlyCollection<string> requiredFiles)
    {
        var pending = new HashSet<string>(requiredFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(extractedRoot, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!pending.Contains(name))
            {
                continue;
            }

            File.Copy(file, Path.Combine(toolDir, name), overwrite: true);
            pending.Remove(name);
            if (pending.Count == 0)
            {
                break;
            }
        }
    }

    void DataInventoryCore()
    {
        var root = ResolvePath(DataRootDir);
        if (!Directory.Exists(root))
        {
            Fail($"Data export root not found: {root}");
        }

        var folders = ResolveDataFolders(root);
        if (folders.Count == 0)
        {
            Fail($"No data folders found under {root}.");
        }

        foreach (var folder in folders)
        {
            var reportPath = Path.Combine(folder, "DATA_TREE.txt");
            if (DryRun)
            {
                Log.Information($"[DRY-RUN] Would generate inventory report: {reportPath}");
                continue;
            }

            WriteDataTreeReport(folder, reportPath);
            Log.Information($"Inventory report written: {reportPath}");
        }
    }

    void OffloadDataCore()
    {
        var root = ResolvePath(DataRootDir);
        if (!Directory.Exists(root))
        {
            Fail($"Data export root not found: {root}");
        }

        var nasDir = NormalizePathOrEmpty(NasDir);
        if (string.IsNullOrWhiteSpace(nasDir))
        {
            Fail("Missing --nasdir <path>.");
        }

        var mode = NormalizeOffloadMode(OffloadMode);
        var folders = ResolveDataFolders(root);
        if (folders.Count == 0)
        {
            Fail($"No data folders found under {root}.");
        }

        EnsureDir(nasDir);

        foreach (var source in folders)
        {
            var name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var destination = Path.Combine(nasDir, name);

            if (DryRun)
            {
                Log.Information($"[DRY-RUN] {mode.ToUpperInvariant()} {source} -> {destination}");
                continue;
            }

            EnsureDir(destination);
            RunRobocopy(source, destination, mode == "move");

            if (mode == "move")
            {
                TryCreateDataJunction(source, destination);
            }
        }
    }

    List<string> ResolveDataFolders(string root)
    {
        var requested = ParseListArgument(Folders);
        var includeAll = requested.Count == 0;
        var directories = Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (includeAll)
        {
            return directories;
        }

        var wanted = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        var selected = directories
            .Where(dir => wanted.Contains(Path.GetFileName(dir)))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = requested
            .Where(name => !selected.Any(dir => Path.GetFileName(dir).Equals(name, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (missing.Count > 0)
        {
            Log.Warning("Skipping unknown data folders: " + string.Join(", ", missing));
        }

        return selected;
    }

    static List<string> ParseListArgument(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string NormalizeOffloadMode(string mode)
    {
        var normalized = (mode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "move" => "move",
            "copy" => "copy",
            _ => FailWithReturn<string>($"Invalid --offloadmode '{mode}'. Use 'move' or 'copy'.")
        };
    }

    void RunRobocopy(string source, string destination, bool move)
    {
        var args = new StringBuilder();
        args.Append(Quote(source));
        args.Append(' ');
        args.Append(Quote(destination));
        args.Append(" /E /R:1 /W:1 /COPY:DAT /DCOPY:DAT /XJ /FFT /NP /NFL /NDL /NJH /NJS");
        if (move)
        {
            args.Append(" /MOVE");
        }

        var result = RunProcess("robocopy", args.ToString(), move ? "Move data to NAS" : "Copy data to NAS", showSpinner: true, silent: true);
        if (result.ExitCode > 7)
        {
            Fail(
                $"robocopy failed with code {result.ExitCode}.{Environment.NewLine}" +
                $"Source: {source}{Environment.NewLine}" +
                $"Destination: {destination}{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{result.StdOut}");
        }

        Log.Information($"{(move ? "Moved" : "Copied")} data: {source} -> {destination}");
    }

    void TryCreateDataJunction(string source, string destination)
    {
        if (!KeepDataJunction)
        {
            return;
        }

        if (destination.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning($"Skipping junction for UNC destination (not supported by /J): {destination}");
            return;
        }

        if (Directory.Exists(source))
        {
            try
            {
                Directory.Delete(source, recursive: true);
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not remove source directory before creating junction: {source}. {ex.Message}");
                return;
            }
        }

        var result = RunProcess("cmd", $"/c mklink /J {Quote(source)} {Quote(destination)}", "Create data junction", showSpinner: false, silent: true);
        if (result.ExitCode != 0)
        {
            Log.Warning(
                $"Failed to create junction {source} -> {destination}.{Environment.NewLine}" +
                $"STDERR:{Environment.NewLine}{result.StdErr}{Environment.NewLine}" +
                $"STDOUT:{Environment.NewLine}{result.StdOut}");
            return;
        }

        Log.Information($"Junction created: {source} -> {destination}");
    }

    static void WriteDataTreeReport(string root, string outputPath)
    {
        var lines = new List<string>
        {
            $"# Data Tree Report",
            $"Root: {root}",
            $"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"",
            $"Legend:",
            $"- Dirs: immediate subdirectory count at this path",
            $"- Files: immediate file count at this path",
            $"- Size: immediate file size at this path (MiB)",
            $"- Types: immediate file extensions and counts",
            $"",
            $". {DescribePathHint(".")}"
        };

        var allDirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dir in allDirs)
        {
            var relative = Path.GetRelativePath(root, dir).Replace('\\', '/');
            var depth = relative.Count(ch => ch == '/') + 1;
            var indent = new string(' ', depth * 2);
            var directSubDirCount = Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly).Count();

            var directFiles = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly).ToList();
            var directFileCount = directFiles.Count;
            var directBytes = directFiles.Sum(file => new FileInfo(file).Length);
            var typeSummary = SummarizeFileTypes(directFiles);
            var hint = DescribePathHint(relative);

            lines.Add($"{indent}- {relative}  [Dirs={directSubDirCount}, Files={directFileCount}, SizeMiB={directBytes / 1024d / 1024d:0.##}]");
            if (!string.IsNullOrWhiteSpace(hint))
            {
                lines.Add($"{indent}  Note: {hint}");
            }

            if (!string.IsNullOrWhiteSpace(typeSummary))
            {
                lines.Add($"{indent}  Types: {typeSummary}");
            }
        }

        File.WriteAllLines(outputPath, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    static string SummarizeFileTypes(IEnumerable<string> files)
    {
        var groups = files
            .GroupBy(path =>
            {
                var ext = Path.GetExtension(path);
                return string.IsNullOrWhiteSpace(ext) ? "<noext>" : ext.ToLowerInvariant();
            })
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key} x{g.Count()}");

        return string.Join(", ", groups);
    }

    static string DescribePathHint(string relativePath)
    {
        var p = relativePath.ToLowerInvariant();

        if (p == ".") return "Top-level folder for this extracted dataset.";
        if (p.Contains("/battle") || p.Contains("/btl")) return "Battle scripts, encounters, AI data, and combat tables.";
        if (p.Contains("/event")) return "Field/event scripts and narrative scene data.";
        if (p.Contains("/mon") || p.Contains("/monster")) return "Monster definitions, assets, and behavior data.";
        if (p.Contains("/kernel")) return "Core gameplay tables (commands, items, stats, CTB, shops).";
        if (p.Contains("/menu")) return "UI/menu strings, resources, and related metadata.";
        if (p.Contains("/map")) return "Map/zone resources and scene-level content.";
        if (p.Contains("/chr")) return "Character model, animation, and character-related resources.";
        if (p.Contains("/magic")) return "Spell/effect resources and related data blocks.";
        if (p.Contains("/sound")) return "Audio banks, voice, and sound stream resources.";
        if (p.Contains("/video")) return "Pre-rendered movie/video assets.";
        if (p.Contains("/help")) return "Localized help/tutorial text and associated assets.";
        if (p.Contains("/yonishi_data")) return "Shared engine/gameplay support datasets.";
        if (p.Contains("/version_config")) return "Region/version marker files.";
        return "Mixed resource/data content.";
    }
}

