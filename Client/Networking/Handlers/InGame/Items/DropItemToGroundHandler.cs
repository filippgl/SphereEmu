using System;
using System.Threading.Tasks;
using SphServer.Shared.Logger;

namespace SphServer.Client.Networking.Handlers.InGame.Items;

public class DropItemToGroundHandler (ushort localId)
    : ISphereClientNetworkingHandler
{
    public async Task Handle (byte[] frame, double delta)
    {
        // TODO: tbd; logged until the drop layout is decoded
        SphLogger.Info ($"Drop to ground: {frame.Length}B {Convert.ToHexString (frame)}. Client ID: {localId:X4}");
    }
}
