using System;
using System.Collections.Generic;

namespace FisherLogger
{
    /// <summary>
    /// NEXT-107: rules for attaching a Player.log species (model file "FishInGame/&lt;model&gt;") to a catch record.
    ///  - the model is resolved ONLY through fish_full_database.json model_id; exactly one fish -> name + fish_lang_id,
    ///    zero or several fish -> nothing is set (no name without id, no guessed id, no numeric-prefix parsing);
    ///  - a species already present from the network (or any non-Player.log source) is never overwritten;
    ///  - a species set from Player.log is marked autodetect_source = "playerlog_asset" so it can be withdrawn
    ///    (ambiguity) or replaced by network data.
    /// </summary>
    public static class PlayerLogSpecies
    {
        public const string Source = "playerlog_asset";

        public static bool IsNameMissing(string? name)
        {
            string t = name?.Trim() ?? "";
            return t.Length == 0 || t == "???" || t == "?" || t.StartsWith("Fish_", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True if the record carries a species that did not come from Player.log.</summary>
        public static bool HasOtherSpecies(CatchData c)
            => c != null && c.autodetect_source != Source && (c.fish_lang_id.HasValue || !IsNameMissing(c.fish_name));

        /// <summary>
        /// Applies the model to the record. Result: applied | kept_existing | no_model | no_mapping | ambiguous_mapping.
        /// <paramref name="resolve"/> returns all fish whose model_id equals the model.
        /// </summary>
        public static string Apply(CatchData c, string? model, Func<string, IReadOnlyList<PacketDeepParser.FishInfo>> resolve)
        {
            if (c == null || string.IsNullOrWhiteSpace(model)) return "no_model";
            if (HasOtherSpecies(c)) return "kept_existing";
            var matches = resolve(model);
            if (matches == null || matches.Count == 0) return "no_mapping";
            if (matches.Count > 1) return "ambiguous_mapping";
            var f = matches[0];
            if (f.Id == 0 || string.IsNullOrWhiteSpace(f.Name)) return "no_mapping";
            c.fish_lang_id = f.Id;
            c.fish_name = f.Name;
            c.autodetect_source = Source;
            return "applied";
        }

        /// <summary>Withdraws a Player.log species (ambiguity). Returns true if something was cleared.</summary>
        public static bool ClearIfFromPlayerLog(CatchData c)
        {
            if (c == null || c.autodetect_source != Source) return false;
            c.fish_name = "";
            c.fish_lang_id = null;
            c.fish_icon_path = "";
            c.fish_min_weight = null;
            c.fish_max_weight = null;
            c.fish_is_mutant = false;
            c.fish_is_aggression = false;
            c.autodetect_source = "none";
            return true;
        }

        /// <summary>Network species arriving for a record: the Player.log species gives way completely.</summary>
        public static bool BeforeNetworkMerge(CatchData existing, uint? networkFishLangId, string? networkFishName)
        {
            if (existing == null || existing.autodetect_source != Source) return false;
            if (!networkFishLangId.HasValue && IsNameMissing(networkFishName)) return false;
            return ClearIfFromPlayerLog(existing);
        }
    }
}
