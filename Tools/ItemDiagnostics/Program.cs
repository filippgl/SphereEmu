using System.Text.RegularExpressions;
using SphServer.Helpers.Networking;

var decoder = MbcProtocolDecoder.CreateDefault ();
foreach (var line in File.ReadLines (args[0]))
{
    var match = Regex.Match (line, @"\[OUT\] CLI 4F70 ([0-9A-F]+)$");
    if (!match.Success) continue;
    foreach (var frame in MbcProtocolDecoder.SplitTcpFrames (Convert.FromHexString (match.Groups[1].Value), MbcDirection.Server))
    {
        if (frame.Message != 300) continue;
        var result = decoder.DecodeGame (frame.Payload, MbcDirection.Server);
        if (line.Contains("20:10:53") || (result.ModuleTag == 407 && line.Contains("20:10:55")))
        {
            Console.WriteLine (line[..25] + $" {result.ProcessId:X4} tag={result.ModuleTag} status={result.Status} overread={result.OverreadBits} error={result.Error}");
            foreach (var evt in result.Events)
            {
                Console.WriteLine ($"  r{evt.Region} cmd={evt.Command} " + string.Join (", ", evt.Fields.Select (f => $"{f.Name}:{f.IntValue}:{f.DoubleValue}")));
            }
        }
    }
}
