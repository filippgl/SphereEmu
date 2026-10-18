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
        if (item?.DroppedOnGroundBy is not null)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (CommonPackets.ClearUseToutAck (localId));
            PickUpFromGround (client, clientConnection, item);
            return;
        }

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

        if (NewPlayerDungeon.TokenIdFor (client) == globalItemId)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (CommonPackets.ClearUseToutAck (localId));
            NewPlayerDungeon.TakeToken (client);
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
        var targetSlot = ItemSlotReserve.SlotForWireId (targetSlotId) ?? BelongingSlot.Unknown;

        if (item.DroppedOnGroundBy is not null)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (CommonPackets.ClearUseToutAck (localId));
            PickUpFromGround (client, clientConnection, item, targetSlot);
            return;
        }

        var container = item.ParentContainerId is { } containerId
            ? DbConnection.ItemContainers.FindById (containerId)
            : null;
        var sackSlot = container?.Contents.FirstOrDefault (x => x.Value == item.Id);
        if (container is null || sackSlot?.Value != item.Id)
        {
            SphLogger.Warning ($"Pickup to slot: item {item.Id:X4} is not in a sack. Client ID: {localId:X4}");
            return;
        }

        // Requirements gate wearing, not carrying: anything may sit in a cell.
        if (targetSlot is BelongingSlot.Unknown || !character.IsItemSlotEmpty (targetSlot) ||
            !item.IsValidForSlot (targetSlot) ||
            (!ItemDbEntry.IsInventorySlot (targetSlot) && !character.CanUseItem (item)))
        {
            SphLogger.Info (
                $"Item {item.Localization[Locale.Russian]} [{globalItemId}] couldn't be used in slot [{Enum.GetName (targetSlot)}]");
            return;
        }

        SphLogger.Info (
            $"CLI: Move item {item.Localization[Locale.Russian]} ({item.ItemCount}) [{clientItemID}] " +
            $"to slot raw [{clientSlot_raw}] [{Enum.GetName (targetSlot)}]");

        character.PlaceItemInSlot (targetSlot, globalItemId);
        SphLogger.Info ($"{Enum.GetName ((BelongingSlot) targetSlotId)} now has " +
                       $"{item.Localization.GetValueOrDefault (Locale.Russian, "?")} " +
                       $"({item.ItemCount}) [{globalItemId}]");
        clientConnection.MaybeScheduleNetworkPacketSend (container.BuildClearSlotPacket (sackSlot.Value.Key));
        clientConnection.MaybeScheduleNetworkPacketSend (ItemRecordEncoder.EncodePutInto (item, ByteSwap (localId)));

        var reserve = ItemSlotReserve.Build (localId, targetSlot, item.Id, item.ItemCount);
        if (reserve is not null)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (reserve);
        }

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
