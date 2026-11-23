using Strela.TestServer;

// Ручной запуск для бенчмарков: два детерминированных файла на фиксированном порту.
const int port = 8787;

await using var server = await TestFileServer.StartAsync(port);

var small = server.AddFile(
    "test-64mb.bin",
    CreateContent(64 * 1024 * 1024, seed: 64),
    new ServeOptions(BytesPerSecondPerConnection: 1_000_000)); // 1 МБ/с на соединение

var large = server.AddFile(
    "test-256mb.bin",
    CreateContent(256 * 1024 * 1024, seed: 256)); // без лимита

Console.WriteLine($"test-64mb.bin  (1 MB/s per connection): {small}");
Console.WriteLine($"test-256mb.bin (no limit):               {large}");
Console.WriteLine("Ctrl+C to stop.");

using var stop = new ManualResetEventSlim();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Set();
};
stop.Wait();

static byte[] CreateContent(int size, int seed)
{
    var data = new byte[size];
    new Random(seed).NextBytes(data);
    return data;
}
