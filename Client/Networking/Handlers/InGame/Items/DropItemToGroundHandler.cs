using System;
using System.Linq;
using System.Threading.Tasks;
using BitStreams;
using SphereHelpers.Extensions;
using SphServer.Client.Networking.GameplayLogic.Stats;
using SphServer.Packets;
using SphServer.Shared.Db;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.Logger;
using SphServer.Shared.Networking;

namespace SphServer.Client.Networking.Handlers.InGame.Items;

// 08 40 63 at bytes 13-15, length 0x2D; the rest is an empty item record template with no position
public class DropItemToGroundHandler (ushort localId, ClientConnection clientConnection, SphereClient client)
    : ISphereClientNetworkingHandler
{
    private const int ItemIdBitOffset = 141;
    private const ushort GroundContainerId = 0xFF00;
    private const double DropOffsetZ = 1.0;

    public async Task Handle (byte[] frame, double delta)
    {
        // Same use timeout as the drag, or the client stays wedged
        clientConnection.MaybeScheduleNetworkPacketSend (CommonPackets.ClearUseToutAck (localId));

        var stream = new BitStream (frame);
        stream.ReadBits (ItemIdBitOffset);
        var itemId = (int) stream.ReadUInt32 (16);

        var character = client.CurrentCharacter;
        if (character is null)
        {
            return;
        }

        // MainHand repeats the inventory slot, so every slot holding the item is cleared
        var slots = character.Items.Where (kv => kv.Value == itemId).Select (kv => kv.Key).ToList ();
        var item = DbConnection.Items.FindById (itemId);
        if (slots.Count == 0 || item is null)
        {
            SphLogger.Warning ($"Drop to ground: item {itemId:X4} is not carried. Client ID: {localId:X4}");
            return;
        }

        var lookBefore = CharacterWornLook.Capture (character);
        foreach (var slot in slots)
        {
            character.Items.Remove (slot);
        }

        item.X = character.X;
        item.Y = character.Y;
        item.Z = character.Z + DropOffsetZ;
        item.ParentContainerId = null;
        DbConnection.Items.Update (item);

        // Same record as carried, reparented to the ground container at the player's feet
        client.MaybeQueueNetworkPacketSend (ItemRecordEncoder.EncodeWithoutGameId ((ushort) item.Id,
            (int) item.WireObjectType, GroundContainerId, (float) item.X, (float) -item.Y, (float) -item.Z));

        if (character.RecalcCurrentStats ())
        {
            NetworkedStatsUpdater.Update (character);
        }

        client.SaveCharacter ();
        if (lookBefore != CharacterWornLook.Capture (character))
        {
            client.BroadcastAppearanceRefreshToVisibleClients ();
        }

        var name = item.Localization.GetValueOrDefault (Locale.Russian, item.GameObjectType.ToString ());
        SphLogger.Info ($"Drop to ground: {name} [{itemId:X4}] from {string.Join (",", slots)}. Client ID: {localId:X4}");
    }
}
