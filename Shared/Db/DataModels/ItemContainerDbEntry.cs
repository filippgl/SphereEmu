using System.Collections.Generic;
using System.Linq;
using Godot;
using LiteDB;
using SphServer.Packets;
using SphServer.Shared.GameData.Enums;
using SphServer.Shared.Logger;
using SphServer.Shared.WorldState;
using SphServer.Sphere.Game.Loot;
using SphServer.Sphere.Game.WorldObject;
using SphServer.System;
using static SphServer.Shared.Networking.DataModel.Serializers.SphereDbEntrySerializerBase;

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
        int titleMinusOne = 0)
    {
        var container = new ItemContainerDbEntry
        {
            Id = WorldObjectIndex.New (),
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
        if (deleteItems)
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
        var itemList = BuildItemListPacket (items.Select (x => x.Id).ToList ());
        if (itemList is not null)
        {
            packets.Add (itemList);
        }

        packets.AddRange (items.Select (x => ItemRecordEncoder.Encode (x, (ushort) Id)));
        return packets;
    }

    public List<byte[]> GetItemRecordPackets () =>
        GetItems ().Select (x => ItemRecordEncoder.Encode (x, (ushort) Id)).ToList ();

    private List<ItemDbEntry> GetItems () =>
        Contents.OrderBy (x => x.Key)
            .Select (x => DbConnection.Items.FindById (x.Value))
            .Where (x => x is not null)
            .ToList ();

    /// <summary>
    /// Captured live replies to an open, one per item count; the bytes between ids are weights
    /// and are left as captured
    /// </summary>
    private byte[]? BuildItemListPacket (IReadOnlyList<int> itemIds)
    {
        int ItemId (int index) => index < itemIds.Count ? itemIds[index] : 0;

        var item_0_id = ItemId (0);
        var item_1_id = ItemId (1);
        var item_2_id = ItemId (2);
        var item_3_id = ItemId (3);

        var item0_1 = (byte) ((item_0_id & 0b1111) << 4);
        var item0_2 = (byte) ((item_0_id >> 4) & 0b11111111);
        var item0_3 = (byte) ((item_0_id >> 12) & 0b1111);

        var item1_1 = (byte) ((item_1_id & 0b1) << 7);
        var item1_2 = (byte) ((item_1_id >> 1) & 0b11111111);
        var item1_3 = (byte) ((item_1_id >> 9) & 0b1111111);

        var item2_1 = (byte) ((item_2_id & 0b111111) << 2);
        var item2_2 = (byte) ((item_2_id >> 6) & 0b11111111);
        var item2_3 = (byte) ((item_2_id >> 14) & 0b11);

        var item3_1 = (byte) ((item_3_id & 0b111) << 5);
        var item3_2 = (byte) ((item_3_id >> 3) & 0b11111111);
        var item3_3 = (byte) ((item_3_id >> 11) & 0b11111);

        var localId = (ushort) Id;

        switch (itemIds.Count)
        {
            case 1:
                return
                [
                    0x19, 0x00, 0x2C, 0x01, 0x00, 0x00, 0x00, MinorByte (localId), MajorByte (localId), 0x5C,
                    0x46, 0x61, 0x02, 0x00, 0x0A, 0x82, 0x00, item0_1, item0_2, item0_3, 0x70, 0x0D, 0x00,
                    0x00, 0x00
                ];
            case 2:
                return
                [
                    0x23, 0x00, 0x2C, 0x01, 0x00, 0x00, 0x00, MinorByte (localId), MajorByte (localId), 0x5C,
                    0x46, 0x61, 0x02, 0x00, 0x0A, 0x82, 0x00, item0_1, item0_2, item0_3, 0xC0,
                    0x00, 0x00,
                    0x00, 0x50, 0x10, 0x84, item1_1, item1_2, item1_3, 0x00, 0x4B, 0x00, 0x00,
                    0x00
                ];
            case 3:
                return
                [
                    0x2E, 0x00, 0x2C, 0x01, 0x00, 0x00, 0x00, MinorByte (localId), MajorByte (localId), 0x5C,
                    0x46, 0x61, 0x02, 0x00, 0x0A, 0x82, 0x00, item0_1, item0_2, item0_3, 0x30, 0x00, 0x00,
                    0x00, 0x50,
                    0x10, 0x84, item1_1, item1_2, item1_3, 0x00, 0x08, 0x00, 0x00, 0x80, 0x82, 0x20, 0x08,
                    item2_1,
                    item2_2, item2_3, 0x2C, 0x00, 0x00, 0x00, 0x00
                ];
            case 4:
                return
                [
                    0x38, 0x00, 0x2C, 0x01, 0x00, 0x00, 0x00, MinorByte (localId), MajorByte (localId), 0x5C,
                    0x46, 0x61, 0x02, 0x00, 0x0A, 0x82, 0x00, item0_1, item0_2, item0_3, 0x30, 0x00, 0x00,
                    0x00, 0x50,
                    0x10, 0x84, item1_1, item1_2, item1_3, 0x00, 0x08, 0x00, 0x00, 0x80, 0x82, 0x20, 0x08,
                    item2_1,
                    item2_2, item2_3, 0x2C, 0x00, 0x00, 0x00, 0x14, 0x04, 0x61, item3_1, item3_2, item3_3,
                    0x80, 0x19,
                    0x00, 0x00, 0x00
                ];
            default:
                SphLogger.Warning ($"Loot bag {Id:X4}: no item list frame for {itemIds.Count} item(s)");
                return null;
        }
    }
}
