using System.Globalization;
using SphServer.Client.Networking.GameplayLogic.Stats;
using SphServer.Helpers;
using SphServer.Server.GameplayLogic.Cult;

namespace SphServer.Server.Debug.Parser;

public partial class ConsoleCommandParser
{
    private void Cult (string args)
    {
        var split = args.Split (" ", StringSplitOptions.RemoveEmptyEntries);
        var action = split.Length > 0 ? split[0].ToLowerInvariant () : "info";
        switch (action)
        {
            case "join":
                ClanRank? rank = split.Length > 1 && int.TryParse (split[1], out var rankValue)
                    ? (ClanRank) rankValue
                    : null;
                SendFeedback (sphereClient is not null && CultRules.Join (sphereClient, rank)
                    ? $"Joined the cult as {currentCharacterDbEntry.ClanRank}"
                    : "Cult join failed (see the log)");
                break;

            case "leave":
                SendFeedback (sphereClient is not null && CultRules.Leave (sphereClient)
                    ? "Left the cult; the nameplate clears on the next spawn"
                    : "Not a cult member");
                break;

            case "here":
                // DB keeps Y and Z negated relative to world coords
                var c = currentCharacterDbEntry;
                var here = new WorldCoords (c.X, -c.Y, -c.Z, c.Angle);
                CultRules.SetRespawnPoint (here);
                SendFeedback (string.Format (CultureInfo.InvariantCulture,
                    "Cult respawn set here: [{0:F1}, {1:F1}, {2:F1}, {3:F2}]", here.x, here.y, here.z, here.turn));
                break;

            case "karma":
                // Signed count, so a GM can try both mirrored ends; /stats parses it unsigned
                if (split.Length < 2 || !int.TryParse (split[1], out var karmaCount))
                {
                    SendFeedback ("Usage: /cult karma <count -5000..5000>");
                    break;
                }

                currentCharacterDbEntry.SetKarmaCount (karmaCount);
                NetworkedStatsUpdater.Update (currentCharacterDbEntry, refreshPeers: true);
                sphereClient?.SaveCharacter ();
                SendFeedback ($"Karma {currentCharacterDbEntry.KarmaCount} ({currentCharacterDbEntry.Karma}), " +
                              $"acts as {CultRules.EffectiveKarma (currentCharacterDbEntry)}");
                break;

            case "skin":
                // turnskin.cfg SK number; off or 0 restores the character's own body
                byte skin = split.Length > 1 && byte.TryParse (split[1], out var value) ? value : (byte) 0;
                if (sphereClient is not null)
                {
                    CultRules.SetSkin (sphereClient, skin);
                }

                SendFeedback (skin == 0 ? "Skin removed" : $"Skin set to SK{skin:D2}");
                break;

            default:
                var member = CultRules.IsMember (currentCharacterDbEntry);
                var respawn = CultRules.RespawnPoint?.ToString () ?? "nearest city";
                SendFeedback (
                    $"Cult member: {member}, karma {currentCharacterDbEntry.Karma} acts as " +
                    $"{CultRules.EffectiveKarma (currentCharacterDbEntry)}, respawn {respawn}. " +
                    "Usage: /cult join [rank] | leave | here | karma <n> | skin <n|off>");
                break;
        }
    }
}
