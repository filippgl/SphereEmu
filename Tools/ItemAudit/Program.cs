using System.Text.Json;
using SphServer.Helpers.Networking;
using SphServer.Packets;
using SphServer.Shared.Db.DataModels;
var decoder = MbcProtocolDecoder.CreateDefault ();
var failed = new List<string> ();
var checks = 0;
foreach (var row in JsonDocument.Parse (File.ReadAllText ("Tools/_tmp/pickup_audit.json")).RootElement.EnumerateArray ())
{
    var tag = row.GetProperty ("tag").GetInt32 ();
    var module = row.GetProperty ("module").GetString ();
    var item = new ItemDbEntry { Id = 20100, ObjectType = (ObjectType) tag, GameId = 1, ItemCount = 3 };
    foreach (var (kind, packet) in new[] {
                 ("inventory", ItemRecordEncoder.Encode (item, 0x774F)),
                 ("ground", ItemRecordEncoder.EncodeOnGround (item, 1, -2, 3, 0)),
                 ("pickup", ItemRecordEncoder.EncodePutInto (item, 0x774F)),
                 ("drop", ItemRecordEncoder.EncodeDropToGround (item, 1, -2, 3, 0)) })
    {
        foreach (var frame in MbcProtocolDecoder.SplitTcpFrames (packet, MbcDirection.Server))
        {
            var result = decoder.DecodeGame (frame.Payload, MbcDirection.Server);
            checks++;
            if (result.Status != "full" || result.OverreadBits != 0)
                failed.Add ($"{module} ({tag}) {kind}: {result.Status}, overread={result.OverreadBits}, {result.Error}");
        }
    }
}
Console.WriteLine ($"Audited {checks} frames, {failed.Count} failures");
foreach (var failure in failed) Console.WriteLine (failure);
Environment.ExitCode = failed.Count == 0 ? 0 : 1;
