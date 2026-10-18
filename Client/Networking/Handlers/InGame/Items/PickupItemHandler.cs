using System;
using System.Linq;
using System.Threading.Tasks;
using BitStreams;
using SphereHelpers.Extensions;
using SphServer.Packets;
using SphServer.Client.Networking.GameplayLogic.Stats;
using SphServer.Shared.Db;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.Logger;
using SphServer.Shared.Networking;
using SphServer.Shared.WorldState;
using SphServer.Sphere.Game.WorldObject;
using static SphServer.Shared.BitStream.SphBitStream;
using static SphServer.Shared.Networking.DataModel.Serializers.SphereDbEntrySerializerBase;

namespace SphServer.Client.Networking.Handlers.InGame.Items;

public class PickupItemHandler (ushort localId, ClientConnection clientConnection, SphereClient client)
    : ISphereClientNetworkingHandler
{
    // 0x2E reply: object type at bit 74, the three position floats at bits 93/125/157
    private const int ObjectTypeBitOffset = 74;
    private const int PositionBitOffset = 93;

    private static uint FloatBits (double value)
    {
        return BitConverter.SingleToUInt32Bits ((float) value);
    }

    // LSB-first, and bits outside the field stay put because the fields share bytes
    public static byte[] BuildPickupMoveResult (int clientItemID, ushort clientSyncOther, byte clientSync1,
        byte clientSync2, byte clientSlotRaw, int objectType, double x, double y, double z)
    {
        var serverItemID_1 = (clientItemID & 0b111111) << 2;
        var serverItemID_2 = (clientItemID & 0b11111111000000) >> 6;
        var serverItemID_3 = (clientItemID & 0b1100000000000000) >> 14;

        var moveResult = new byte[]
        {
            0x2E, 0x00, 0x2C, 0x01, 0x00, 0x00, 0x00, MinorByte ((ushort) clientItemID),
            MajorByte ((ushort) clientItemID), 0xE8, 0xC7, 0xA0, 0xB0, 0x6E, 0xA6, 0x88, 0x98, 0x95, 0xB1, 0x28, 0x09,
            0xDC, 0x85, 0xC8, 0xDF, 0x02, 0x0C, MinorByte (clientSyncOther), MajorByte (clientSyncOther), 0x01, 0xFC,
            clientSync1, clientSync2, 0x10, 0x80, 0x82, 0x20, (byte) (clientSlotRaw << 1), (byte) serverItemID_1,
            (byte) serverItemID_2, (byte) serverItemID_3, 0x20, 0x4E, 0x00, 0x00, 0x00
        };

        // The template is one captured item, so type and position are overwritten in place
        var stream = new BitStream (moveResult);
        stream.SeekBitOffset (ObjectTypeBitOffset);
        stream.WriteUInt16 ((ushort) objectType, 12);
        stream.SeekBitOffset (PositionBitOffset);
        WriteFloatBits (stream, x);
        WriteFloatBits (stream, -y);
        WriteFloatBits (stream, -z);
        return stream.GetStreamData ();
    }

    private static void WriteFloatBits (BitStream stream, double value)
    {
        var bits = FloatBits (value);
        stream.WriteUInt16 ((ushort) bits, 16);
        stream.WriteUInt16 ((ushort) (bits >> 16), 16);
    }

    public async Task Handle (byte[] frame, double delta)
    {
    }

    /// <summary>
    /// First payload bit after the action code and flag
    /// </summary>
    private const int PickupPayloadBit = 131;

    /// <summary>
    /// Both live pickups in itemPickup.txt carry the item id here, as the drop frame does
    /// </summary>
    private const int PickupItemIdBit = 141;

    /// <summary>
    /// Double-click in an open sack: the item goes to the first free cell
    /// </summary>
    public async Task HandlePickupToNextAvailableEmptySlot (byte[] frame, double delta)
    {
        SphLogger.Info ($"CLI: Pickup to free slot frame {Convert.ToHexString (frame)}. Client ID: {localId:X4}");

        if (NewPlayerDungeon.StarterSwordIdFor (client) is { } swordId && PayloadContainsId (frame, swordId))
        {
            NewPlayerDungeon.TakeStarterSword (client);
            return;
        }

        var stream = new BitStream (frame);
        stream.SeekBitOffset (PickupItemIdBit);
        var itemId = (int) stream.ReadUInt16 (16);
        var item = DbConnection.Items.FindById (itemId);
        var container = item?.ParentContainerId is { } containerId
            ? DbConnection.ItemContainers.FindById (containerId)
            : null;
        var sackSlot = container?.Contents.FirstOrDefault (x => x.Value == itemId);
        if (item is null || container is null || sackSlot?.Value != itemId)
        {
            SphLogger.Warning ($"Pickup to free slot: item {itemId:X4} is not in a sack. Client ID: {localId:X4}");
            return;
        }

        var character = client.CurrentCharacter!;
        if (character.FindEmptyInventorySlot () is not { } slot)
        {
            SphLogger.Info ($"Pickup to free slot: inventory full. Client ID: {localId:X4}");
            return;
        }

        character.Items[slot] = item.Id;
        clientConnection.SaveSelectedCharacter ();

        // The live reply (itemPickup.txt): the sack clears its cell, the item takes the player as
        // parent, the player's cell gets the item
        clientConnection.MaybeScheduleNetworkPacketSend (container.BuildClearSlotPacket (sackSlot.Value.Key));
        clientConnection.MaybeScheduleNetworkPacketSend (ItemRecordEncoder.EncodePutInto (item, ByteSwap (localId)));
        if (ItemSlotReserve.Build (localId, slot, item.Id, item.ItemCount) is { } reserve)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (reserve);
        }

        SphLogger.Info (
            $"CLI: Picked up {item.Localization.GetValueOrDefault (Locale.Russian, "?")} [{item.Id:X4}] " +
            $"from sack {container.Id:X4} cell {sackSlot.Value.Key} into {Enum.GetName (slot)}. Client ID: {localId:X4}");
        TakeOutOfLootBag (item);
    }

    private static bool PayloadContainsId (byte[] frame, ushort id)
    {
        for (var bit = PickupPayloadBit; bit + 16 <= frame.Length * 8; bit++)
        {
            var stream = new BitStream (frame);
            stream.SeekBitOffset (bit);
            if (stream.ReadUInt16 (16) == id)
            {
                return true;
            }
        }

        return false;
    }

    public async Task HandlePickupToTargetSlot (byte[] frame, double delta)
    {
        var clientItemID_1 = frame[21] >> 1;
        var clientItemID_2 = frame[22];
        var clientItemID_3 = frame[23] % 2;
        var clientItemID = (clientItemID_3 << 15) + (clientItemID_2 << 7) + clientItemID_1;

        var globalItemId = (ushort) clientItemID;

        // No 0x2E reply: the pedestal object is despawned and a real sword is declared instead
        if (NewPlayerDungeon.StarterSwordIdFor (client) == globalItemId)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (CommonPackets.ClearUseToutAck (localId));
            NewPlayerDungeon.TakeStarterSword (client);
            return;
        }

        var item = DbConnection.Items.Find (x => x.Id == globalItemId).FirstOrDefault ();

        if (item is null)
        {
            SphLogger.Warning ($"Unable to pickup item. Missing ID: {globalItemId:X4}. Client ID: {localId:X4}");
            return;
        }

        var character = clientConnection.GetSelectedCharacter ()!;

        var clientSlot_raw = frame[24];
        var targetSlotId = clientSlot_raw >> 1;
        var targetSlot = Enum.IsDefined (typeof (BelongingSlot), targetSlotId)
            ? (BelongingSlot) targetSlotId
            : BelongingSlot.Unknown;

        if (item.DroppedOnGroundBy is not null)
        {
            PickUpFromGround (client, clientConnection, item, targetSlot);
            return;
        }

        // Requirements gate wearing, not carrying: anything may sit in a cell.
        if (targetSlot is BelongingSlot.Unknown || !item.IsValidForSlot (targetSlot) ||
            (!ItemDbEntry.IsInventorySlot (targetSlot) && !character.CanUseItem (item)))
        {
            SphLogger.Info (
                $"Item {item.Localization[Locale.Russian]} [{globalItemId}] couldn't be used in slot [{Enum.GetName (targetSlot)}]");
            return;
        }

        SphLogger.Info (
            $"CLI: Move item {item.Localization[Locale.Russian]} ({item.ItemCount}) [{clientItemID}] " +
            $"to slot raw [{clientSlot_raw}] [{Enum.GetName (targetSlot)}]");

        var clientSync_1 = frame[17];
        var clientSync_2 = frame[18];

        var clientSyncOther_1 = (frame[10] & 0b11000000) >> 4;
        var clientSyncOther_2 = frame[11];
        var clientSyncOther_3 = frame[12] & 0b111111;
        var clientSyncOther = (ushort) ((clientSyncOther_3 << 10) + (clientSyncOther_2 << 2) + clientSyncOther_1);

        var moveResult = BuildPickupMoveResult (clientItemID, clientSyncOther, clientSync_1, clientSync_2,
            clientSlot_raw, (int) item.ObjectType, item.X, item.Y, item.Z);

        character.PlaceItemInSlot (targetSlot, globalItemId);
        SphLogger.Info ($"{Enum.GetName ((BelongingSlot) targetSlotId)} now has " +
                       $"{item.Localization.GetValueOrDefault (Locale.Russian, "?")} " +
                       $"({item.ItemCount}) [{globalItemId}]");
        clientConnection.MaybeScheduleNetworkPacketSend (moveResult);

        // The grant above does not fill the slot array, so the cell stays empty without these.
        var reserve = ItemSlotReserve.Build (localId, targetSlot, item.Id, item.ItemCount);
        if (reserve is not null)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (reserve);
        }

        clientConnection.MaybeScheduleNetworkPacketSend (ItemRecordEncoder.Encode (item, ByteSwap (localId)));

        TakeOutOfLootBag (item);
        TakeOffTheGround (item);

        if (!ItemDbEntry.IsInventorySlot (targetSlot))
        {
            if (character.RecalcCurrentStats ())
            {
                NetworkedStatsUpdater.Update (character);
            }
        }

        // Recalc writes worn look and item stats, so a save before it would miss them
        clientConnection.SaveSelectedCharacter ();
    }

    /// <summary>
    /// A dropped item into the picker's inventory: the wanted cell if it is a free inventory cell,
    /// otherwise the first free one. The reply is the live sack pickup without the sack: the item
    /// takes the player as parent and the player's cell gets the item
    /// </summary>
    public static bool PickUpFromGround (SphereClient client, ClientConnection clientConnection, ItemDbEntry item,
        BelongingSlot? wanted = null)
    {
        var localId = clientConnection.LocalId;
        if (client.CurrentCharacter is not { } character)
        {
            return false;
        }

        var slot = wanted is { } cell && ItemDbEntry.IsInventorySlot (cell) && character.IsItemSlotEmpty (cell)
            ? cell
            : character.FindEmptyInventorySlot ();
        if (slot is null)
        {
            SphLogger.Info ($"Pickup off the ground: inventory full. Client ID: {localId:X4}");
            return false;
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
                        $"off the ground into {Enum.GetName (slot.Value)}. Client ID: {localId:X4}");
        return true;
    }

    /// <summary>
    /// A dropped item is lifted for everyone who sees it and no longer goes back to its dropper
    /// </summary>
    private void TakeOffTheGround (ItemDbEntry item)
    {
        if (item.DroppedOnGroundBy is null)
        {
            return;
        }

        item.DroppedOnGroundBy = null;
        DbConnection.Items.Update (item);
        GroundItem.Find (item.Id)?.PickedUp (client);
    }

    /// <summary>
    /// The emptied sack despawns for everyone who sees it, the picker included
    /// </summary>
    private static void TakeOutOfLootBag (ItemDbEntry item)
    {
        var container = item.ParentContainerId is null
            ? null
            : DbConnection.ItemContainers.FindById (item.ParentContainerId);
        if (container is null)
        {
            return;
        }

        item.ParentContainerId = null;
        DbConnection.Items.Update (item);
        container.RemoveItemByIdAndDestroyContainerIfEmpty (item.Id);
    }
}
