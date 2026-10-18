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

        var slots = character.Items.Where (kv => kv.Value == itemId).Select (kv => kv.Key).ToList ();
        var item = DbConnection.Items.FindById (itemId);
        if (slots.Count == 0 || item is null)
        {
            SphLogger.Warning ($"Drop to ground: item {itemId:X4} is not carried. Client ID: {localId:X4}");
            return;
        }

        // The client lets go of the item only once the sack's spawn re-parents it there
        var wasWorn = slots.Any (slot => !ItemDbEntry.IsInventorySlot (slot));
        foreach (var slot in slots)
        {
            character.Items.Remove (slot);
        }

        if (wasWorn && character.RecalcCurrentStats ())
        {
            NetworkedStatsUpdater.Update (character);
        }

        client.SaveCharacter ();
        var bag = ItemContainerDbEntry.SpawnWithItems (character.Origin, [item]);

        var name = item.Localization.GetValueOrDefault (Locale.Russian, item.GameObjectType.ToString ());
        SphLogger.Info ($"Dropped {name} [{itemId:X4}] from {string.Join (",", slots)} into loot bag {bag.Id:X4}. Client ID: {localId:X4}");
    }
}
