using System;
using System.Threading.Tasks;
using BitStreams;
using SphereHelpers.Extensions;
using SphServer.Packets;
using SphServer.Shared.Db;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.Logger;
using SphServer.Shared.Networking;
using SphServer.Shared.WorldState;
using SphServer.Sphere.Game.WorldObject;
using static SphServer.Shared.BitStream.SphBitStream;

namespace SphServer.Client.Networking.Handlers.InGame.Items;

// 08 40 63 at bytes 13-15; the client retries about once a second until this is answered
public class DragItemOnGroundHandler (ushort localId, ClientConnection clientConnection)
    : ISphereClientNetworkingHandler
{
    // Item id is the spawned entity; the three floats track the player during the drag
    private const int ItemIdBitOffset = 141;
    private const int PositionBitOffset = 165;

    /// <summary>
    /// Length 0x26 ends the drag in a cell: no item id, the cell number is byte 17
    /// </summary>
    private const int DropIntoCellFrameLength = 0x26;
    private const int CellByte = 17;

    /// <summary>
    /// The drop into a cell does not repeat the id, so the last drag frame names the item
    /// </summary>
    private ushort? draggedItemId;

    public async Task Handle (byte[] frame, double delta)
    {
        // Dragging arms g_6008, so this ack clears it or the client stays wedged
        clientConnection.MaybeScheduleNetworkPacketSend (CommonPackets.ClearUseToutAck (localId));

        if (frame[0] == DropIntoCellFrameLength)
        {
            PutDraggedItemIntoCell (frame);
            return;
        }

        var buffer = frame;
        var stream = new BitStream (buffer);
        stream.ReadBits (ItemIdBitOffset);
        var itemId = stream.ReadUInt16 (16);

        stream = new BitStream (buffer);
        stream.ReadBits (PositionBitOffset);
        var x = ReadFloat (stream);
        var y = ReadFloat (stream);
        var z = ReadFloat (stream);

        // The pedestal sword is not a DB item; grabbing it off the pedestal is how the client takes it
        if (ActiveClients.Get (localId) is { } client && NewPlayerDungeon.StarterSwordIdFor (client) == itemId)
        {
            NewPlayerDungeon.TakeStarterSword (client);
            return;
        }

        var item = DbConnection.Items.FindById ((int) itemId);
        if (item is null)
        {
            SphLogger.Warning ($"Drag on ground: no item {itemId:X4}. Client ID: {localId:X4}");
            return;
        }

        draggedItemId = itemId;

        // Client coords -> DB coords: Y and Z are negated, the same convention the spawn uses.
        item.X = x;
        item.Y = -y;
        item.Z = -z;
        DbConnection.Items.Update (item);

        SphLogger.Info ($"Drag on ground: {item.Localization.GetValueOrDefault (Locale.Russian, "?")} [{itemId:X4}] " +
                       $"to ({x:F2}, {y:F2}, {z:F2}). Client ID: {localId:X4}");
    }

    /// <summary>
    /// A ground item dragged into the inventory: the same reply as the live sack pickup, without
    /// the sack. The item takes the player as parent and the player's cell gets the item
    /// </summary>
    private void PutDraggedItemIntoCell (byte[] frame)
    {
        var item = draggedItemId is { } id ? DbConnection.Items.FindById ((int) id) : null;
        draggedItemId = null;
        if (item?.DroppedOnGroundBy is null || ActiveClients.Get (localId) is not { } client ||
            client.CurrentCharacter is not { } character)
        {
            SphLogger.Warning ($"Drag into cell: no dragged ground item ({Convert.ToHexString (frame)}). " +
                               $"Client ID: {localId:X4}");
            return;
        }

        // An occupied or non-inventory cell takes the first free one instead
        var cell = (BelongingSlot) frame[CellByte];
        var slot = ItemDbEntry.IsInventorySlot (cell) && character.IsItemSlotEmpty (cell)
            ? cell
            : character.FindEmptyInventorySlot ();
        if (slot is null)
        {
            SphLogger.Info ($"Drag into cell: inventory full. Client ID: {localId:X4}");
            return;
        }

        character.Items[slot.Value] = item.Id;
        clientConnection.SaveSelectedCharacter ();

        clientConnection.MaybeScheduleNetworkPacketSend (ItemRecordEncoder.EncodePutInto (item, ByteSwap (localId)));
        if (ItemSlotReserve.Build (localId, slot.Value, item.Id, item.ItemCount) is { } reserve)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (reserve);
        }

        item.DroppedOnGroundBy = null;
        DbConnection.Items.Update (item);
        GroundItem.Find (item.Id)?.PickedUp (client);

        SphLogger.Info ($"Picked up {item.Localization.GetValueOrDefault (Locale.Russian, "?")} [{item.Id:X4}] " +
                        $"off the ground into {Enum.GetName (slot.Value)} (cell byte {frame[CellByte]}). " +
                        $"Client ID: {localId:X4}");
    }

    private static double ReadFloat (BitStream stream)
    {
        return BitConverter.Int32BitsToSingle ((int) stream.ReadUInt32 (32));
    }
}
