using System.Collections.Generic;
using System.Linq;
using Godot;
using LiteDB;
using SphereHelpers.Extensions;
using SphServer.Packets;
using SphServer.Shared.GameData.Enums;
using SphServer.Shared.Logger;
using SphServer.Shared.WorldState;
using SphServer.Sphere.Game.Loot;
using SphServer.Sphere.Game.WorldObject;
using SphServer.System;

namespace SphServer.Shared.Db.DataModels;

/// <summary>
/// A ground sack's contents; the id doubles as the sack's wire id
/// </summary>
public class ItemContainerDbEntry
{
    /// <summary>
    /// The captured list frames stop at four entries
    /// </summary>
    public const int MaxItems = 4;

    [BsonId] public int Id { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Angle { get; set; }
    public int TitleMinusOne { get; set; }
    public int DegreeMinusOne { get; set; }

    public Dictionary<int, int> Contents { get; set; } = [];

    /// <summary>
    /// Set for a player's drop: what nobody picked up goes back to them instead of being deleted
    /// </summary>
    public int? DroppedByCharacterId { get; set; }

    /// <summary>
    /// Random loot for a title level, 1-4 items unless a count is given
    /// </summary>
    public static ItemContainerDbEntry SpawnRandomLoot (Vector3 godotPosition, int titleMinusOne,
        LootRatity ratity, int count = -1)
    {
        var itemCount = count == -1 ? SphRng.Rng.Next (1, MaxItems + 1) : Mathf.Clamp (count, 1, MaxItems);
        var items = new List<ItemDbEntry> ();
        for (var i = 0; i < itemCount; i++)
        {
            var item = ItemDbEntry.CreateFromGameObject (LootRandomizer.GetRandomLootObject (titleMinusOne));
            item.Id = WorldObjectIndex.NewItem ();
            DbConnection.Items.Insert (item.Id, item);
            items.Add (item);
        }

        return SpawnWithItems (godotPosition, items, titleMinusOne);
    }

    /// <summary>
    /// Items must already be in the item collection
    /// </summary>
    public static ItemContainerDbEntry SpawnWithItems (Vector3 godotPosition, IReadOnlyList<ItemDbEntry> items,
        int titleMinusOne = 0, int? droppedByCharacterId = null)
    {
        var container = new ItemContainerDbEntry
        {
            Id = WorldObjectIndex.New (),
            DroppedByCharacterId = droppedByCharacterId,
            TitleMinusOne = titleMinusOne,
            X = godotPosition.X,
            Y = -godotPosition.Y,
            Z = -godotPosition.Z
        };

        for (var i = 0; i < items.Count && i < MaxItems; i++)
        {
            items[i].ParentContainerId = container.Id;
            DbConnection.Items.Update (items[i]);
            container.Contents[i] = items[i].Id;
        }

        DbConnection.ItemContainers.Insert (container.Id, container);
        LootBag.Spawn (container, godotPosition);

        SphLogger.Info (
            $"Added loot bag {container.Id:X4} with {container.Contents.Count} item(s) at " +
            $"({container.X:F2}, {container.Y:F2}, {container.Z:F2})");

        return container;
    }

    /// <summary>
    /// Sacks live only in memory, so any left in the database lost their world object on restart
    /// </summary>
    public static int DeleteOrphans ()
    {
        var containers = DbConnection.ItemContainers.FindAll ().ToList ();
        foreach (var container in containers)
        {
            Delete (container, deleteItems: true);
        }

        return containers.Count;
    }

    public static void Delete (ItemContainerDbEntry container, bool deleteItems)
    {
        if (deleteItems && container.DroppedByCharacterId is { } ownerId)
        {
            ReturnToOwner (container, ownerId);
        }
        else if (deleteItems)
        {
            var carried = DbConnection.Characters.FindAll ()
                .SelectMany (x => x.Items.Values)
                .ToHashSet ();
            foreach (var itemId in container.Contents.Values.Where (x => !carried.Contains (x)))
            {
                DbConnection.Items.Delete (itemId);
                WorldObjectIndex.Release ((ushort) itemId);
            }
        }

        DbConnection.ItemContainers.Delete (container.Id);
    }

    /// <summary>
    /// An online owner gets the item declared again; offline, it goes into the stored character.
    /// With no free cell it stays an unparented row rather than being deleted
    /// </summary>
    private static void ReturnToOwner (ItemContainerDbEntry container, int ownerId)
    {
        foreach (var itemId in container.Contents.Values)
        {
            if (DbConnection.Items.FindById (itemId) is { } item)
            {
                var slot = ReturnItemToOwner (item, ownerId);
                SphLogger.Info (slot is null
                    ? $"Loot bag {container.Id:X4}: owner {ownerId} has no free cell for {item.Id:X4}, kept unparented"
                    : $"Loot bag {container.Id:X4}: returned {item.Id:X4} to owner {ownerId} in {slot}");
            }
        }
    }

    /// <summary>
    /// An online owner gets the item declared again; offline, it goes into the stored character.
    /// With no free cell it stays an unparented row rather than being deleted
    /// </summary>
    public static BelongingSlot? ReturnItemToOwner (ItemDbEntry item, int ownerId)
    {
        item.ParentContainerId = null;
        item.DroppedOnGroundBy = null;
        DbConnection.Items.Update (item);

        var client = ActiveClients.GetAll ().Values
            .FirstOrDefault (x => x.CurrentCharacter?.Id == ownerId);
        if (client is not null)
        {
            return client.PutItemInInventoryAndDeclare (item);
        }

        var stored = DbConnection.Characters.FindById (ownerId);
        var slot = stored?.FindEmptyInventorySlot ();
        if (slot is not null)
        {
            stored!.Items[slot.Value] = item.Id;
            DbConnection.Characters.Update (stored);
        }

        return slot;
    }

    /// <summary>
    /// Dropped items live only as ground objects, so any left from the previous run go back to
    /// the players who dropped them
    /// </summary>
    public static int ReturnGroundItemsFromLastRun ()
    {
        var items = DbConnection.Items.Find (x => x.DroppedOnGroundBy != null).ToList ();
        foreach (var item in items)
        {
            ReturnItemToOwner (item, item.DroppedOnGroundBy!.Value);
        }

        return items.Count;
    }

    public bool RemoveItemByIdAndDestroyContainerIfEmpty (int itemGlobalId)
    {
        if (Contents.ContainsValue (itemGlobalId))
        {
            var key = Contents.First (x => x.Value == itemGlobalId).Key;
            Contents.Remove (key);
            SphLogger.Info ($"Removed item {itemGlobalId:X4} at {key} from loot bag {Id:X4}");
        }

        return SaveOrDespawnIfEmpty ();
    }

    public bool RemoveItemBySlotIdAndDestroyContainerIfEmpty (int slotId)
    {
        if (Contents.Remove (slotId, out var itemId))
        {
            SphLogger.Info ($"Removed item {itemId:X4} at {slotId} from loot bag {Id:X4}");
        }

        return SaveOrDespawnIfEmpty ();
    }

    private bool SaveOrDespawnIfEmpty ()
    {
        if (Contents.Count != 0)
        {
            DbConnection.ItemContainers.Update (this);
            return false;
        }

        var bag = LootBag.Find (Id);
        if (bag is not null)
        {
            bag.DespawnEmpty ();
        }
        else
        {
            Delete (this, deleteItems: false);
        }

        return true;
    }

    /// <summary>
    /// The list frame, then one record per item parented to the sack
    /// </summary>
    public List<byte[]> GetContentsPackets ()
    {
        var items = GetItems ();
        var packets = new List<byte[]> ();
        var itemList = BuildItemListPacket (items);
        if (itemList is not null)
        {
            packets.Add (itemList);
        }

        packets.AddRange (items.Select (x => ItemRecordEncoder.EncodeInLootBag (x, (ushort) Id)));
        return packets;
    }

    public List<byte[]> GetItemRecordPackets () =>
        GetItems ().Select (x => ItemRecordEncoder.EncodeInLootBag (x, (ushort) Id)).ToList ();

    private List<ItemDbEntry> GetItems () =>
        Contents.OrderBy (x => x.Key)
            .Select (x => DbConnection.Items.FindById (x.Value))
            .Where (x => x is not null)
            .ToList ();

    /// <summary>
    /// Constant lead after the record header in every live list frame
    /// </summary>
    private const uint ListLead = 0x13;

    private const byte FollowOnMarker = 5;
    private const byte LockMessage = 3;
    private const byte SlotMessage = 2;

    /// <summary>
    /// Live list frames (mobSpawnAndDrops): unlock (type 3, one byte 0), then per slot type 2 with
    /// slot, 24-bit item id and 32-bit weight, framed like the item records. ct_mbag starts
    /// locked, and without the unlock its items cannot be taken out
    /// </summary>
    private byte[]? BuildItemListPacket (IReadOnlyList<ItemDbEntry> items)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var stream = SphServer.Shared.BitStream.SphBitStream.GetWriteBitStream ();
        stream.WriteUInt16 ((ushort) Id, 16);
        stream.WriteByte (0, 2);
        stream.WriteUInt16 ((ushort) ObjectType.Sack_Mob_Loot, 10);
        stream.WriteByte (0, 1);
        stream.WriteByte (0x0A, 8);
        stream.WriteUInt16 ((ushort) ListLead, 16);
        stream.WriteByte ((byte) (ListLead >> 16), 4);

        WriteMessageHeader (stream, LockMessage, 1);
        stream.WriteByte (0, 8);

        for (var slot = 0; slot < items.Count && slot < MaxItems; slot++)
        {
            WriteMessageHeader (stream, SlotMessage, 8);
            stream.WriteByte ((byte) slot, 8);
            stream.WriteUInt16 ((ushort) items[slot].Id, 16);
            stream.WriteByte ((byte) (items[slot].Id >> 16), 8);
            var weight = (uint) global::System.Math.Max (1, items[slot].Weight);
            stream.WriteUInt16 ((ushort) weight, 16);
            stream.WriteUInt16 ((ushort) (weight >> 16), 16);
        }

        return Packet.ToByteArray (stream.GetStreamData (), 3);
    }

    private static void WriteMessageHeader (SphServer.Shared.BitStream.SphWriteStream stream, byte type, byte length)
    {
        stream.WriteByte (FollowOnMarker, 7);
        stream.WriteByte (type, 4);
        stream.WriteByte (length, 8);
    }
}
