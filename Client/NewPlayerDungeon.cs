using System;
using Godot;
using SphServer.Helpers;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.Logger;
using SphServer.Shared.Networking;
using SphServer.Shared.Networking.DataModel.Serializers;

namespace SphServer.Client;

/// <summary>
/// First world entry of a new character: placed in the captured dungeon instance, teleported there
/// after the client settles, then the captured dungeon objects (guide, sword, token, chest,
/// tutorial tablet) are replayed
/// </summary>
public static class NewPlayerDungeon
{
    /// <summary>
    /// World coords of the captured instance spawn; the serializer flips y itself
    /// </summary>
    private static readonly WorldCoords TeleportCoords =
        new (-1098.69506835937500, -4501.61474609375000, 1900.05493164062500, 1.57079637050629);

    private const int StartingMoney = 10;
    private const double TeleportDelaySeconds = 3;
    private const double LoadDelaySeconds = 0.5;

    public static void PlaceCharacter (CharacterDbEntry character)
    {
        // DB keeps Y and Z negated relative to world coords
        character.X = TeleportCoords.x;
        character.Y = TeleportCoords.y;
        character.Z = -TeleportCoords.z;
        character.Angle = TeleportCoords.turn;
        character.Money = StartingMoney;
    }

    public static async void Start (SphereClient client)
    {
        try
        {
            await client.ToSignal (client.GetTree ().CreateTimer (TeleportDelaySeconds), SceneTreeTimer.SignalName.Timeout);
            if (client.CurrentCharacter is not { } character || !GodotObject.IsInstanceValid (client))
            {
                return;
            }

            client.MaybeQueueNetworkPacketSend (
                new CharacterDbEntrySerializer (character).GetNewPlayerDungeonTeleportAndUpdateStatsByteArray (TeleportCoords));

            await client.ToSignal (client.GetTree ().CreateTimer (LoadDelaySeconds), SceneTreeTimer.SignalName.Timeout);
            if (!GodotObject.IsInstanceValid (client))
            {
                return;
            }

            client.MaybeQueueNetworkPacketSend (CommonPackets.LoadNewPlayerDungeon);
            client.SendMoney ();
            SphLogger.Info ($"New player dungeon loaded for {character.Name}. Client ID: {character.ClientIndex:X4}");
        }
        catch (Exception ex)
        {
            SphLogger.Error ("New player dungeon start failed", ex);
        }
    }
}
