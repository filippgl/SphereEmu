using System.Reflection;
using System.Text.RegularExpressions;
using SphServer.Helpers.Networking;
using SphServer.Packets;
using SphServer.Shared.Db.DataModels;

var decoder = MbcProtocolDecoder.CreateDefault ();
var checks = 0;
void Check (bool condition, string message)
{
    if (!condition) throw new Exception (message);
    checks++;
}

List<MbcDecodedEvent> Decode (byte[] bytes)
{
    var frames = MbcProtocolDecoder.SplitTcpFrames (bytes, MbcDirection.Server);
    Check (frames.Count > 0 && frames.Sum (f => f.Size) == bytes.Length, "TCP frame boundaries");
    var events = new List<MbcDecodedEvent> ();
    foreach (var frame in frames)
    {
        var result = decoder.DecodeGame (frame.Payload, MbcDirection.Server);
        Check (result.Status == "full" && result.OverreadBits == 0,
            $"Packet parse: {result.Status}, {result.Error}");
        events.AddRange (result.Events);
    }
    return events;
}

long? Field (MbcDecodedEvent evt, string name) => evt.Fields.Single (f => f.Name == name).IntValue;

if (args.Length > 0)
{
    var unlocks = 0;
    foreach (var line in File.ReadLines (args[0]))
    {
        var match = Regex.Match (line, @"\[OUT\] CLI 4F77 ([0-9A-F]+)$");
        if (!match.Success) continue;
        foreach (var frame in MbcProtocolDecoder.SplitTcpFrames (
                     Convert.FromHexString (match.Groups[1].Value), MbcDirection.Server))
        {
            if (frame.Message != 300) continue;
            var result = decoder.DecodeGame (frame.Payload, MbcDirection.Server);
            unlocks += result.Events.Count (e => e.Module == "_player" && e.Region == 4 && e.Command == 3);
        }
    }
    Console.WriteLine ($"Player inventory lock updates: {unlocks}");
    return;
}

try
{
var sword = new ItemDbEntry { Id = 20001, GameId = 1, ObjectType = (ObjectType) 500, ItemCount = 1, Weight = 1000 };
var powder = new ItemDbEntry { Id = 20002, GameId = 601, ObjectType = (ObjectType) 453, ItemCount = 7, Weight = 250 };

var unlock = Decode (ItemSlotReserve.BuildUnlock (0x4F77)).Single ();
Check (unlock.ProcessId == 0x774F && unlock.Module == "_player" && unlock.Command == 3 &&
       Field (unlock, "clear_flag") == 0, "Player inventory must unlock without clearing its slots");

foreach (var item in new[] { sword, powder })
{
    var spawn = Decode (ItemRecordEncoder.Encode (item, 0x774F));
    Check (spawn.Any (e => e.Region == 61), "Item process must be declared");
    Check (Field (spawn.Single (e => e.Command == 0), "container_id") == 0x774F, "Item parent");
    var pickup = Decode (ItemRecordEncoder.EncodePutInto (item, 0x774F));
    Check (pickup.All (e => e.Region != 61), "Pickup must preserve the existing item process");
    Check (pickup.Single (e => e.Region == 2).Fields.Last ().IntValue == 2, "Contained placement state");
    Check (Field (pickup.Single (e => e.Command == 0), "container_id") == 0x774F, "Pickup parent");
    Decode (ItemRecordEncoder.EncodeDropToGround (item, 5, -7, 9, 3));
    Decode (ItemRecordEncoder.EncodeOnGround (item, 5, -7, 9, 3));
}

var bag = new ItemContainerDbEntry { Id = 1234, Contents = { [0] = sword.Id, [3] = powder.Id } };
var buildList = typeof (ItemContainerDbEntry).GetMethod ("BuildItemListPacket", BindingFlags.Instance | BindingFlags.NonPublic)!;
var bagEvents = Decode ((byte[]) buildList.Invoke (bag, [new List<ItemDbEntry> { sword, powder }])!);
var slots = bagEvents.Where (e => e.Command == 2).ToDictionary (e => (int) Field (e, "slot")!);
Check (slots.Count == 4, "A sack refresh must also clear stale slots");
Check (Field (slots[0], "item_id") == sword.Id && Field (slots[3], "item_id") == powder.Id,
    "Remaining sack items must keep their original slots");
Check (slots[1].Fields.All (f => f.Name != "item_id") && slots[2].Fields.All (f => f.Name != "item_id"),
    "Empty sack slots must stay empty");
Check (Field (slots[3], "count") == 1750, "Sack slot word is the total stack weight");
Decode (bag.BuildClearSlotPacket (3));

foreach (var count in new[] { 1, 255, 256, 65535, 65536 })
{
    var slot = Decode (ItemSlotReserve.Build (0x4F77, BelongingSlot.Inventory_1, sword.Id, count)!).Single ();
    Check (Field (slot, "item_id") == sword.Id && Field (slot, "count") == count,
        $"Slot values must retain 32 bits: {count}");
}
var cleared = Decode (ItemSlotReserve.Build (0x4F77, BelongingSlot.Inventory_1, ItemSlotReserve.NoItem)!).Single ();
Check (Field (cleared, "count") == 0, "An empty slot must contribute no weight");
var move = Decode (ItemSlotReserve.BuildMove (0x4F77, BelongingSlot.Backpack, BelongingSlot.Special_4, sword.Id));
Check (Field (move[0], "slot") == 22 && Field (move[1], "slot") == 49, "Move must use wire slot ids");

foreach (var type in new[] { ObjectType.Mantra_White, ObjectType.Mantra_Black })
{
    var mantra = new ItemDbEntry { Id = 20003, GameId = 1, ObjectType = type, ItemCount = 3 };
    foreach (var packet in new[] {
                 ItemRecordEncoder.Encode (mantra, 0x774F),
                 ItemRecordEncoder.EncodeInLootBag (mantra),
                 ItemRecordEncoder.EncodeOnGround (mantra, 5, -7, 9, 3) })
    {
        var events = Decode (packet);
        Check (events.Any (e => e.Region == 61), "Mantra must be declared");
        Check (events.All (e => e.Command != 9), "Mantra weight must not receive the weapon ownership payload");
        Check (events.Any (e => e.Command == 12), "Mantra amount must be initialized");
    }
    Decode (ItemRecordEncoder.EncodePutInto (mantra, 0x774F));
    Decode (ItemRecordEncoder.EncodeDropToGround (mantra, 5, -7, 9, 3));
}

foreach (var type in new[] { ObjectType.Sack, ObjectType.Recipe_Book, ObjectType.Mantra_Book_Large,
             ObjectType.Mantra_Book_Great, ObjectType.Blueprint, ObjectType.Alchemy_Furnace })
{
    var item = new ItemDbEntry { Id = 20006, GameId = 1, ObjectType = type, ItemCount = 1 };
    var inventory = Decode (ItemRecordEncoder.Encode (item, 0x774F));
    Check (inventory.All (e => e.Command != 9), $"Preserve native weight for {type}");
    Decode (ItemRecordEncoder.EncodeOnGround (item, 5, -7, 9, 3));
    Decode (ItemRecordEncoder.EncodePutInto (item, 0x774F));
}
var token = new ItemDbEntry { Id = 20007, ObjectType = ObjectType.Token_Tutorial_Torweal, ItemCount = 1 };
var tokenEvents = Decode (ItemRecordEncoder.Encode (token, 0x774F));
Check (tokenEvents.Any (e => e.Command == 15), "Tutorial token must receive its destination setup");
Check (tokenEvents.Any (e => e.Command == 0), "Tutorial token must acquire its inventory parent");
Decode (ItemRecordEncoder.EncodeOnGround (token, 5, -7, 9, 3));
var sackType = typeof (ItemRecordEncoder).Assembly.GetType ("SphServer.Sphere.Game.WorldObject.LootBag")!;
var spawnSack = sackType.GetMethod ("BuildLiveSpawn", BindingFlags.Static | BindingFlags.NonPublic)!;
Decode ((byte[]) spawnSack.Invoke (null, new object[] { (ushort) 1234, 5f, -7f, 9f, (byte) 3 })!);
foreach (var deleted in new[] { true, false })
{
    var reply = SphServer.Shared.Networking.CommonPackets.DeleteCharacterResult (0x4F77, deleted);
    var evt = Decode (reply).Single ();
    Check (evt.ProcessId == 0x774F && evt.Region == 12 && evt.Command == 42,
        "Delete result must reach the native character selection handler");
    var resultReader = new BitStreams.BitStream (reply);
    resultReader.ReadBits (109);
    Check (resultReader.ReadUInt32 () == (deleted ? 0u : 1u), "Native delete result is a full u32");
}
var activeType = typeof (ItemRecordEncoder).Assembly.GetType (
    "SphServer.Client.Networking.Handlers.InGame.Mutator.ActiveMutators")!;
var setup = activeType.GetMethod ("BuildSetup", BindingFlags.NonPublic | BindingFlags.Static)!;
var setupPacket = (byte[]) setup.Invoke (null, new object[] { (ushort) 20008, (ushort) 1036, 1200, (ushort) 0x774F })!;
Decode (setupPacket);
var reader = new BitStreams.BitStream (setupPacket);
reader.ReadBits (109);
Check (reader.ReadUInt16 () == 1036, "Status setup must contain the Shackles mutator id");
Check (reader.ReadByte () == 0, "Status timing mode");
Check (reader.ReadUInt16 () == 1200, "Shackles lifetime must retain all 16 bits");
Check (reader.ReadUInt16 () == 0x774F, "Status caster id");
using (var memory = new MemoryStream ())
using (var database = new LiteDB.LiteDatabase (memory))
{
    var items = database.GetCollection<ItemDbEntry> ("Items");
    typeof (SphServer.Shared.Db.DbConnection).GetProperty ("Items")!.SetValue (null, items);
    var buyType = typeof (ItemRecordEncoder).Assembly.GetType (
        "SphServer.Client.Networking.Handlers.InGame.Items.BuyItemFromTargetHandler")!;
    var create = buyType.GetMethod ("CreatePurchasedItem", BindingFlags.NonPublic | BindingFlags.Static)!;
    var stock = new ItemDbEntry { Id = 1234, ObjectType = ObjectType.Powder_Single_Target,
        GameId = 601, ItemCount = 1, ParentContainerId = 2837 };
    var bought = (ItemDbEntry) create.Invoke (null, [stock, 2])!;
    Check (items.FindById (bought.Id).ItemCount == 2, "Purchased stack must persist its actual quantity");
    Check (items.FindById (bought.Id).ParentContainerId is null, "Purchased stack must leave the vendor");
    Check (stock.ItemCount == 1 && stock.ParentContainerId == 2837, "Purchase must preserve vendor stock");
    Check (bought.Id != stock.Id, "Purchase must allocate a distinct item id");
}
Console.WriteLine ($"PASS: {checks} checks, inventory, ground items, both mantra types, sack spawn");
}
catch (Exception error)
{
    Console.Error.WriteLine (error);
    Environment.ExitCode = 1;
}
