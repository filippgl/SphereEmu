using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SphereHelpers.Extensions;
using SphServer.Client.Networking.GameplayLogic.Stats;
using SphServer.Packets;
using SphServer.Shared.Db;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.Logger;
using SphServer.Shared.Networking;
using SphServer.Shared.WorldState;

namespace SphServer.Client.Networking.Handlers.InGame.Items;

public class UseItemHandler (ushort localId, ClientConnection clientConnection, SphereClient client)
    : ISphereClientNetworkingHandler
{
    public async Task Handle (byte[] frame, double delta)
    {
        var itemId = (ushort) (frame[11] + frame[12] * 0x100);
        // Item use arms g_6008, so this ack clears it or the client stays wedged
        clientConnection.MaybeScheduleNetworkPacketSend (CommonPackets.ClearUseToutAck (localId));

        if (NewPlayerDungeon.StarterSwordIdFor (client) == itemId)
        {
            NewPlayerDungeon.TakeStarterSword (client);
            return;
        }

        if (NewPlayerDungeon.TokenIdFor (client) == itemId)
        {
            NewPlayerDungeon.TakeToken (client);
            return;
        }

        // This client opens a loot sack with the use request, naming the sack instead of an item
        if (DbConnection.ItemContainers.FindById ((int) itemId) is { } bag)
        {
            foreach (var packet in bag.GetContentsPackets ())
            {
                clientConnection.MaybeScheduleNetworkPacketSend (packet);
            }

            Log (itemId, "loot bag", $"opened, {bag.Contents.Count} item(s)");
            return;
        }

        var character = clientConnection.GetSelectedCharacter ();
        var item = DbConnection.Items.FindById ((int) itemId);
        if (character is null || item is null)
        {
            Log (itemId, "?", "no character or no such item");
            return;
        }

        var name = item.Localization.GetValueOrDefault (Locale.Russian, "?");
        if (!character.Items.Any (x => x.Value == item.Id))
        {
            // A dragged item never reaches the server when the client's own cell checks refuse it,
            // so using a dropped item picks it up
            if (item.DroppedOnGroundBy is not null)
            {
                Log (itemId, name, PickupItemHandler.PickUpFromGround (client, clientConnection, item)
                    ? "picked up off the ground"
                    : "not picked up");
                return;
            }

            if (item.ParentBackpackId is not null)
            {
                Log (itemId, name, Backpack.TakeOut (clientConnection, item, null)
                    ? "taken out of the backpack"
                    : "left in the backpack");
                return;
            }

            Log (itemId, name, "not carried");
            return;
        }

        if (item.WireObjectType == ObjectType.Token_Tutorial_Torweal)
        {
            var identity = SphServer.Helpers.Networking.GameplayRecord.ReadIdentity (frame);
            var targetBit = 165 + (identity.PositionFlag ? 45 : 0);
            if (frame.Length * 8 >= targetBit + 24)
            {
                var stream = new BitStreams.BitStream (frame);
                stream.ReadBits (targetBit);
                NewPlayerDungeon.UseExitToken (client, item, (ushort) stream.ReadUInt32 (24));
            }
            return;
        }

        if (FoodSatiety.TryGetValue (item.WireObjectType, out var satiety))
        {
            Eat (character, item, satiety, itemId, name);
            return;
        }

        var from = character.Items.First (x => x.Value == item.Id).Key;

        // The client opens the worn backpack itself and then reports the use, so the bag stays worn
        if (from == BelongingSlot.Backpack && Backpack.IsBackpack (item))
        {
            clientConnection.MaybeScheduleNetworkPacketSend (Backpack.BuildCellsPacket (item));
            Log (itemId, name, $"backpack opened, {item.BackpackContents.Count} item(s)");
            return;
        }

        var wearing = !ItemDbEntry.IsInventorySlot (from);
        var to = wearing ? character.FindEmptyInventorySlot () : WearSlotFor (character, item);
        if (to is null)
        {
            Log (itemId, name, wearing ? "inventory is full" : "nowhere to wear it");
            return;
        }

        // The occupant of the destination takes the slot this item is leaving
        var displaced = character.Items.TryGetValue (to.Value, out var occupant) ? occupant : (int?) null;

        var lookBefore = CharacterWornLook.Capture (character);
        character.PlaceItemInSlot (to.Value, item.Id);

        // The source slot still has a handle: the swap, or an explicit clear
        byte[]? vacated;
        if (displaced is { } displacedId)
        {
            character.PlaceItemInSlot (from, displacedId);
            vacated = ItemSlotReserve.Build (localId, from, displacedId);
        }
        else
        {
            vacated = ItemSlotReserve.Build (localId, from, ItemSlotReserve.NoItem);
        }

        if (vacated is not null)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (vacated);
        }

        var claimed = ItemSlotReserve.Build (localId, to.Value, item.Id, item.ItemCount);
        if (claimed is not null)
        {
            clientConnection.MaybeScheduleNetworkPacketSend (claimed);
        }

        if (to == BelongingSlot.Backpack && Backpack.IsBackpack (item))
        {
            clientConnection.MaybeScheduleNetworkPacketSend (Backpack.BuildCellsPacket (item));
        }

        if (character.RecalcCurrentStats ())
        {
            NetworkedStatsUpdater.Update (character);
        }

        clientConnection.SaveSelectedCharacter ();

        if (lookBefore != CharacterWornLook.Capture (character))
        {
            ActiveClients.Get (localId)?.BroadcastAppearanceRefreshToVisibleClients ();
        }

        Log (itemId, name, displaced is null
            ? $"{Enum.GetName (from)} -> {Enum.GetName (to.Value)}"
            : $"{Enum.GetName (from)} <-> {Enum.GetName (to.Value)}");
    }

    /// <summary>
    /// Satiety per bite out of the usual 100. No original values survived, so they follow the price.
    /// Raw fish (Food_Fish) never sends a use: its script refuses to be eaten
    /// </summary>
    private static readonly Dictionary<ObjectType, int> FoodSatiety = new ()
    {
        [ObjectType.Food_Apple] = 10,
        [ObjectType.Food_Pear] = 10,
        [ObjectType.Food_Bread] = 20,
        [ObjectType.Food_Fish2] = 25,
        [ObjectType.Food_Meat] = 30
    };

    // One double click in the bag sends the same use twice ~250 ms apart
    private const long RepeatedUseMs = 400;
    private int lastEatenItemId = -1;
    private long lastEatenAt;

    /// <summary>
    /// The food script prints "you ate" on its own and only waits for the new count; satiety reaches
    /// the client through the stats
    /// </summary>
    private void Eat (CharacterDbEntry character, ItemDbEntry item, int satiety, ushort itemId, string name)
    {
        var now = Environment.TickCount64;
        if (item.Id == lastEatenItemId && now - lastEatenAt < RepeatedUseMs)
        {
            Log (itemId, name, "repeat of the same click");
            return;
        }

        lastEatenItemId = item.Id;
        lastEatenAt = now;

        character.CurrentSatiety = (ushort) Math.Min (character.CurrentSatiety + satiety, character.MaxSatiety);
        NetworkedStatsUpdater.Update (character);
        ItemConsumption.ConsumeOne (client, item);
        clientConnection.SaveSelectedCharacter ();

        Log (itemId, name, $"eaten, satiety {character.CurrentSatiety}/{character.MaxSatiety}");
    }

    private static BelongingSlot? WearSlotFor (CharacterDbEntry character, ItemDbEntry item)
    {
        BelongingSlot? fallback = null;

        foreach (var slot in Enum.GetValues<BelongingSlot> ())
        {
            if (ItemDbEntry.IsInventorySlot (slot) || !item.IsValidForSlot (slot))
            {
                continue;
            }

            // Empty legal slot wins; an occupied one is only the fallback
            if (!character.Items.ContainsKey (slot))
            {
                return slot;
            }

            fallback ??= slot;
        }

        return fallback;
    }

    private void Log (ushort itemId, string name, string outcome)
    {
        SphLogger.Info ($"UseItem: Source [{localId:X4}] - Target [{itemId:X4}] - Item [{name}] - [{outcome}]");
    }
}
