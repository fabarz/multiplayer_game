using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// ---------- Shared data model ----------
class PlayerInfo
{
    public int Id { get; set; }
    public string Color { get; set; } = "";
    public string Shape { get; set; } = "";
    public float X { get; set; }
    public float Y { get; set; }
}

class ConnectedClient
{
    public required TcpClient TcpClient { get; set; }
    public required NetworkStream Stream { get; set; }
    public required PlayerInfo Info { get; set; }
}

class Program
{
    static readonly string[] Colors = { "Red", "Blue", "Green", "Orange", "Purple", "Teal", "Magenta", "Gold" };
    static readonly string[] Shapes = { "Circle", "Square", "Triangle", "Diamond", "Star" };

    static readonly Dictionary<int, ConnectedClient> Clients = new();
    static readonly object Lock = new();
    static int nextId = 1;

    const int Port = 5000;
    const int FieldWidth = 800;
    const int FieldHeight = 600;

    static async Task Main()
    {
        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Console.WriteLine($"Server listening on port {Port}. Waiting for players...");

        _ = Task.Run(BroadcastLoop);

        while (true)
        {
            TcpClient tcpClient = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => HandleClient(tcpClient));
        }
    }

    static async Task HandleClient(TcpClient tcpClient)
    {
        NetworkStream stream = tcpClient.GetStream();
        int id;
        PlayerInfo info;

        lock (Lock)
        {
            id = nextId++;
            info = new PlayerInfo
            {
                Id = id,
                Color = Colors[(id - 1) % Colors.Length],
                Shape = Shapes[(id - 1) % Shapes.Length],
                X = FieldWidth / 2f,
                Y = FieldHeight / 2f
            };
            Clients[id] = new ConnectedClient { TcpClient = tcpClient, Stream = stream, Info = info };
        }

        Console.WriteLine($"Player {id} connected -> {info.Color} {info.Shape}");

        string initMsg = JsonSerializer.Serialize(new { Type = "init", Info = info, FieldWidth, FieldHeight });
        await SendLine(stream, initMsg);

        var reader = new StreamReader(stream, Encoding.UTF8);
        try
        {
            while (true)
            {
                string? line = await reader.ReadLineAsync();
                if (line == null) break;
                HandleMessage(id, line);
            }
        }
        catch
        {
            // client disconnected abruptly
        }
        finally
        {
            lock (Lock) { Clients.Remove(id); }
            Console.WriteLine($"Player {id} disconnected");
            tcpClient.Close();
        }
    }

    static void HandleMessage(int id, string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = root.GetProperty("Type").GetString() ?? "";

            if (type == "move")
            {
                float dx = root.GetProperty("Dx").GetSingle();
                float dy = root.GetProperty("Dy").GetSingle();
                lock (Lock)
                {
                    if (Clients.TryGetValue(id, out var cc))
                    {
                        cc.Info.X = Math.Clamp(cc.Info.X + dx, 0, FieldWidth);
                        cc.Info.Y = Math.Clamp(cc.Info.Y + dy, 0, FieldHeight);
                    }
                }
            }
        }
        catch
        {
            // ignore malformed message
        }
    }

    static async Task BroadcastLoop()
    {
        while (true)
        {
            await Task.Delay(40); // ~25 updates/sec

            List<PlayerInfo> snapshot;
            List<ConnectedClient> targets;
            lock (Lock)
            {
                snapshot = Clients.Values.Select(c => c.Info).ToList();
                targets = Clients.Values.ToList();
            }
            if (snapshot.Count == 0) continue;

            string msg = JsonSerializer.Serialize(new { Type = "state", Players = snapshot });
            foreach (var cc in targets)
            {
                try { await SendLine(cc.Stream, msg); }
                catch { /* client will be cleaned up by its own handler */ }
            }
        }
    }

    static async Task SendLine(NetworkStream stream, string text)
    {
        byte[] data = Encoding.UTF8.GetBytes(text + "\n");
        await stream.WriteAsync(data);
    }
}
