// Stands in for the Discord client's IPC pipe and "restarts" on a timer, to test that
// iRacingPresence reconnects after Discord goes away, without touching the real Discord.
//
//   dotnet run --project tools/FakeDiscord -- <pipe name> <seconds up> <seconds down> <restarts>
//   iRacingPresence.exe --demo --discord-pipe <pipe name>

using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;

var pipeName = args.Length > 0 ? args[0] : "discord-ipc-fake";
var up = TimeSpan.FromSeconds(args.Length > 1 ? double.Parse(args[1]) : 20);
var down = TimeSpan.FromSeconds(args.Length > 2 ? double.Parse(args[2]) : 5);
int runs = args.Length > 3 ? int.Parse(args[3]) : 2;
var started = DateTime.Now;

void Say(string text) => Console.WriteLine($"[{(DateTime.Now - started).TotalSeconds,5:0.0}s] {text}");

for (int run = 1; run <= runs; run++)
{
    using var stop = new CancellationTokenSource(up);
    var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    Say($"Discord up (run {run})");
    try
    {
        await server.WaitForConnectionAsync(stop.Token);
        Say("client connected");
        var header = new byte[8];
        while (true)
        {
            await server.ReadExactlyAsync(header, stop.Token);
            int op = BinaryPrimitives.ReadInt32LittleEndian(header);
            var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4))];
            await server.ReadExactlyAsync(body, stop.Token);
            var json = Encoding.UTF8.GetString(body);
            Say($"op {op}: {(json.Length > 150 ? json[..150] + "…" : json)}");

            var reply = op == 0
                ? """{"cmd":"DISPATCH","evt":"READY","data":{"v":1,"user":{"username":"fake-user"}}}"""
                : """{"cmd":"SET_ACTIVITY","evt":null,"data":{}}""";
            var bytes = Encoding.UTF8.GetBytes(reply);
            var frame = new byte[8 + bytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, 1);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), bytes.Length);
            bytes.CopyTo(frame, 8);
            await server.WriteAsync(frame, stop.Token);
        }
    }
    catch (Exception e) when (e is OperationCanceledException or IOException or EndOfStreamException)
    {
    }
    server.Dispose();
    Say("Discord down");
    if (run < runs)
        await Task.Delay(down);
}
