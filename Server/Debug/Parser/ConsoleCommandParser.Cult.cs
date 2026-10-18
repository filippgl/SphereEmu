using System.Globalization;
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

            case "skin":
                // Trial of region 61 model_or_type; seen by peers on their next spawn of this character
                uint? model = split.Length > 1 && uint.TryParse (split[1], out var value) ? value : null;
                CultRules.SetSpawnModelTrial (currentCharacterDbEntry, model);
                SendFeedback (model is null ? "Spawn model trial cleared" : $"Spawn model trial = {model}");
                break;

            default:
                var member = CultRules.IsMember (currentCharacterDbEntry);
                var respawn = CultRules.RespawnPoint?.ToString () ?? "nearest city";
                SendFeedback (
                    $"Cult member: {member}, karma {currentCharacterDbEntry.Karma} acts as " +
                    $"{CultRules.EffectiveKarma (currentCharacterDbEntry)}, respawn {respawn}. " +
                    "Usage: /cult join [rank] | leave | here");
                break;
        }
    }
}
