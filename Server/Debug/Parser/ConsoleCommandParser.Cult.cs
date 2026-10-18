using System.Globalization;
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
                var here = string.Format (CultureInfo.InvariantCulture, "[{0:F1}, {1:F1}, {2:F1}, {3:F2}]",
                    c.X, -c.Y, -c.Z, c.Angle);
                SendFeedback ($"cult.json \"respawn\": {here}");
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
