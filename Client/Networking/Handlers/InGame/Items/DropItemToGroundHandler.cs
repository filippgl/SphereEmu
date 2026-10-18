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

        var slots = character.Items.Where (kv => kv.Value == itemId).Select (kv => kv.Key).ToList ();
        var item = DbConnection.Items.FindById (itemId);
        if (slots.Count == 0 || item is null)
        {
            SphLogger.Warning ($"Drop to ground: item {itemId:X4} is not carried. Client ID: {localId:X4}");
            return;
        }

        // TODO: the client keeps the item until it gets a loot bag holding it; removing it here
        // without that desyncs the inventory, so the drop is refused for now
        var name = item.Localization.GetValueOrDefault (Locale.Russian, item.GameObjectType.ToString ());
        SphLogger.Info ($"Drop to ground refused: {name} [{itemId:X4}] stays in {string.Join (",", slots)}. Client ID: {localId:X4}");
    }
}
