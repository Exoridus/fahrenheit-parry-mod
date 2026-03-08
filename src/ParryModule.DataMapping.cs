namespace Fahrenheit.Mods.Parry;

public unsafe sealed partial class ParryModule
{
    private static readonly string[] _mappingJsonNames = [
        "ffx-mappings.json",
        "ffx-mappings.us.json",
        "ffx-mappings.de.json",
        "ffx-mappings.fr.json",
        "ffx-mappings.it.json",
        "ffx-mappings.sp.json",
        "ffx-mappings.jp.json",
        "ffx-mappings.ch.json",
        "ffx-mappings.kr.json"
    ];

    private void initialize_data_mappings(FhModContext modContext)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FHPARRY_DATA_MAP_DIR")))
        {
            log_debug("FHPARRY_DATA_MAP_DIR is deprecated; use FH_PARRY_DATA_MAP_DIR instead.");
        }

        string preferredLocale = CultureInfo.CurrentUICulture.Name;
        List<string> candidates = build_mapping_directory_candidates(modContext);
        _dataMappings.LoadFromDirectories(
            candidates,
            preferredLocale,
            infoLog: msg => _logger.Info($"[Parry][DataMap] {msg}"),
            warnLog: msg => _logger.Warning($"[Parry][DataMap] {msg}"));

        if (_dataMappings.HasAny)
        {
            _dataMappingStatus =
                $"Loaded mappings ({_dataMappings.SourceSummary}) | " +
                $"cmd={_dataMappings.CommandCount}, auto={_dataMappings.AutoAbilityCount}, key={_dataMappings.KeyItemCount}, " +
                $"monster={_dataMappings.MonsterCount}, battle={_dataMappings.BattleCount}, event={_dataMappings.EventCount}";
            _logger.Info($"[Parry][DataMap] {_dataMappingStatus}");
        }
        else
        {
            _dataMappingStatus = "No mappings loaded. Place locale bundles (ffx-mappings.<locale>.json) in mappings/runtime or set FH_PARRY_DATA_MAP_DIR.";
            _logger.Info($"[Parry][DataMap] {_dataMappingStatus}");
        }
    }

    private static List<string> build_mapping_directory_candidates(FhModContext modContext)
    {
        var dirs = new List<string>();

        void add_if_not_blank(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            dirs.Add(value);
        }

        add_if_not_blank(Environment.GetEnvironmentVariable("FH_PARRY_DATA_MAP_DIR"));
        add_if_not_blank(Environment.GetEnvironmentVariable("FHPARRY_DATA_MAP_DIR"));

        add_if_not_blank(Path.Combine(modContext.Paths.ModDir.FullName, "mappings", "runtime"));
        add_if_not_blank(Path.Combine(modContext.Paths.ResourcesDir.FullName, "mappings", "runtime"));
        add_if_not_blank(modContext.Paths.ResourcesDir.FullName);

        string cwd = Environment.CurrentDirectory;
#if DEBUG
        // Local dev fallbacks — .workspace is research infrastructure, not available in release builds.
        add_if_not_blank(Path.Combine(cwd, ".workspace", "data", "ffx-dataparser"));
        add_if_not_blank(Path.Combine(cwd, ".workspace", "data", "ffx_parser"));
        add_if_not_blank(Path.Combine(cwd, ".workspace", "data", "exports"));
        add_if_not_blank(Path.Combine(cwd, ".workspace", "data"));
#endif
        add_if_not_blank(Path.Combine(cwd, "mappings", "runtime"));

        foreach (string discovered in discover_mapping_dirs(cwd))
        {
            add_if_not_blank(discovered);
        }

        // Distinct while preserving order.
        return dirs
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> discover_mapping_dirs(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) yield break;

        string[] roots = [
#if DEBUG
            // Local dev roots — .workspace is research infrastructure, not available in release builds.
            Path.Combine(cwd, ".workspace", "data"),
            Path.Combine(cwd, ".workspace", "exports"),
#endif
            Path.Combine(cwd, "resources"),
            Path.Combine(cwd, "mappings")
        ];

        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < roots.Length; i++)
        {
            string root = roots[i];
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories);
            }
            catch (Exception)
            {
                // Silently skip roots that cannot be enumerated (missing mounts, permission errors, etc.).
                continue;
            }

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (!_mappingJsonNames.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

                string? dir = Path.GetDirectoryName(file);
                if (string.IsNullOrWhiteSpace(dir)) continue;
                if (!emitted.Add(dir)) continue;
                yield return dir;
            }
        }
    }

    private bool try_get_last_command_label(out string label, out string kind, out ushort commandId)
    {
        label = string.Empty;
        kind = string.Empty;
        commandId = 0;

        Btl* battle = _battleAdapter.GetBattle();
        if (battle == null) return false;

        commandId = (ushort)(battle->last_com & 0xFFFFu);
        if (commandId == 0) return false;

        return _dataMappings.TryResolveCommandDisplay(commandId, out label, out kind);
    }

    private string format_last_command_summary()
    {
        Btl* battle = _battleAdapter.GetBattle();
        if (battle == null) return "None";

        ushort commandId = (ushort)(battle->last_com & 0xFFFFu);
        if (commandId == 0) return "None";

        if (try_get_last_command_label(out string label, out string kind, out _))
        {
            return $"0x{commandId:X4} {kind}: {truncate_display(label, 40)}";
        }

        return $"0x{commandId:X4}";
    }

    private string format_current_battle_summary()
    {
        if (!try_get_current_battle_id(out string battleId)) return "None";
        if (_dataMappings.TryResolveBattleLabel(battleId, out string battleLabel))
        {
            return $"{battleId} - {truncate_display(battleLabel, 40)}";
        }

        return battleId;
    }

    private bool try_get_current_battle_id(out string battleId)
    {
        battleId = string.Empty;
        Btl* battle = _battleAdapter.GetBattle();
        if (battle == null) return false;

        string field = decode_field_name(battle);
        if (string.IsNullOrWhiteSpace(field)) return false;

        // Most battle ids follow <field><field_idx>_<group_idx>.
        string primary = $"{field}{battle->field_idx:D2}_{battle->group_idx:D2}".ToLowerInvariant();
        if (_dataMappings.TryResolveBattleLabel(primary, out _))
        {
            battleId = primary;
            return true;
        }

        string altFormation = $"{field}{battle->field_idx:D2}_{battle->formation_idx:D2}".ToLowerInvariant();
        if (_dataMappings.TryResolveBattleLabel(altFormation, out _))
        {
            battleId = altFormation;
            return true;
        }

        battleId = primary;
        return true;
    }

    private static string decode_field_name(Btl* battle)
    {
        if (battle == null) return string.Empty;

        var sb = new StringBuilder(14);
        for (int i = 0; i < 14; i++)
        {
            byte b = battle->field_name[i];
            if (b == 0) break;
            if (b >= 32 && b <= 126)
            {
                sb.Append((char)b);
            }
        }

        return sb.ToString().Trim();
    }

    private bool try_map_enemy_chr_id_to_name(int chrId, out string name)
    {
        return _dataMappings.TryResolveMonsterName(chrId, out name);
    }

    private static string truncate_display(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value;
        }

        if (maxLength <= 3) return value[..maxLength];
        return value[..(maxLength - 3)] + "...";
    }

    private ResolvedCommandInfo resolve_command_for_cue(Btl* battle, byte queueIndex, in AttackCue cue)
    {
        ushort cueCommandId = 0;
        if (cue.command_count > 0)
        {
            cueCommandId = read_attack_command_id_raw(cue.command_list[0]);
        }

        bool cueLooksValid = is_plausible_command_id(cueCommandId);

        ushort offsetCandidate = read_attack_command_id_candidate_from_btl_offset(battle, queueIndex);
        bool offsetLooksValid = is_plausible_command_id(offsetCandidate);

        ushort resolvedId = 0;
        CommandIdSource source = CommandIdSource.None;
        CommandIdConfidence confidence = CommandIdConfidence.None;

        if (cueLooksValid && offsetLooksValid && cueCommandId == offsetCandidate)
        {
            resolvedId = cueCommandId;
            source = CommandIdSource.CueCommandInfo;
            confidence = CommandIdConfidence.High;
        }
        else if (cueLooksValid)
        {
            resolvedId = cueCommandId;
            source = CommandIdSource.CueCommandInfo;
            confidence = offsetLooksValid ? CommandIdConfidence.Medium : CommandIdConfidence.High;
        }
        else if (offsetLooksValid)
        {
            resolvedId = offsetCandidate;
            source = CommandIdSource.CueOffsetCandidate;
            confidence = CommandIdConfidence.Medium;
        }
        else if (battle != null && queueIndex == 0)
        {
            ushort lastCom = (ushort)(battle->last_com & 0xFFFFu);
            if (is_plausible_command_id(lastCom))
            {
                resolvedId = lastCom;
                source = CommandIdSource.LastComFallback;
                confidence = CommandIdConfidence.Low;
            }
        }

        return create_resolved_command_info(resolvedId, source, confidence);
    }

    private ResolvedCommandInfo create_resolved_command_info(ushort commandId, CommandIdSource source, CommandIdConfidence confidence)
    {
        if (commandId == 0) return ResolvedCommandInfo.None;

        _dataMappings.TryResolveCommandDamageType(commandId, out string damageType);

        if (_dataMappings.TryResolveCommandDisplay(commandId, out string label, out string kind))
        {
            return new ResolvedCommandInfo(commandId, label, kind, damageType, source, confidence);
        }

        return new ResolvedCommandInfo(commandId, string.Empty, string.Empty, damageType, source, confidence);
    }

    private static string format_command_source(CommandIdSource source)
    {
        return source switch
        {
            CommandIdSource.CueCommandInfo => "cue",
            CommandIdSource.CueOffsetCandidate => "cue@0x3A8",
            CommandIdSource.LastComFallback => "last_com",
            _ => "none"
        };
    }

    private static string format_command_confidence(CommandIdConfidence confidence)
    {
        return confidence switch
        {
            CommandIdConfidence.High => "high",
            CommandIdConfidence.Medium => "med",
            CommandIdConfidence.Low => "low",
            _ => "none"
        };
    }

    private static TurnTimelineCommandConfidence to_timeline_confidence(CommandIdConfidence confidence)
    {
        return confidence switch
        {
            CommandIdConfidence.High => TurnTimelineCommandConfidence.High,
            CommandIdConfidence.Medium => TurnTimelineCommandConfidence.Medium,
            CommandIdConfidence.Low => TurnTimelineCommandConfidence.Low,
            _ => TurnTimelineCommandConfidence.None
        };
    }

    private static string format_command_hint(in ResolvedCommandInfo command, int maxLabelLength)
    {
        if (!command.HasCommandId) return string.Empty;

        string source = format_command_source(command.Source);
        string conf = format_command_confidence(command.Confidence);
        if (command.HasLabel)
        {
            return $" [0x{command.CommandId:X4} {truncate_display(command.Label, maxLabelLength)} | {source}/{conf}]";
        }

        return $" [0x{command.CommandId:X4} | {source}/{conf}]";
    }

    private static bool is_plausible_command_id(ushort commandId)
    {
        // Typical runtime command ranges observed in FFX:
        // - 0x2000 item commands
        // - 0x3000 player commands
        // - 0x4000 monster commands
        // - 0x6000 boss/monmagic2 commands
        return commandId >= 0x2000 && commandId <= 0x6FFF;
    }

    private static ushort read_attack_command_id_candidate_from_btl_offset(Btl* battle, byte queueIndex)
    {
        if (battle == null) return 0;

        const int cueBaseOffset = 0x3A8;   // btl.attack_cues[0] + first command payload
        const int cueStride = 0x48;        // sizeof(AttackCue)
        byte* ptr = (byte*)battle + cueBaseOffset + (queueIndex * cueStride);
        return *(ushort*)ptr;
    }

    private static ushort read_attack_command_id_raw(AttackCommandInfo info)
    {
        AttackCommandInfo local = info;
        return *(ushort*)&local;
    }
}
