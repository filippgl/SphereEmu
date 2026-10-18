using Godot;
using SphServer.Client;
using SphServer.Shared.Db.DataModels;
using SphServer.Shared.GameData.Enums;
using SphServer.Shared.Logger;
using SphServer.Shared.WorldState;

namespace SphServer.Sphere.Game.WorldObject;

/// <summary>
/// Ground sack whose wire id is its container's DB id, so the open request names the container
/// directly
/// </summary>
public partial class LootBag : WorldObject
{
    /// <summary>
    /// Unclaimed loot goes away with its contents, as on live
    /// </summary>
    private const double LifetimeSeconds = 300;

    public ItemContainerDbEntry Container { get; private set; } = null!;

    private bool _despawned;

    public static LootBag Spawn (ItemContainerDbEntry container, Vector3 godotPosition)
    {
        var bag = new LootBag
        {
            Container = container,
            ID = (ushort) container.Id,
            ObjectType = ObjectType.Sack_Mob_Loot,
            Name = $"LootBag_{container.Id}",
            Angle = CreateRandomSpawnAngle ()
        };
        bag.Position = godotPosition;
        SphServer.Server.SphereServer.ServerNode.CallDeferred (Node.MethodName.AddChild, bag);
        return bag;
    }

    /// <summary>
    /// The id is the container's, already reserved and already in the item records
    /// </summary>
    protected override bool HoldsReservedId => true;

    public static LootBag? Find (int containerId) => ActiveWorldObjects.Get ((ushort) containerId) as LootBag;

    public override void _Ready ()
    {
        base._Ready ();
        if (Engine.IsEditorHint ())
        {
            return;
        }

        GetTree ().CreateTimer (LifetimeSeconds).Timeout += Expire;
        EnsureVisibilityArea ();
    }

    /// <summary>
    /// Live follows the sack with its items' records; a dropping client only lets go of an item
    /// once it sees it parented here
    /// </summary>
    protected override void ShowForClient (SphereClient client)
    {
        if (_despawned)
        {
            return;
        }

        base.ShowForClient (client);
        foreach (var packet in Container.GetItemRecordPackets ())
        {
            client.MaybeQueueNetworkPacketSend (packet);
        }
    }

    /// <summary>
    /// Last item taken: everyone who sees the sack loses it, not only the picker
    /// </summary>
    public void DespawnEmpty ()
    {
        Despawn ();
        ItemContainerDbEntry.Delete (Container, deleteItems: false);
    }

    private void Expire ()
    {
        if (_despawned)
        {
            return;
        }

        SphLogger.Info ($"Loot bag {ID:X4} expired with {Container.Contents.Count} item(s)");
        Despawn ();
        ItemContainerDbEntry.Delete (Container, deleteItems: true);
    }

    private void Despawn ()
    {
        if (_despawned)
        {
            return;
        }

        _despawned = true;
        BroadcastDespawnToVisibleClients ();
        RemoveFromWorldRegistry ();
        WorldObjectIndex.Release (ID);
        QueueFree ();
    }
}
