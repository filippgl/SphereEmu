using System.Reflection;
using System.Runtime.CompilerServices;
using BitStreams;
using LiteDB;
using SphereHelpers.Extensions;
using SphServer.Client;
using SphServer.Client.Networking.Handlers.InGame.Bank;
using SphServer.Client.Networking.Handlers.InGame.Items;
using SphServer.Helpers.Networking;
using SphServer.Shared.Db;
using SphServer.Shared.Db.DataModels;
using static SphServer.Shared.BitStream.SphBitStream;

internal static class GameplayChecks
{
    internal static void Run (Action<bool, string> check)
    {
        using var memory = new MemoryStream ();
        using var db = new LiteDatabase (memory);
        SetDb ("Db", db);
        SetDb ("Characters", db.GetCollection<CharacterDbEntry> ("Characters"));
        SetDb ("Items", db.GetCollection<ItemDbEntry> ("Items"));
        // Packet sinks skip TCP; the handlers and character persistence use an isolated database
        var client = (SphereClient) RuntimeHelpers.GetUninitializedObject (typeof (SphereClient));
        GC.SuppressFinalize (client);
        typeof (SphereClient).GetProperty ("IsAdminDebugDummy")!.SetValue (client, true);
        var character = new CharacterDbEntry { Id = 1, Name = "Checks", ClientIndex = 0x4F70, Money = 10000 };
        client.CurrentCharacter = character;
        DbConnection.Characters.Insert (character);
        var bank = new BankHandler (character.ClientIndex, client);
        bank.Declare ();
        var id = (ushort) typeof (BankHandler).GetField ("bankId", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue (bank)!;
        void Request (byte operation, uint amount, bool position = false, ushort? target = null, byte length = 5)
        {
            var stream = GetWriteBitStream ();
            for (var bit = 0; bit < ClientFrame.BodyOffset * 8; bit++) stream.WriteByte (0, 1);
            stream.WriteByte (position ? (byte) 1 : (byte) 0, 1);
            if (position) for (var bit = 0; bit < 45; bit++) stream.WriteByte (0, 1);
            stream.WriteUInt16 (0, 15);
            stream.WriteUInt32 (target ?? id, 18);
            stream.WriteUInt16 (GameplayRecord.SubjectTypeBank, 12);
            stream.WriteByte (13, 7);
            stream.WriteByte (15, 5);
            stream.WriteByte (length, 8);
            stream.WriteByte (operation, 8);
            foreach (var value in BitConverter.GetBytes (amount)) stream.WriteByte (value, 8);
            bank.Handle (stream.GetStreamData (), 0).GetAwaiter ().GetResult ();
        }
        Request (0, 3500);
        check (character.Money == 6500 && character.BankMoney == 3500, "Bank deposit conserves money");
        Request (1, 500, true);
        check (character.Money == 7000 && character.BankMoney == 3000, "Bank withdrawal accepts position prefix");
        var reloaded = DbConnection.Characters.FindById (1);
        check (reloaded.Money == 7000 && reloaded.BankMoney == 3000, "Bank balance survives character reload");
        Request (0, 7001);
        Request (1, 3001);
        Request (0, 0);
        Request (1, uint.MaxValue);
        Request (0, 1, target: (ushort) (id + 1));
        Request (0, 1, length: 250);
        check (character.Money == 7000 && character.BankMoney == 3000, "Invalid bank transfers leave both balances intact");
        character.Money = 1999999999;
        Request (1, 2);
        check (character.Money == 1999999999 && character.BankMoney == 3000, "Bank enforces client cash limit");
        character.BankMoney = long.MaxValue;
        Request (0, 1);
        check (character.Money == 1999999999 && character.BankMoney == long.MaxValue, "Bank refuses account overflow");
        bank.Release ();

        ItemDbEntry Item (int itemId, int count, int game = 601) => new ()
        { Id = itemId, GameId = game, ObjectType = ObjectType.Powder_Single_Target, ItemCount = count };
        var source = Item (30001, 12);
        var targetStack = Item (30002, 20);
        DbConnection.Items.Insert (source);
        DbConnection.Items.Insert (targetStack);
        character.Items[BelongingSlot.Inventory_1] = source.Id;
        character.Items[BelongingSlot.Inventory_2] = targetStack.Id;
        check (StackItemsHandler.Merge (client, source, targetStack, 5), "Partial stack merge succeeds");
        check (DbConnection.Items.FindById (source.Id).ItemCount == 7 && DbConnection.Items.FindById (targetStack.Id).ItemCount == 25,
            "Partial merge persists both quantities");
        check (StackItemsHandler.Merge (client, source, targetStack, 0), "Whole stack merge succeeds");
        check (!DbConnection.Items.Exists (x => x.Id == source.Id) && !character.Items.ContainsValue (source.Id) && targetStack.ItemCount == 32,
            "Whole merge removes source row and source slot");
        var bag = new ItemDbEntry { Id = 30003, ObjectType = ObjectType.Backpack_Small, ItemCount = 1 };
        source = Item (30004, 10);
        source.ParentBackpackId = bag.Id;
        bag.BackpackContents[2] = source.Id;
        DbConnection.Items.Insert (source);
        DbConnection.Items.Insert (bag);
        character.Items[BelongingSlot.Backpack] = bag.Id;
        targetStack.ItemCount = 253;
        DbConnection.Items.Update (targetStack);
        check (StackItemsHandler.Merge (client, source, targetStack, 8), "Backpack to inventory merge succeeds");
        check (source.ItemCount == 8 && targetStack.ItemCount == 255, "Merge caps target and preserves remainder");
        check (!StackItemsHandler.Merge (client, source, targetStack, 1), "Full target refuses merge");
        targetStack.ItemCount = 1;
        DbConnection.Items.Update (targetStack);
        check (StackItemsHandler.Merge (client, source, targetStack, 0), "Whole backpack stack merge succeeds");
        check (!DbConnection.Items.FindById (bag.Id).BackpackContents.ContainsKey (2), "Whole merge clears backpack cell");
        source = Item (30005, 4, game: 602);
        DbConnection.Items.Insert (source);
        character.Items[BelongingSlot.Inventory_1] = source.Id;
        check (!StackItemsHandler.Merge (client, source, targetStack, 1), "Different item refuses merge");
        source.GameId = targetStack.GameId;
        character.Items.Remove (BelongingSlot.Inventory_1);
        check (!StackItemsHandler.Merge (client, source, targetStack, 1), "Unowned stack refuses merge");
        source.GameId = targetStack.GameId;
        DbConnection.Items.Update (source);
        character.Items[BelongingSlot.Inventory_1] = source.Id;
        byte[] StackRequest (bool position, ushort player)
        {
            var stream = GetWriteBitStream ();
            for (var bit = 0; bit < ClientFrame.BodyOffset * 8; bit++) stream.WriteByte (0, 1);
            stream.WriteByte (position ? (byte) 1 : (byte) 0, 1);
            if (position) for (var bit = 0; bit < 45; bit++) stream.WriteByte (0, 1);
            stream.WriteUInt16 (0, 15);
            stream.WriteUInt32 ((uint) source.Id, 18);
            stream.WriteUInt16 ((ushort) source.WireObjectType, 12);
            stream.WriteByte (GameplayRecord.TagObjectInteract, 7);
            stream.WriteByte (GameplayRecord.ObjectUseCommand, 8);
            stream.WriteByte (GameplayRecord.ObjectUseOnTargetLength, 8);
            stream.WriteUInt32 (player, 24);
            stream.WriteUInt32 ((uint) targetStack.Id, 24);
            stream.WriteUInt32 (1, 24);
            return stream.GetStreamData ();
        }
        var stackHandler = new StackItemsHandler (character.ClientIndex, client);
        stackHandler.Handle (StackRequest (false, ByteSwap (character.ClientIndex)), 0).GetAwaiter ().GetResult ();
        check (DbConnection.Items.FindById (source.Id).ItemCount == 3, "Stack handler reads source id without position prefix");
        stackHandler.Handle (StackRequest (true, ByteSwap (character.ClientIndex)), 0).GetAwaiter ().GetResult ();
        check (DbConnection.Items.FindById (source.Id).ItemCount == 2, "Stack handler reads source id with position prefix");
        stackHandler.Handle (StackRequest (true, 1), 0).GetAwaiter ().GetResult ();
        check (DbConnection.Items.FindById (source.Id).ItemCount == 2, "Stack handler rejects another player id");

        var food = new ItemDbEntry { Id = 30006, GameId = 10, ObjectType = ObjectType.Food_Apple, ItemCount = 2 };
        DbConnection.Items.Insert (food);
        character.Items[BelongingSlot.Inventory_3] = food.Id;
        character.CurrentSatiety = 95;
        character.MaxSatiety = 100;
        var connection = new SphServer.Client.Networking.ClientConnection (null!, character.ClientIndex, client);
        var foodHandler = new UseItemHandler (character.ClientIndex, connection, client);
        typeof (UseItemHandler).GetMethod ("Eat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke (foodHandler, [character, food, 10, (ushort) food.Id, "apple"]);
        check (character.CurrentSatiety == 100 && DbConnection.Items.FindById (food.Id).ItemCount == 1,
            "Food raises satiety to its cap and consumes one item");
        typeof (UseItemHandler).GetMethod ("Eat", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke (foodHandler, [character, food, 10, (ushort) food.Id, "apple"]);
        check (DbConnection.Items.FindById (food.Id).ItemCount == 1, "A repeated food click consumes once");
        var tavern = SphServer.Sphere.Game.NpcTrade.ItemsOnSale.ItemsOnSaleGenerator.Tavernkeeper ();
        check (tavern.Count == 5 && tavern.All (x => x.VendorCost > 0 && x.ItemCount > 0), "Tavern has five stocked foods");
        check (tavern.All (x => x.WireObjectType is ObjectType.Food_Apple or ObjectType.Food_Pear or
            ObjectType.Food_Bread or ObjectType.Food_Fish2 or ObjectType.Food_Meat), "Tavern excludes raw fish and weapons");
        var weapons = SphServer.Sphere.Game.NpcTrade.ItemsOnSale.ItemsOnSaleGenerator.Weapons (4, 6);
        check (weapons.Any (x => x.WireObjectType != ObjectType.Arrow) &&
            weapons.Where (x => x.WireObjectType != ObjectType.Arrow).All (x => x.Tier is >= 4 and <= 6),
            "Weapons stay in the vendor's tier range");
    }

    private static void SetDb (string name, object value) => typeof (DbConnection).GetProperty (name)!.SetValue (null, value);
}
