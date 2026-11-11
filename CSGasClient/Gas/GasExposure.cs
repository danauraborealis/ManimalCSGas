using System;
using System.Collections.Generic;
using EFT;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // which infected have been inside which vaccine gas, and when. retail does this with a stim buff
    // on the bot and a quest condition that reads it (enemyHealthBuffs); the 4.1 client has no such
    // condition field, so the mark lives here and Api.Gas hands it to whoever counts the kills.
    internal static class GasExposure
    {
        // retail's buffs run 60s from the dose
        internal const float WindowSeconds = 60f;

        private static readonly Dictionary<string, Dictionary<string, float>> Marks = new Dictionary<string, Dictionary<string, float>>();
        private static readonly Dictionary<string, bool> InfectedCache = new Dictionary<string, bool>();
        private static bool _buffFailLogged;

        internal static event Action<string, string> Dosed;

        // every infected role is named infected*, including ones a later patch adds
        internal static bool IsInfected(Player player)
        {
            var id = player.ProfileId;
            if (id == null) return false;
            if (InfectedCache.TryGetValue(id, out var known)) return known;
            var settings = player.Profile?.Info?.Settings;
            var infected = settings != null && settings.Role.ToString().StartsWith("infected", StringComparison.OrdinalIgnoreCase);
            InfectedCache[id] = infected;
            return infected;
        }

        internal static void Mark(Player bot, string buff)
        {
            var id = bot.ProfileId;
            if (id == null) return;
            var first = !WasGassed(id, buff);
            if (!Marks.TryGetValue(id, out var marks))
                Marks[id] = marks = new Dictionary<string, float>();
            marks[buff] = Time.time;

            if (Plugin.GasInfectedBuffs.Value)
            {
                try
                {
                    // dedupes by buff name, so a tick while still inside just keeps it alive
                    bot.ActiveHealthController?.TryDoExternalBuff(buff);
                }
                catch (Exception e)
                {
                    if (!_buffFailLogged)
                    {
                        _buffFailLogged = true;
                        Plugin.LogSource?.LogWarning("[CSGas] could not apply " + buff + " to an infected: " + e.Message);
                    }
                }
            }

            if (!first) return;
            Plugin.LogSource?.LogInfo("[CSGas] infected " + id + " dosed with " + buff);
            try { Dosed?.Invoke(id, buff); }
            catch (Exception e) { Plugin.LogSource?.LogWarning("[CSGas] a Dosed handler threw: " + e.Message); }
        }

        internal static bool WasGassed(string profileId, string buff)
        {
            return profileId != null && buff != null
                && Marks.TryGetValue(profileId, out var marks)
                && marks.TryGetValue(buff, out var time)
                && Time.time - time <= WindowSeconds;
        }

        internal static void Clear()
        {
            Marks.Clear();
            InfectedCache.Clear();
        }
    }
}
