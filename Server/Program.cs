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
    public int Health { get; set; }
}

class BulletInfo
{
    public int Id { get; set; }
    public int OwnerId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Dx { get; set; }
    public float Dy { get; set; }
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
    static readonly Dictionary<int, BulletInfo> Bullets = new();
    static readonly object Lock = new();
    static int nextId = 1;
    static int nextBulletId = 1;

    const int Port = 5000;
    const int FieldWidth = 800;
    const int FieldHeight = 600;
    const int MaxHealth = 100;
    const int DamagePerHit = 25;
    const float BulletSpeed = 15f;
    const float HitRadius = 20f;

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
                Y = FieldHeight / 2f,
                Health = MaxHealth
            };
            Clients[id] = new ConnectedClient { TcpClient = tcpClient, Stream = stream, Info = info };
        }

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
            else if (type == "shoot")
            {
                float targetX = root.GetProperty("TargetX").GetSingle();
                float targetY = root.GetProperty("TargetY").GetSingle();
                lock (Lock)
                {
                    if (Clients.TryGetValue(id, out var cc))
                    {
                        float startX = cc.Info.X;
                        float startY = cc.Info.Y;
                        float dx = targetX - startX;
                        float dy = targetY - startY;
                        float length = MathF.Max(1f, MathF.Sqrt(dx * dx + dy * dy));
                        float normX = dx / length;
                        float normY = dy / length;
                        Bullets[nextBulletId] = new BulletInfo
                        {
                            Id = nextBulletId,
                            OwnerId = id,
                            X = startX,
                            Y = startY,
                            Dx = normX * BulletSpeed,
                            Dy = normY * BulletSpeed
                        };
                        Console.WriteLine($"Player {id} shot bullet {nextBulletId} from ({startX:F0},{startY:F0}) to ({targetX:F0},{targetY:F0})");
                        nextBulletId++;
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
            List<BulletInfo> bulletsSnapshot;
            List<ConnectedClient> targets;
            lock (Lock)
            {
                snapshot = Clients.Values.Select(c => c.Info).ToList();
                bulletsSnapshot = Bullets.Values.ToList();
                targets = Clients.Values.ToList();

                var bulletsToRemove = new List<int>();
                foreach (var bullet in Bullets.Values)
                {
                    bullet.X += bullet.Dx;
                    bullet.Y += bullet.Dy;

                    if (bullet.X < 0 || bullet.X > FieldWidth || bullet.Y < 0 || bullet.Y > FieldHeight)
                    {
                        bulletsToRemove.Add(bullet.Id);
                        continue;
                    }

                    foreach (var player in Clients.Values)
                    {
                        if (player.Info.Id == bullet.OwnerId) continue;
                        float diffX = player.Info.X - bullet.X;
                        float diffY = player.Info.Y - bullet.Y;
                        if (diffX * diffX + diffY * diffY <= HitRadius * HitRadius)
                        {
                            bulletsToRemove.Add(bullet.Id);
                            player.Info.Health = Math.Max(0, player.Info.Health - DamagePerHit);
                            if (player.Info.Health <= 0)
                            {
                                player.Info.X = FieldWidth / 2f;
                                player.Info.Y = FieldHeight / 2f;
                                player.Info.Health = MaxHealth;
                            }
                            break;
                        }
                    }
                }

                foreach (var bulletId in bulletsToRemove)
                    Bullets.Remove(bulletId);
                bulletsSnapshot = Bullets.Values.ToList();
            }

            if (snapshot.Count == 0) continue;

            string msg = JsonSerializer.Serialize(new { Type = "state", Players = snapshot, Bullets = bulletsSnapshot });
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
