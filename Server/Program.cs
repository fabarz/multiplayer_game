using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

class PlayerInfo
{
    public int    Id    { get; set; }
    public string Color { get; set; } = "";
    public string Shape { get; set; } = "";
    public float  X     { get; set; }
    public float  Y     { get; set; }
    public int    Score { get; set; }
}

class BulletInfo
{
    public int   Id      { get; set; }
    public int   OwnerId { get; set; }
    public float X       { get; set; }
    public float Y       { get; set; }
    public float Dx      { get; set; }
    public float Dy      { get; set; }
}

class CoinInfo
{
    public int   Id { get; set; }
    public float X  { get; set; }
    public float Y  { get; set; }
}

class GameServer
{
    const int PORT         = 5000;
    const int FIELD_W      = 800;
    const int FIELD_H      = 600;
    const int PLAYER_SIZE  = 30;
    const int COIN_SIZE    = 14;
    const int BULLET_SPEED = 12;
    const int MAX_COINS    = 8;

    static readonly string[] Colors = { "Red","Blue","Green","Purple","Orange","Teal","Brown","Pink" };
    static readonly string[] Shapes = { "Circle","Square","Triangle","Diamond","Star","Circle","Square","Triangle" };

    static ConcurrentDictionary<int, PlayerInfo>    players = new();
    static ConcurrentDictionary<int, NetworkStream> streams = new();
    static ConcurrentDictionary<int, BulletInfo>   bullets = new();
    static ConcurrentDictionary<int, CoinInfo>     coins   = new();

    static int nextPlayerId = 0;
    static int nextBulletId = 0;
    static int nextCoinId   = 0;

    static readonly Random rng           = new();
    static readonly object broadcastLock = new();

    static void Main()
    {
        var listener = new TcpListener(IPAddress.Any, PORT);
        listener.Start();
        Console.WriteLine($"Server listening on port {PORT}. Waiting for players...");

        for (int i = 0; i < MAX_COINS; i++) SpawnCoin();

        new Thread(GameLoop) { IsBackground = true }.Start();

        while (true)
        {
            var tcp = listener.AcceptTcpClient();
            new Thread(() => HandleClient(tcp)) { IsBackground = true }.Start();
        }
    }

    static void HandleClient(TcpClient tcp)
    {
        int id       = Interlocked.Increment(ref nextPlayerId);
        int colorIdx = (id - 1) % Colors.Length;

        var player = new PlayerInfo
        {
            Id    = id,
            Color = Colors[colorIdx],
            Shape = Shapes[colorIdx],
            X     = 100 + (id * 120) % (FIELD_W - 100),
            Y     = 300,
            Score = 0
        };

        var stream = tcp.GetStream();
        players[id] = player;
        streams[id] = stream;

        SendTo(stream, new
        {
            Type        = "init",
            Info        = player,
            FieldWidth  = FIELD_W,
            FieldHeight = FIELD_H
        });

        BroadcastState();
        BroadcastGameObjects();

        var reader = new System.IO.StreamReader(stream, Encoding.UTF8);
        try
        {
            while (true)
            {
                string? line = reader.ReadLine();
                if (line == null) break;

                using var doc  = JsonDocument.Parse(line);
                var       root = doc.RootElement;
                string    type = root.GetProperty("Type").GetString() ?? "";

                if (type == "move")
                {
                    float dx = root.GetProperty("Dx").GetSingle();
                    float dy = root.GetProperty("Dy").GetSingle();

                    player.X = Math.Clamp(player.X + dx, 0, FIELD_W);
                    player.Y = Math.Clamp(player.Y + dy, 0, FIELD_H);

                    CheckCoinCollisions(player);
                    BroadcastState();
                }
                else if (type == "shoot")
                {
                    float dx  = root.GetProperty("Dx").GetSingle();
                    float dy  = root.GetProperty("Dy").GetSingle();
                    float len = MathF.Sqrt(dx * dx + dy * dy);
                    if (len < 0.01f) { dx = 1; dy = 0; len = 1; }

                    int bid = Interlocked.Increment(ref nextBulletId);
                    bullets[bid] = new BulletInfo
                    {
                        Id      = bid,
                        OwnerId = id,
                        X       = player.X,
                        Y       = player.Y,
                        Dx      = dx / len * BULLET_SPEED,
                        Dy      = dy / len * BULLET_SPEED
                    };
                }
            }
        }
        catch { }
        finally
        {
            Console.WriteLine($"Player {id} disconnected.");
            players.TryRemove(id, out _);
            streams.TryRemove(id, out _);
            tcp.Close();
            BroadcastState();
        }
    }

    static void GameLoop()
    {
        while (true)
        {
            MoveBullets();
            Thread.Sleep(16);
        }
    }

    static void MoveBullets()
    {
        bool changed = false;

        foreach (var (bid, bullet) in bullets)
        {
            bullet.X += bullet.Dx;
            bullet.Y += bullet.Dy;

            if (bullet.X < 0 || bullet.X > FIELD_W ||
                bullet.Y < 0 || bullet.Y > FIELD_H)
            {
                bullets.TryRemove(bid, out _);
                changed = true;
                continue;
            }

            foreach (var (pid, player) in players)
            {
                if (pid == bullet.OwnerId) continue;

                float dist = MathF.Sqrt(
                    MathF.Pow(bullet.X - player.X, 2) +
                    MathF.Pow(bullet.Y - player.Y, 2));

                if (dist < PLAYER_SIZE)
                {
                    Console.WriteLine($"Player {bullet.OwnerId} hit Player {pid}!");

                    if (players.TryGetValue(bullet.OwnerId, out var shooter))
                        shooter.Score++;

                    player.X = rng.Next(50, FIELD_W - 50);
                    player.Y = rng.Next(50, FIELD_H - 50);

                    bullets.TryRemove(bid, out _);
                    changed = true;
                    break;
                }
            }
        }

        BroadcastGameObjects();
        if (changed) BroadcastState();
    }

    static void SpawnCoin()
    {
        int id = Interlocked.Increment(ref nextCoinId);
        coins[id] = new CoinInfo
        {
            Id = id,
            X  = rng.Next(20, FIELD_W - 20),
            Y  = rng.Next(20, FIELD_H - 20)
        };
    }

    static void CheckCoinCollisions(PlayerInfo player)
    {
        foreach (var (cid, coin) in coins)
        {
            float dist = MathF.Sqrt(
                MathF.Pow(player.X - coin.X, 2) +
                MathF.Pow(player.Y - coin.Y, 2));

            if (dist < (PLAYER_SIZE / 2f + COIN_SIZE / 2f))
            {
                player.Score++;
                coins.TryRemove(cid, out _);
                if (coins.Count < MAX_COINS) SpawnCoin();
                Console.WriteLine($"Player {player.Id} got a coin! Score={player.Score}");
                BroadcastGameObjects();
                break;
            }
        }
    }

    static void BroadcastState()
    {
        BroadcastToAll(new { Type = "state", Players = players.Values.ToArray() });
    }

    static void BroadcastGameObjects()
    {
        BroadcastToAll(new
        {
            Type    = "gameobjects",
            Bullets = bullets.Values.ToArray(),
            Coins   = coins.Values.ToArray()
        });
    }

    static void BroadcastToAll(object message)
    {
        string json = JsonSerializer.Serialize(message);
        byte[] data = Encoding.UTF8.GetBytes(json + "\n");

        lock (broadcastLock)
        {
            foreach (var (_, stream) in streams)
            {
                try { stream.Write(data, 0, data.Length); }
                catch { }
            }
        }
    }

    static void SendTo(NetworkStream stream, object message)
    {
        string json = JsonSerializer.Serialize(message);
        byte[] data = Encoding.UTF8.GetBytes(json + "\n");
        stream.Write(data, 0, data.Length);
    }
}